using System.Text;
using Microsoft.Data.Sqlite;
using Glink;
using Glink.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Graphics.Imaging;

// Run: dotnet run --project Glink.ImportTests
var testRoot = Path.Combine(Path.GetTempPath(), "Glink-import-tests-" + Guid.NewGuid());
Directory.CreateDirectory(testRoot);
Environment.SetEnvironmentVariable("GLINK_DATA_ROOT", Path.Combine(testRoot, "library"));
var sandbox = Path.Combine(testRoot, "sandbox");
var categoryId = Guid.NewGuid();
var tests = 0;
try
{
    await using var database = new SqliteConnection($"Data Source={Path.Combine(testRoot, "test.db")};Pooling=False");
    await database.OpenAsync();
    await using (var create = database.CreateCommand())
    {
        create.CommandText = "CREATE TABLE imported (Id TEXT PRIMARY KEY, Type INTEGER, Content TEXT, FilePath TEXT, CategoryId TEXT)";
        await create.ExecuteNonQueryAsync();
    }
    async Task Persist(Item item)
    {
        await using var insert = database.CreateCommand();
        insert.CommandText = "INSERT INTO imported VALUES (@id, @type, @content, @path, @category)";
        insert.Parameters.AddWithValue("@id", item.Id.ToString());
        insert.Parameters.AddWithValue("@type", (int)item.Type);
        insert.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
        insert.Parameters.AddWithValue("@path", (object?)item.FilePath ?? DBNull.Value);
        insert.Parameters.AddWithValue("@category", item.CategoryId?.ToString() ?? (object)DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }
    var importer = new ExternalDropImporter(
        (file, category) => Store.AddFileAsync(file, category, sandbox, Persist),
        async (text, category) =>
        {
            var item = new Item { Id = Guid.NewGuid(), Type = ItemType.Text, Content = text, CategoryId = category };
            await Persist(item);
            return item;
        });

    void Check(bool success, string name)
    {
        if (!success) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        tests++;
    }
    async Task<StorageFile> Fixture(string name, byte[] bytes)
    {
        var path = Path.Combine(testRoot, name);
        await File.WriteAllBytesAsync(path, bytes);
        return await StorageFile.GetFileFromPathAsync(path);
    }
    async Task<ImportResult> DropFiles(params IStorageItem[] files)
    {
        var package = new DataPackage();
        package.SetStorageItems(files);
        return await importer.ImportAsync(package.GetView(), categoryId);
    }
    async Task<ImportResult> DropText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        return await importer.ImportAsync(package.GetView(), categoryId);
    }

    var textFile = await Fixture("灵感.MARKDOWN", Encoding.UTF8.GetBytes("中文 inspiration\n\n日本語 🎵"));
    var imageFile = await Fixture("image.PNG", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cS8AAAAASUVORK5CYII="));
    // Media is copied byte-for-byte; codec playback is outside the importer contract.
    var audioFile = await Fixture("audio.aac", [0xFF, 0xF1, 0x50, 0x80, 1, 2, 3]);
    var videoFile = await Fixture("video.AVI", Encoding.ASCII.GetBytes("RIFF-test-AVI"));
    var batch = await DropFiles(textFile, imageFile, audioFile, videoFile);
    Check(batch.Items.Select(i => i.Type).SequenceEqual(new[] { ItemType.Text, ItemType.Image, ItemType.Audio, ItemType.Video }), "Four object types including AAC/AVI and uppercase extensions");
    Check(batch.Failures.Count == 0 && batch.Items.All(i => i.CategoryId == categoryId), "Batch destination is preserved");
    Check(batch.Items[0].Content == "中文 inspiration\n\n日本語 🎵" && batch.Items[0].FilePath == null, "Text content preserves Unicode and paragraphs");
    foreach (var item in batch.Items.Where(i => i.FilePath != null))
    {
        var original = Path.Combine(testRoot, item.FileName!);
        var copy = Path.Combine(sandbox, item.FilePath!);
        Check(File.Exists(original) && File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(copy)) && new FileInfo(copy).Length == item.SizeBytes,
            $"Sandbox copy preserves source and bytes: {item.Type}");
    }

    var realFixtures = new List<IStorageItem>();
    foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures")))
        realFixtures.Add(await StorageFile.GetFileFromPathAsync(path));
    var realBatch = await importer.ImportFilesAsync(realFixtures, categoryId);
    Check(realBatch.Items.Count == 4 && realBatch.Items.Select(item => item.Type).Distinct().Count() == 4,
        "Real TXT/PNG/WAV/MP4 files import as four distinct object types");
    foreach (var item in realBatch.Items.Where(item => item.FilePath != null))
    {
        var copy = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(Path.Combine(sandbox, item.FilePath!)));
        if (item.Type == ItemType.Image)
        {
            using var stream = await copy.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Check(decoder.PixelWidth == 640 && decoder.PixelHeight == 360, "Imported PNG remains decodable");
        }
        else if (item.Type == ItemType.Audio)
            Check((await copy.Properties.GetMusicPropertiesAsync()).Duration.TotalSeconds > 0, "Imported WAV retains readable duration");
        else
            Check((await copy.Properties.GetVideoPropertiesAsync()).Duration.TotalSeconds > 0, "Imported MP4 retains readable duration");
    }
    Check(Store.TryGetFileType("clip.WMV", out var wmv) && wmv == ItemType.Video &&
          Store.TryGetFileType("track.opus", out var opus) && opus == ItemType.Audio &&
          Store.TryGetFileType("data.csv", out var csv) && csv == ItemType.Text, "Previously inconsistent formats agree");
    Check(Store.SupportedFileExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == Store.SupportedFileExtensions.Count &&
          Store.SupportedFileExtensions.All(ext => Store.TryGetFileType("sample" + ext, out _)), "Every picker extension has a type");

    foreach (var (name, encoding) in new (string, Encoding)[] {
        ("utf8-bom", new UTF8Encoding(true)), ("utf16-le", Encoding.Unicode),
        ("utf16-be", Encoding.BigEndianUnicode), ("utf32", Encoding.UTF32),
        ("gb18030", CodePagesEncodingProvider.Instance.GetEncoding(54936)!) })
    {
        var content = "中文文本\r\n第二行";
        var file = await Fixture(name + ".txt", encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray());
        var result = await DropFiles(file);
        Check(result.Items.Single().Content == content, "Text decoding: " + name);
    }

    var unsupported = await Fixture("archive.zip", [0, 1, 2]);
    var invalid = await Fixture("binary.txt", [65, 0, 66, 0]);
    var folderPath = Path.Combine(testRoot, "folder");
    Directory.CreateDirectory(folderPath);
    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
    var mixed = await DropFiles(invalid, unsupported, folder, imageFile, textFile);
    Check(mixed.Items.Count == 2 && mixed.Failures.Count == 1 && mixed.Skipped.Count == 2, "A failed file and unsupported entries do not stop later files");

    var both = new DataPackage();
    both.SetStorageItems([unsupported]);
    both.SetText(unsupported.Path);
    var priority = await importer.ImportAsync(both.GetView(), categoryId);
    Check(priority.Items.Count == 0 && priority.Skipped.Count == 1, "Skipped StorageItems never fall back to path-text cards");

    var inaccessible = new DataPackage();
    inaccessible.SetDataProvider(StandardDataFormats.StorageItems, request =>
        request.SetData("Invalid storage payload"));
    inaccessible.SetText(unsupported.Path);
    var unavailable = await importer.ImportAsync(inaccessible.GetView(), categoryId);
    Check(unavailable.Items.Count == 0 && unavailable.Failures.Count == 1,
        "Unavailable storage payload reports failure without text fallback");

    var delayed = new DataPackage();
    delayed.SetDataProvider(StandardDataFormats.Text, async request =>
    {
        var deferral = request.GetDeferral();
        try
        {
            await Task.Delay(30);
            request.SetData("延迟提供的外部文本");
        }
        finally { deferral.Complete(); }
    });
    Check((await importer.ImportAsync(delayed.GetView(), categoryId)).Items.Single().Content == "延迟提供的外部文本",
        "Delayed external data providers are awaited");

    var rejectsUnknown = false;
    try { await Store.AddFileAsync(unsupported, categoryId, sandbox, Persist); }
    catch (NotSupportedException) { rejectsUnknown = true; }
    Check(rejectsUnknown, "Store also rejects unsupported files when called directly");

    var pureText = "  第一行\r\n\r\n第二行  \n";
    Check((await DropText(pureText)).Items.Single().Content == pureText, "Multiline plain text creates one unmodified object");
    Check((await DropText(" \r\n\t")).Items.Count == 0, "Whitespace creates no object");
    Check((await DropText($"\"{imageFile.Path}\"\r\n{audioFile.Path}")).Items.Count == 2, "Quoted and multiline file paths import files");
    Check((await DropText(new Uri(imageFile.Path).AbsoluteUri)).Items.Single().Type == ItemType.Image, "File URI text is decoded as a file");
    Check((await DropText(imageFile.Path.Replace('\\', '/'))).Items.Single().Type == ItemType.Image,
        "Forward-slash Windows paths are normalized for WinRT");
    Check((await DropText(Path.Combine(testRoot, "missing.png"))).Failures.Count == 1, "Missing paths report failure rather than becoming text");
    var mixedText = "Keep this reference:\n" + imageFile.Path;
    Check((await DropText(mixedText)).Items.Single().Content == mixedText, "Prose containing a file path stays intact");

    var link = new DataPackage();
    link.SetWebLink(new Uri("https://example.com/inspiration"));
    Check((await importer.ImportAsync(link.GetView(), null)).Items.Single().Content == "https://example.com/inspiration", "Web links are stored as text");
    var fileLink = new DataPackage();
    fileLink.SetUri(new Uri(videoFile.Path));
    Check((await importer.ImportAsync(fileLink.GetView(), null)).Items.Single().Type == ItemType.Video, "Legacy file URI imports media");

    var emptyFile = await Fixture("empty.txt", []);
    Check((await DropFiles(emptyFile)).Failures.Count == 1, "Empty text file reports a readable failure");
    var largePath = Path.Combine(testRoot, "large.txt");
    using (var stream = File.Create(largePath)) stream.SetLength(16 * 1024 * 1024 + 1);
    Check((await DropFiles(await StorageFile.GetFileFromPathAsync(largePath))).Failures.Count == 1, "Oversized text is rejected before loading");

    var mediaBefore = Directory.GetFiles(Path.Combine(sandbox, "media")).Length;
    var failedPersistence = false;
    try
    {
        await Store.AddFileAsync(imageFile, null, sandbox, _ => throw new IOException("Simulated database failure"));
    }
    catch (IOException) { failedPersistence = true; }
    Check(failedPersistence && Directory.GetFiles(Path.Combine(sandbox, "media")).Length == mediaBefore,
        "Database failure cleans up sandbox media");

    var failOnce = true;
    var recoverableImporter = new ExternalDropImporter((file, category) =>
        Store.AddFileAsync(file, category, sandbox, async item =>
        {
            if (failOnce) { failOnce = false; throw new IOException("Simulated first write failure"); }
            await Persist(item);
        }));
    var recovered = await recoverableImporter.ImportFilesAsync(new[] { imageFile, audioFile }, categoryId);
    Check(recovered.Failures.Count == 1 && recovered.Items.Single().Type == ItemType.Audio,
        "A failed save does not prevent the next file from being persisted");

    var duplicate = await DropFiles(imageFile, imageFile);
    Check(duplicate.Items.Select(i => i.FilePath).Distinct().Count() == 2, "Same-name files receive independent sandbox paths");
    await using var query = database.CreateCommand();
    query.CommandText = "SELECT COUNT(*) FROM imported WHERE Type IN (0,1,2,3)";
    Check((long)(await query.ExecuteScalarAsync())! >= 4, "Imported records persist in isolated SQLite database");
    await Store.InitializeAsync();
    var editable = await Store.AddTextAsync("Original", null);
    await Store.SetItemPositionAsync(editable.Id, 120, 240, 5);
    await Store.ToggleFavoriteAsync(editable.Id);
    await Store.MoveItemAsync(editable.Id, categoryId);
    var editedText = "  保留前后空格\n\n日本語 🎵  ";
    await Store.UpdateTextAsync(editable.Id, editedText);
    var saved = (await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == editable.Id);
    Check(saved.Content == editedText, "Inline edit persists exact Unicode, whitespace and paragraphs");
    Check(saved.CategoryId == categoryId && saved.IsFavorite && saved.PosX == 120 && saved.PosY == 240 && saved.ZIndex == 5,
        "Text-only save preserves concurrent category, favorite and canvas position changes");
    await Store.UpdateTextAsync(editable.Id, "");
    Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == editable.Id).Content == "",
        "Clearing an existing text object persists an empty value");
    var missingEditFailed = false;
    try { await Store.UpdateTextAsync(Guid.NewGuid(), "Draft"); }
    catch (InvalidOperationException) { missingEditFailed = true; }
    Check(missingEditFailed, "Saving a deleted text object reports failure instead of silently discarding the draft");
    var category = await Store.AddCategoryAsync("颜色与撤回", "#E8956B");
    await Store.SetCategoryColorAsync(category.Id, "#7cace2");
    Check((await Store.GetCategoriesAsync()).Single(c => c.Id == category.Id).AccentHex == "#7CACE2", "Category colors persist in normalized form");
    var invalidColorRejected = false;
    try { await Store.SetCategoryColorAsync(category.Id, "invalid"); }
    catch (ArgumentException) { invalidColorRejected = true; }
    Check(invalidColorRejected, "Malformed category colors are rejected before storage");
    await Store.MoveItemAsync(editable.Id, category.Id);
    await Store.DeleteCategoryAsync(category.Id);
    Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == editable.Id).CategoryId == null, "Category deletion uncategorizes its contents");
    category.AccentHex = "#7CACE2";
    await Store.RestoreCategoryAsync(category, new[] { editable.Id });
    Check((await Store.GetItemsAsync(category.Id, null, null, false)).Single().Id == editable.Id &&
        (await Store.GetCategoriesAsync()).Single(c => c.Id == category.Id).AccentHex == "#7CACE2", "Undo restores category identity, color and membership");
    Check((await Store.GetItemsAsync(null, null, null, true)).All(i => i.IsFavorite) && await Store.GetItemCountAsync("favorites") == 1,
        "Favorites collection and sidebar count match persisted favorites");
    await Store.SetItemPositionAsync(editable.Id, null, null, 0);
    Check((await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == editable.Id).PosX == null, "Undo can restore an unset canvas position");
    var retainedItem = await Store.AddFileAsync(imageFile, category.Id);
    await Store.ToggleFavoriteAsync(retainedItem.Id);
    retainedItem.IsFavorite = true;
    var originalBytes = await File.ReadAllBytesAsync(Store.AbsolutePath(retainedItem.FilePath!));
    using (var archive = new Store.ItemArchive(retainedItem))
    {
        await archive.RemoveAsync();
        Check(!File.Exists(Store.AbsolutePath(retainedItem.FilePath!)) && !(await Store.GetItemsAsync(null, null, null, false)).Any(i => i.Id == retainedItem.Id),
            "Undoable deletion removes the item and retains media outside the library");
        await archive.RestoreAsync();
        var restored = (await Store.GetItemsAsync(null, null, null, false)).Single(i => i.Id == retainedItem.Id);
        var restoredBytes = await File.ReadAllBytesAsync(Store.AbsolutePath(restored.FilePath!));
        Check(restored.IsFavorite && restored.CategoryId == category.Id && originalBytes.SequenceEqual(restoredBytes),
            "Undo restores original media bytes, category and favorite state");
        await archive.RemoveAsync(); await archive.RestoreAsync();
        Check(File.Exists(Store.AbsolutePath(retainedItem.FilePath!)), "Media delete and restore supports repeated undo/redo");
    }
    Check(File.Exists(Store.AbsolutePath(retainedItem.FilePath!)), "Discarding history never deletes restored media");
    using (var history = new UndoHistory())
    {
        var value = 1;
        history.Record("first", () => { value = 0; return Task.CompletedTask; }, () => { value = 1; return Task.CompletedTask; });
        await history.UndoAsync();
        Check(value == 0 && history.CanRedo && !history.CanUndo, "Undo transfers the operation to redo");
        await history.RedoAsync();
        Check(value == 1 && history.CanUndo, "Redo reapplies the operation");
        await history.UndoAsync();
        history.Record("branch", () => Task.CompletedTask, () => Task.CompletedTask);
        Check(!history.CanRedo, "A new edit clears the obsolete redo branch");
        history.Record("failure", () => throw new IOException("Simulated undo failure"), () => Task.CompletedTask);
        try { await history.UndoAsync(); } catch (IOException) { }
        Check(history.CanUndo && history.UndoName == "failure" && !history.IsBusy, "Failed undo remains available and releases the busy guard");
    }
    // —— 向心紧凑平铺（RadialPackingCore，纯几何回归）——
    void CheckPacking(string name, IReadOnlyList<RadialPackingCore.Item> items, double targetWidth, double gap = 12.0,
        double minFill = 0.55)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var placed = RadialPackingCore.Pack(items, targetWidth, gap);
        stopwatch.Stop();
        Check(placed.Count == items.Count && placed.Select(p => p.Index).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, items.Count)),
            name + " places every card exactly once");
        var first = placed[0];
        var largest = Enumerable.Range(0, items.Count).OrderByDescending(i => items[i].Width * items[i].Height).ThenBy(i => i).First();
        Check(first.Index == largest, name + " first result is the largest card");
        Check(Math.Abs(first.X + items[first.Index].Width / 2) < 1 && Math.Abs(first.Y + items[first.Index].Height / 2) < 1,
            name + " anchor card is centered at origin");
        var boxes = placed.Select(p => (L: p.X, T: p.Y,
            R: p.X + items[p.Index].Width + gap, B: p.Y + items[p.Index].Height + gap)).ToArray();
        var overlaps = 0;
        for (var i = 0; i < boxes.Length; i++)
            for (var j = i + 1; j < boxes.Length; j++)
                if (boxes[i].L < boxes[j].R && boxes[i].R > boxes[j].L && boxes[i].T < boxes[j].B && boxes[i].B > boxes[j].T)
                    overlaps++;
        Check(overlaps == 0, name + " keeps zero overlap with at least gap clearance");
        var minX = boxes.Min(b => b.L); var maxX = boxes.Max(b => b.R);
        var minY = boxes.Min(b => b.T); var maxY = boxes.Max(b => b.B);
        var boundsWidth = maxX - minX; var boundsHeight = maxY - minY;
        var diagonal = Math.Sqrt(boundsWidth * boundsWidth + boundsHeight * boundsHeight);
        var offset = Math.Sqrt(Math.Pow((minX + maxX) / 2, 2) + Math.Pow((minY + maxY) / 2, 2));
        Check(offset <= diagonal * 0.15 + 1e-9,
            $"{name} content stays centered on the anchor (offset {offset:F0} of {diagonal:F0})");
        var fill = items.Sum(i => (i.Width + gap) * (i.Height + gap)) / (boundsWidth * boundsHeight);
        Check(fill >= minFill, $"{name} packs densely (fill {fill:P0}, floor {minFill:P0})");
        Check(stopwatch.ElapsedMilliseconds < 2000, $"{name} packs fast ({stopwatch.ElapsedMilliseconds} ms)");
    }

    CheckPacking("Single card", [new RadialPackingCore.Item(360, 240)], 900);
    CheckPacking("Equal cards", Enumerable.Repeat(new RadialPackingCore.Item(200, 150), 8).ToList(), 900);
    CheckPacking("Tall narrow viewport", Enumerable.Repeat(new RadialPackingCore.Item(200, 150), 8).ToList(), 240);
    // 旧实现在此场景（细长卡宽于目标容器）会无限放大容器重试、永不返回；密度上限本身只有 ~50%。
    CheckPacking("Wider-than-bin card", [new RadialPackingCore.Item(360, 500), new RadialPackingCore.Item(900, 80)], 600,
        minFill: 0.40);

    var random = new Random(42);
    RadialPackingCore.Item RandomCard()
    {
        // 与画布真实卡片同量级的混合尺寸：文本 360×(60..320)、图片 360×(200..520)、视频 480×300。
        var roll = random.Next(3);
        return roll switch
        {
            0 => new RadialPackingCore.Item(360, 60 + random.Next(260)),
            1 => new RadialPackingCore.Item(360, 200 + random.Next(320)),
            _ => new RadialPackingCore.Item(480, 300)
        };
    }
    CheckPacking("Glink card mix", Enumerable.Range(0, 40).Select(_ => RandomCard()).ToList(), 900);
    CheckPacking("Large canvas", Enumerable.Range(0, 300).Select(_ => RandomCard()).ToList(), 900);

    // ==================== 边缘调整大小几何核心（ResizeGeometry） ====================
    void CheckResize(bool ok, string name) => Check(ok, "Resize: " + name);
    // 光标映射：8 向把手 → 标准系统调整光标
    CheckResize(ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.West) == ResizeGeometry.IdcSizewe &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.East) == ResizeGeometry.IdcSizewe &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.North) == ResizeGeometry.IdcSizens &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.South) == ResizeGeometry.IdcSizens &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.West | ResizeGeometry.Edge.North) == ResizeGeometry.IdcSizenwse &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.East | ResizeGeometry.Edge.South) == ResizeGeometry.IdcSizenwse &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.East | ResizeGeometry.Edge.North) == ResizeGeometry.IdcSizenesw &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.West | ResizeGeometry.Edge.South) == ResizeGeometry.IdcSizenesw &&
                ResizeGeometry.CursorIdFor(ResizeGeometry.Edge.None) == 0, "cursor map");
    // 能力矩阵：图片/视频 8 向（视频放大按钮区单独让位）；文本/音频仅左右
    CheckResize(ResizeGeometry.CapabilitiesOf(ItemType.Image) == (ResizeGeometry.Edge.West | ResizeGeometry.Edge.East | ResizeGeometry.Edge.North | ResizeGeometry.Edge.South) &&
                ResizeGeometry.CapabilitiesOf(ItemType.Video) == (ResizeGeometry.Edge.West | ResizeGeometry.Edge.East | ResizeGeometry.Edge.North | ResizeGeometry.Edge.South) &&
                ResizeGeometry.CapabilitiesOf(ItemType.Text) == (ResizeGeometry.Edge.West | ResizeGeometry.Edge.East) &&
                ResizeGeometry.CapabilitiesOf(ItemType.Audio) == (ResizeGeometry.Edge.West | ResizeGeometry.Edge.East), "capabilities");
    // 环宽：屏幕恒 ~12 DIP，深度缩小补偿，边长 1/3 钳制防重叠
    CheckResize(Math.Abs(ResizeGeometry.BandWidth(0.5, 360) - 24.0) < 1e-9 &&
                Math.Abs(ResizeGeometry.BandWidth(1.0, 360) - 12.0) < 1e-9 &&
                Math.Abs(ResizeGeometry.BandWidth(2.0, 36) - 6.0) < 1e-9 &&
                Math.Abs(ResizeGeometry.BandWidth(0.25, 60) - 20.0) < 1e-9, "band width");
    // 图片会话：比率 1.5、内容满卡（OverheadW=2、上下边框 OverheadH=2）
    var img = new ResizeGeometry.Session { Handle = ResizeGeometry.Edge.East, Aspect = true, StartX = 100, StartY = 50, StartW = 360, StartH = 242, Ratio = 1.5, OverheadW = 2, OverheadH = 2 };
    // E：被拖边跟随指针，左/上锚定
    var r1 = ResizeGeometry.Apply(img, 500, 171);
    CheckResize(Math.Abs(r1.Width - 400) < 1e-6 && Math.Abs(r1.Left - 100) < 1e-6 && Math.Abs(r1.Top - 50) < 1e-6, "E follows pointer");
    // E 高度联动：与 ApplyCardWidthToContent 的照片换算口径一致
    CheckResize(Math.Abs(ResizeGeometry.HeightForWidth(img, 400) - ((400 - 2) / 1.5 + 2)) < 1e-6, "E ratio height");
    // W：右缘锚定、向左生长
    var imgW = img with { Handle = ResizeGeometry.Edge.West };
    var r2 = ResizeGeometry.Apply(imgW, 40, 171);
    CheckResize(Math.Abs(r2.Width - 420) < 1e-6 && Math.Abs(r2.Left - 40) < 1e-6, "W anchors right");
    // W 越过画布原点：left 地板 0
    var r3 = ResizeGeometry.Apply(imgW, -50, 171);
    CheckResize(Math.Abs(r3.Width - 460) < 1e-6 && Math.Abs(r3.Left) < 1e-6, "W floor at origin");
    // W 向内拖穿对边：最小宽钳制、锚定边保持
    var r4 = ResizeGeometry.Apply(imgW, 400, 171);
    CheckResize(Math.Abs(r4.Width - 150) < 1e-6 && Math.Abs(r4.Left - 310) < 1e-6, "W min clamp");
    // E 超最大宽钳制
    var r5 = ResizeGeometry.Apply(img, 2000, 171);
    CheckResize(Math.Abs(r5.Width - 800) < 1e-6 && Math.Abs(r5.Left - 100) < 1e-6, "E max clamp");
    // N：高度驱动宽随比率、底缘锚定（上缘跟随指针）
    var imgN = img with { Handle = ResizeGeometry.Edge.North };
    var r6 = ResizeGeometry.Apply(imgN, 280, 20);
    CheckResize(Math.Abs(r6.Width - 407) < 1e-6 && Math.Abs(r6.Top - 20) < 1e-6, "N bottom anchor");
    // N 越过画布原点：top 地板 0（宽随高度上限收缩）
    var imgN2 = new ResizeGeometry.Session { Handle = ResizeGeometry.Edge.North, Aspect = true, StartX = 0, StartY = 20, StartW = 300, StartH = 100, Ratio = 1.5, OverheadW = 2, OverheadH = 2 };
    var r7 = ResizeGeometry.Apply(imgN2, 150, -200);
    CheckResize(Math.Abs(r7.Top) < 1e-6 && Math.Abs(r7.Width - ((20 + 100 - 2) * 1.5 + 2)) < 1e-6, "N floor at origin");
    // 四角主导轴：水平主导（|ΔW| 更大者胜）
    var imgNW = img with { Handle = ResizeGeometry.Edge.West | ResizeGeometry.Edge.North };
    var r8 = ResizeGeometry.Apply(imgNW, 60, 40);
    CheckResize(Math.Abs(r8.Width - 400) < 1e-6 && Math.Abs(r8.Left - 60) < 1e-6, "NW dominant horizontal");
    // 四角主导轴：垂直主导 + 上缘地板
    var r9 = ResizeGeometry.Apply(imgNW, 85, 0);
    CheckResize(Math.Abs(r9.Width - 437) < 1e-6 && Math.Abs(r9.Top) < 1e-6, "NW dominant vertical");
    // 视频会话：画面表面高度钳制 [140,760]
    var vid = new ResizeGeometry.Session { Handle = ResizeGeometry.Edge.East, Aspect = true, StartX = 0, StartY = 0, StartW = 480, StartH = 349, Ratio = 16.0 / 9, OverheadW = 2, OverheadH = 70, ContentMinH = 140, ContentMaxH = 760 };
    var r10 = ResizeGeometry.Apply(vid, 1500, 174);
    CheckResize(Math.Abs(r10.Width - 800) < 1e-6, "video E max clamp");
    CheckResize(Math.Abs(ResizeGeometry.HeightForWidth(vid, 800) - (Math.Min((800 - 2) / (16.0 / 9), 760) + 70)) < 1e-6, "video surface clamp");
    // 宽高换算往返一致
    CheckResize(Math.Abs(ResizeGeometry.WidthForHeight(vid, ResizeGeometry.HeightForWidth(vid, 600)) - 600) < 1e-6, "W/H roundtrip");
    // 文本/音频（非等比）：仅调宽，高度不动
    var txt = new ResizeGeometry.Session { Handle = ResizeGeometry.Edge.West, Aspect = false, StartX = 100, StartY = 50, StartW = 360, StartH = 116, Ratio = 1, OverheadW = 0, OverheadH = 0 };
    var r11 = ResizeGeometry.Apply(txt, 20, 108);
    CheckResize(Math.Abs(r11.Width - 440) < 1e-6 && Math.Abs(r11.Left - 20) < 1e-6 && Math.Abs(r11.Top - 50) < 1e-6, "text W width-only");

    await Store.InitializeAsync();
    foreach (var seededCategory in await Store.GetCategoriesAsync()) await Store.DeleteCategoryAsync(seededCategory.Id);
    await Store.InitializeAsync();
    Check((await Store.GetCategoriesAsync()).Count == 0, "Deleted default category stays deleted across restarts");
    await ExportChecks.RunAsync(testRoot, Check);
    Console.WriteLine($"All {tests} regression checks passed.");
}
finally
{
    // Only this run's freshly created temporary tree is removed.
    SqliteConnection.ClearAllPools();
    Directory.Delete(testRoot, recursive: true);
}
