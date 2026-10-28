using Glink.Core;

internal static class ExportChecks
{
    private sealed class InlineProgress(Action<ExportProgress> callback) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => callback(value);
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var output = Path.Combine(root, "exports");
        var library = Path.Combine(root, "export-library");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(library);
        var categories = new[]
        {
            new MoodCategory { Id = Guid.NewGuid(), Name = "旅行/观察" },
            new MoodCategory { Id = Guid.NewGuid(), Name = "旅行\\观察" },
            new MoodCategory { Id = Guid.NewGuid(), Name = "CON" }
        };
        var folders = new[] { new ExportFolder(Guid.NewGuid(), "周末:收藏", categories.Take(2).Select(c => c.Id).ToArray()) };
        var date = new DateTime(2026, 10, 27, 8, 0, 0);
        var items = new List<Item>
        {
            new() { Id = Guid.NewGuid(), Type = ItemType.Text, Content = "  中文\n\n日本語 🎵  ", CreatedAt = date, CategoryId = categories[0].Id },
            new() { Id = Guid.NewGuid(), Type = ItemType.Text, Content = "稍后的灵感", CreatedAt = date.AddMinutes(1) },
            new() { Id = Guid.NewGuid(), Type = ItemType.Text, Content = "", CreatedAt = date.AddMinutes(2), CategoryId = categories[1].Id }
        };
        foreach (var (type, ext) in new[] { (ItemType.Image, ".png"), (ItemType.Audio, ".wav"), (ItemType.Video, ".mp4") })
        {
            var file = "original" + ext;
            await File.WriteAllBytesAsync(Path.Combine(library, file), [1, 3, 5, 7, (byte)type]);
            items.Add(new Item { Id = Guid.NewGuid(), Type = type, FileName = "../同名" + ext, FilePath = file,
                CreatedAt = date.AddMinutes(3 + (int)type), CategoryId = categories[(int)type - 1].Id });
        }
        items.Add(new Item { Id = Guid.NewGuid(), Type = ItemType.Image, FileName = "同名.png", FilePath = "original.png", CreatedAt = date.AddDays(1), CategoryId = categories[1].Id });
        var all = new ExportRequest(ExportScope.All, [], [], []);
        var selected = ExportService.Select(items.AsEnumerable().Reverse(), all, folders);
        check(selected.Count == 7 && selected[0].Content == items[0].Content, "Export all is chronological and ignores input ordering");
        var types = new ExportRequest(ExportScope.Type, [ItemType.Text, ItemType.Image], [], []);
        check(ExportService.Select(items, types, folders).Count == 5, "Export selects multiple types");
        var byCategory = new ExportRequest(ExportScope.Category, [], [categories[0].Id, null], []);
        check(ExportService.Select(items, byCategory, folders).Count == 3, "Export selects emotion categories including unclassified");
        var byFolder = new ExportRequest(ExportScope.Folder, [], [], [folders[0].Id]);
        check(ExportService.Select(items, byFolder, folders).Count == 5, "Export folder includes all child categories only");
        var first = await ExportService.ExportAsync(output, library + Path.DirectorySeparatorChar, items, categories, folders, all);
        var textPath = Path.Combine(first.Directory, "灵感文本.txt");
        var text = await File.ReadAllTextAsync(textPath);
        check(first.TextCount == 3 && first.MediaCount == 4 && first.Failures.Count == 0, "Export counts text and media correctly");
        check(text.Contains("[2026-10-27 08:00:00] · 旅行/观察") && text.Contains(items[0].Content!) && text.IndexOf(items[0].Content!, StringComparison.Ordinal) < text.IndexOf("稍后的灵感", StringComparison.Ordinal), "Export one UTF-8 text file preserves timestamps, Unicode, whitespace and ordering");
        foreach (var file in Directory.GetFiles(first.Directory).Where(f => Path.GetExtension(f) != ".txt"))
            check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(library, "original" + Path.GetExtension(file)))), "Export preserves original media bytes: " + Path.GetFileName(file));
        check(Directory.GetFiles(first.Directory, "*.png").Length == 2, "Export preserves duplicate original filenames without overwriting");
        var again = await ExportService.ExportAsync(output, library, items, categories, folders, all);
        check(again.Directory != first.Directory && File.Exists(textPath), "Repeated exports preserve existing results");
        var grouped = await ExportService.ExportAsync(output, library, items, categories, folders, byFolder);
        check(Directory.GetDirectories(Path.Combine(grouped.Directory, "周末_收藏")).Length == 2 && Directory.GetFiles(grouped.Directory, "灵感文本.txt", SearchOption.AllDirectories).Length == 1, "Folder export preserves hierarchy, handles colliding names, and merges all selected texts once");
        var typeResult = await ExportService.ExportAsync(output, library, items, categories, folders, new ExportRequest(ExportScope.Type, [ItemType.Image], [], []));
        check(!File.Exists(Path.Combine(typeResult.Directory, "灵感文本.txt")) && Directory.GetFiles(Path.Combine(typeResult.Directory, "图片")).Length == 2, "Image-only export creates no empty text file");
        var categoryResult = await ExportService.ExportAsync(output, library, items, categories, folders, new ExportRequest(ExportScope.Category, [], [categories[2].Id], []));
        check(Directory.Exists(Path.Combine(categoryResult.Directory, "_CON")), "Reserved Windows folder names are safe");
        var bad = new[]
        {
            new Item { Id = Guid.NewGuid(), Type = ItemType.Image, FilePath = "missing.png", FileName = "缺失.png" },
            new Item { Id = Guid.NewGuid(), Type = ItemType.Image, FilePath = "../outside.png", FileName = "越界.png" },
            new Item { Id = Guid.NewGuid(), Type = ItemType.Image, FilePath = Path.Combine(library, "original.png"), FileName = "绝对路径.png" }
        };
        var incomplete = await ExportService.ExportAsync(output, library, bad, categories, folders, all);
        check(incomplete.MediaCount == 0 && incomplete.Failures.Count == 3 && File.Exists(Path.Combine(incomplete.Directory, "未导出文件.txt")), "Missing and unsafe media paths are reported without silently succeeding");
        using var cancellation = new CancellationTokenSource();
        var before = Directory.GetDirectories(output).Length;
        cancellation.Cancel();
        try { await ExportService.ExportAsync(output, library, items, categories, folders, all, cancellationToken: cancellation.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { check(Directory.GetDirectories(output).Length == before, "Cancelled export removes its staging directory"); }
        using var duringExport = new CancellationTokenSource();
        var reports = 0;
        try
        {
            await ExportService.ExportAsync(output, library, items, categories, folders, all,
                new InlineProgress(_ => { reports++; duringExport.Cancel(); }), duringExport.Token);
            throw new Exception("Cancellation during export was ignored");
        }
        catch (OperationCanceledException)
        {
            check(reports > 0 && Directory.GetDirectories(output).Length == before, "Cancellation during export cleans up all partial files");
        }
        try { await ExportService.ExportAsync(output, library, items, categories, folders, new ExportRequest(ExportScope.Type, [], [], [])); throw new Exception("Empty selection was accepted"); }
        catch (InvalidOperationException) { check(Directory.GetDirectories(output).Length == before, "Empty export creates no files"); }
        check(!Directory.GetDirectories(output, "*.partial").Any(), "No partial export remains after checks");
    }
}
