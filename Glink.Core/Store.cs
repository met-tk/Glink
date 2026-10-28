using Microsoft.Data.Sqlite;
using System.Text;
using Windows.Storage;

namespace Glink.Core;

public static partial class Store
{
    //% LOCALAPPDATA%\Glink\
    // An explicit override keeps development/interaction tests separate from the user's library.
    private static string? s_root;
    private static bool _rootReady;
    private static readonly object RootGate = new();

    /// <summary>
    /// 数据根目录。首次取用时若存在旧版 Moodia 目录而 Glink 目录不存在，
    /// 整体搬移一次（数据库/媒体/旁路文件原子迁移，库内相对路径不受影响）；
    /// 搬移失败则继续用旧目录，保证可用性。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (_rootReady) return s_root!;
            lock (RootGate)
            {
                if (_rootReady) return s_root!;
                var root = Environment.GetEnvironmentVariable("GLINK_DATA_ROOT") is { Length: > 0 } directory
                    ? Path.GetFullPath(directory)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glink");
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLINK_DATA_ROOT")))
                {
                    var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moodia");
                    try
                    {
                        if (!Directory.Exists(root) && Directory.Exists(legacy))
                            Directory.Move(legacy, root);
                    }
                    catch
                    {
                        if (Directory.Exists(legacy)) root = legacy; // 迁移失败退回旧目录
                    }
                }
                s_root = root;
                _rootReady = true;
                return root;
            }
        }
    }

    private static string DbPath => Path.Combine(DataDirectory, "moodia.db");

    //媒体沙盒根 = DataDirectory
    private static string MediaRoot => DataDirectory;

    private static string ConnectionString => $"Data Source={DbPath};Mode=ReadWriteCreate";

    // -------------------- Initialize --------------------

    public static async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(MediaRoot, "media"));

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        // Seed defaults only for a brand-new database; an emptied library must stay empty.
        var freshDatabase = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'categories';";
            freshDatabase = (long)(await cmd.ExecuteScalarAsync() ?? 0) == 0;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS categories (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AccentHex TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS items (
                    Id TEXT PRIMARY KEY,
                    Type INTEGER NOT NULL,
                    Content TEXT,
                    FilePath TEXT,
                    FileName TEXT,
                    SizeBytes INTEGER NOT NULL,
                    CategoryId TEXT,
                    CreatedAt TEXT NOT NULL,
                    Note TEXT,
                    IsFavorite INTEGER NOT NULL DEFAULT 0
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // indexes
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_items_category ON items(CategoryId);";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_items_created ON items(CreatedAt);";
            await cmd.ExecuteNonQueryAsync();
        }

        // first run: insert default category "全部灵感"
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM categories;";
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0);
            if (freshDatabase && count == 0)
            {
                await using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO categories (Id, Name, AccentHex, SortOrder)
                    VALUES (@id, @name, @hex, @sort);
                    """;
                ins.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                ins.Parameters.AddWithValue("@name", "全部灵感");
                ins.Parameters.AddWithValue("@hex", "#E8956B");
                ins.Parameters.AddWithValue("@sort", 0);
                await ins.ExecuteNonQueryAsync();
            }
        }

        // Migration: add PosX, PosY, ZIndex columns if not present
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(items)";
            await using var reader = await cmd.ExecuteReaderAsync();
            var cols = new HashSet<string>();
            while (await reader.ReadAsync())
                cols.Add(reader.GetString(1));
            reader.Close();

            if (!cols.Contains("PosX"))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE items ADD COLUMN PosX REAL";
                await alter.ExecuteNonQueryAsync();
            }
            if (!cols.Contains("PosY"))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE items ADD COLUMN PosY REAL";
                await alter.ExecuteNonQueryAsync();
            }
            if (!cols.Contains("ZIndex"))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE items ADD COLUMN ZIndex INTEGER NOT NULL DEFAULT 0";
                await alter.ExecuteNonQueryAsync();
            }
        }
    }

    // -------------------- Category CRUD --------------------

    public static async Task<List<MoodCategory>> GetCategoriesAsync()
    {
        var list = new List<MoodCategory>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, AccentHex, SortOrder FROM categories ORDER BY SortOrder";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MoodCategory
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                AccentHex = reader.GetString(2),
                SortOrder = reader.GetInt32(3)
            });
        }
        return list;
    }

    public static async Task<MoodCategory> AddCategoryAsync(string name, string accentHex)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        int nextSort;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM categories";
            nextSort = (int)((long)(await cmd.ExecuteScalarAsync() ?? 0));
        }

        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
            INSERT INTO categories (Id, Name, AccentHex, SortOrder)
            VALUES (@id, @name, @hex, @sort);
            """;
        var id = Guid.NewGuid();
        cmd2.Parameters.AddWithValue("@id", id.ToString());
        cmd2.Parameters.AddWithValue("@name", name);
        cmd2.Parameters.AddWithValue("@hex", accentHex);
        cmd2.Parameters.AddWithValue("@sort", nextSort);
        await cmd2.ExecuteNonQueryAsync();
        return new MoodCategory { Id = id, Name = name, AccentHex = accentHex, SortOrder = nextSort };
    }

    public static async Task RenameCategoryAsync(Guid id, string name)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE categories SET Name = @name WHERE Id = @id";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteCategoryAsync(Guid id)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        using var transaction = conn.BeginTransaction();

        // un-categorize items under this category (set CategoryId = NULL)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE items SET CategoryId = NULL WHERE CategoryId = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM categories WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Accepts a Guid (category id), or the special string value "all" / "uncategorized".
    /// </summary>
    public static async Task<int> GetItemCountAsync(object idOrSpecial)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();

        if (idOrSpecial is string s)
        {
            if (s == "all")
            {
                cmd.CommandText = "SELECT COUNT(*) FROM items";
            }
            else if (s == "uncategorized")
            {
                cmd.CommandText = "SELECT COUNT(*) FROM items WHERE CategoryId IS NULL";
            }
            else if (s == "favorites")
            {
                cmd.CommandText = "SELECT COUNT(*) FROM items WHERE IsFavorite = 1";
            }
            else
            {
                // try parse as Guid
                if (Guid.TryParse(s, out var g))
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM items WHERE CategoryId = @id";
                    cmd.Parameters.AddWithValue("@id", g.ToString());
                }
                else
                {
                    return 0;
                }
            }
        }
        else if (idOrSpecial is Guid guid)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE CategoryId = @id";
            cmd.Parameters.AddWithValue("@id", guid.ToString());
        }
        else
        {
            return 0;
        }

        return (int)((long)(await cmd.ExecuteScalarAsync() ?? 0));
    }

    // -------------------- Items --------------------

    public static async Task<List<Item>> GetItemsAsync(Guid? categoryId, ItemType? type, string? timeFilter, bool favOnly)
    {
        var list = new List<Item>();
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT Id, Type, Content, FilePath, FileName, SizeBytes, CategoryId, CreatedAt, Note, IsFavorite, PosX, PosY, ZIndex FROM items WHERE 1=1");

        if (categoryId.HasValue)
        {
            sb.Append(" AND CategoryId = @cat");
        }

        if (type.HasValue)
        {
            sb.Append(" AND Type = @type");
        }

        // timeFilter: null / "today" / "week" / "earlier"
        var now = DateTime.Now;
        if (timeFilter == "today")
        {
            var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Local);
            sb.Append(" AND CreatedAt >= @today");
        }
        else if (timeFilter == "week")
        {
            var weekStart = now.AddDays(-6); // 近7天 (today + 6 previous days)
            var weekStartNorm = new DateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0, DateTimeKind.Local);
            sb.Append(" AND CreatedAt >= @week");
        }
        else if (timeFilter == "earlier")
        {
            var weekStart = now.AddDays(-6);
            var earlierEnd = new DateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0, DateTimeKind.Local);
            sb.Append(" AND CreatedAt < @earlier");
        }

        if (favOnly)
        {
            sb.Append(" AND IsFavorite = 1");
        }

        sb.Append(" ORDER BY CreatedAt DESC");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();

        if (categoryId.HasValue)
            cmd.Parameters.AddWithValue("@cat", categoryId.Value.ToString());

        if (type.HasValue)
            cmd.Parameters.AddWithValue("@type", (int)type.Value);

        if (timeFilter == "today")
        {
            var todayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Local);
            cmd.Parameters.AddWithValue("@today", todayStart.ToString("O"));
        }
        else if (timeFilter == "week")
        {
            var weekStart = now.AddDays(-6);
            var weekStartNorm = new DateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0, DateTimeKind.Local);
            cmd.Parameters.AddWithValue("@week", weekStartNorm.ToString("O"));
        }
        else if (timeFilter == "earlier")
        {
            var weekStart = now.AddDays(-6);
            var earlierEnd = new DateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0, DateTimeKind.Local);
            cmd.Parameters.AddWithValue("@earlier", earlierEnd.ToString("O"));
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Item
            {
                Id = Guid.Parse(reader.GetString(0)),
                Type = (ItemType)reader.GetInt32(1),
                Content = reader.IsDBNull(2) ? null : reader.GetString(2),
                FilePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                SizeBytes = reader.GetInt64(5),
                CategoryId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                CreatedAt = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Note = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsFavorite = reader.GetInt32(9) == 1,
                PosX = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                PosY = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                ZIndex = reader.IsDBNull(12) ? 0 : reader.GetInt32(12)
            });
        }
        return list;
    }

    // -------------------- Add --------------------

    private static readonly HashSet<string> s_textExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".rtf", ".html", ".htm"
    };

    private static readonly HashSet<string> s_imageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    private static readonly HashSet<string> s_audioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".wma", ".opus"
    };

    private static readonly HashSet<string> s_videoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv"
    };

    public static IReadOnlyList<string> SupportedFileExtensions { get; } =
        Array.AsReadOnly(s_textExts.Concat(s_imageExts).Concat(s_audioExts).Concat(s_videoExts).ToArray());

    public static bool TryGetFileType(string fileName, out ItemType type)
    {
        var ext = Path.GetExtension(fileName);
        type = ItemType.Text;
        if (s_textExts.Contains(ext)) return true;
        if (s_imageExts.Contains(ext)) { type = ItemType.Image; return true; }
        if (s_audioExts.Contains(ext)) { type = ItemType.Audio; return true; }
        if (s_videoExts.Contains(ext)) { type = ItemType.Video; return true; }
        return false;
    }

    public static Task<Item> AddFileAsync(StorageFile file, Guid? categoryId) =>
        AddFileAsync(file, categoryId, MediaRoot, InsertFileAsync);

    // The same import pipeline can be exercised against an isolated sandbox and database.
    internal static async Task<Item> AddFileAsync(StorageFile file, Guid? categoryId,
        string rootDirectory, Func<Item, Task> persist)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!TryGetFileType(file.Name, out var type))
            throw new NotSupportedException($"不支持 {Path.GetExtension(file.Name)} 格式");

        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var id = Guid.NewGuid();
        string? content = null;
        string? filePath = null;
        var props = await file.GetBasicPropertiesAsync();
        var sizeBytes = checked((long)props.Size);

        if (type == ItemType.Text)
        {
            content = await ReadTextFileAsync(file, sizeBytes);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(rootDirectory, "media"));
            filePath = $"media/{id}{ext}";
        }

        var item = new Item
        {
            Id = id,
            Type = type,
            Content = content,
            FilePath = filePath,
            FileName = file.Name,
            SizeBytes = sizeBytes,
            CategoryId = categoryId,
            CreatedAt = DateTime.Now,
            IsFavorite = false
        };

        try
        {
            if (filePath != null)
            {
                // Stream to disk without loading large audio/video files into memory.
                using var input = await file.OpenStreamForReadAsync();
                await using var output = new FileStream(Path.Combine(rootDirectory, filePath),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await input.CopyToAsync(output);
                item.SizeBytes = output.Length;
            }
            await persist(item);
            return item;
        }
        catch
        {
            // A failed copy or database write must not leave an orphaned media file.
            if (filePath != null)
            {
                try { File.Delete(Path.Combine(rootDirectory, filePath)); }
                catch (Exception cleanupError) { System.Diagnostics.Debug.WriteLine(cleanupError); }
            }
            throw;
        }
    }

    private const int MaxTextFileBytes = 16 * 1024 * 1024;

    private static async Task<string> ReadTextFileAsync(StorageFile file, long sizeBytes)
    {
        if (sizeBytes > MaxTextFileBytes)
            throw new InvalidDataException("文本文件超过 16 MB，请拆分后再添加");

        using var input = await file.OpenStreamForReadAsync();
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer)) != 0)
        {
            if (bytes.Length + count > MaxTextFileBytes)
                throw new InvalidDataException("文本文件超过 16 MB，请拆分后再添加");
            bytes.Write(buffer, 0, count);
        }

        bytes.Position = 0;
        string text;
        try
        {
            // Detect UTF-8/UTF-16/UTF-32 BOMs, and reject invalid UTF-8 rather than storing mojibake.
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = await reader.ReadToEndAsync();
        }
        catch (DecoderFallbackException)
        {
            // Older Chinese Windows editors commonly save ANSI (GBK/GB18030) text.
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(54936,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
            text = encoding.GetString(bytes.ToArray());
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("文件包含二进制内容，无法作为文本添加");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("文本文件为空");
        return text;
    }

    private static async Task InsertFileAsync(Item item)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (Id, Type, Content, FilePath, FileName, SizeBytes, CategoryId, CreatedAt, Note, IsFavorite)
            VALUES (@id, @type, @content, @filepath, @filename, @size, @cat, @created, @note, @fav);
            """;
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@type", (int)item.Type);
        cmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filepath", (object?)item.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filename", (object?)item.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", item.SizeBytes);
        cmd.Parameters.AddWithValue("@cat", item.CategoryId.HasValue ? (object)item.CategoryId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@note", DBNull.Value);
        cmd.Parameters.AddWithValue("@fav", 0);
        await cmd.ExecuteNonQueryAsync();

    }

    public static async Task<Item> AddTextAsync(string text, Guid? categoryId)
    {
        var id = Guid.NewGuid();
        var now = DateTime.Now;
        var item = new Item
        {
            Id = id,
            Type = ItemType.Text,
            Content = text,
            FilePath = null,
            FileName = null,
            SizeBytes = 0,
            CategoryId = categoryId,
            CreatedAt = now,
            IsFavorite = false
        };

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (Id, Type, Content, FilePath, FileName, SizeBytes, CategoryId, CreatedAt, Note, IsFavorite)
            VALUES (@id, @type, @content, @filepath, @filename, @size, @cat, @created, @note, @fav);
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@type", (int)ItemType.Text);
        cmd.Parameters.AddWithValue("@content", text);
        cmd.Parameters.AddWithValue("@filepath", DBNull.Value);
        cmd.Parameters.AddWithValue("@filename", DBNull.Value);
        cmd.Parameters.AddWithValue("@size", 0);
        cmd.Parameters.AddWithValue("@cat", categoryId.HasValue ? (object)categoryId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", now.ToString("O"));
        cmd.Parameters.AddWithValue("@note", DBNull.Value);
        cmd.Parameters.AddWithValue("@fav", 0);
        await cmd.ExecuteNonQueryAsync();

        return item;
    }

    // -------------------- Update / Delete / ToggleFav --------------------

    public static async Task UpdateTextAsync(Guid id, string content)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET Content = @content WHERE Id = @id AND Type = @type";
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@type", (int)ItemType.Text);
        if (await cmd.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("这条文字已不存在，无法保存修改。");
    }

    public static async Task UpdateAsync(Item item)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE items SET
                Type = @type,
                Content = @content,
                FilePath = @filepath,
                FileName = @filename,
                SizeBytes = @size,
                CategoryId = @cat,
                CreatedAt = @created,
                Note = @note,
                IsFavorite = @fav,
                PosX = @posX,
                PosY = @posY,
                ZIndex = @zindex
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@type", (int)item.Type);
        cmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filepath", (object?)item.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filename", (object?)item.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", item.SizeBytes);
        cmd.Parameters.AddWithValue("@cat", item.CategoryId.HasValue ? (object)item.CategoryId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@note", (object?)item.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fav", item.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("@posX", (object?)item.PosX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@posY", (object?)item.PosY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@zindex", item.ZIndex);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeleteAsync(Guid id)
    {
        // get FilePath before deleting
        string? filePath = null;
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FilePath FROM items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
            {
                filePath = reader.GetString(0);
            }
        }

        // delete the DB record
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // delete the sandbox file if exists
        if (!string.IsNullOrEmpty(filePath))
        {
            try
            {
                var abs = AbsolutePath(filePath);
                if (File.Exists(abs))
                {
                    File.Delete(abs);
                }
            }
            catch
            {
                // swallow — file cleanup is best-effort
            }
        }
    }

    public static async Task ToggleFavoriteAsync(Guid id)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET IsFavorite = CASE WHEN IsFavorite = 1 THEN 0 ELSE 1 END WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    // -------------------- Move / Position --------------------

    /// <summary>Move an item to a different category (or null = unclassified).</summary>
    public static async Task MoveItemAsync(Guid itemId, Guid? categoryId)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET CategoryId = @cat WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        cmd.Parameters.AddWithValue("@cat", categoryId.HasValue ? (object)categoryId.Value.ToString() : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Update canvas position and Z-index for an item.</summary>
    public static async Task SetItemPositionAsync(Guid itemId, double? posX, double? posY, int zIndex)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET PosX = @x, PosY = @y, ZIndex = @z WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        cmd.Parameters.AddWithValue("@x", (object?)posX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@y", (object?)posY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@z", zIndex);
        await cmd.ExecuteNonQueryAsync();
    }

    // -------------------- Path helper --------------------

    /// <summary>
    /// Convert a relative sandbox path (e.g. "media/xxx.png") to an absolute filesystem path.
    /// </summary>
    public static string AbsolutePath(string relPath)
    {
        return Path.Combine(MediaRoot, relPath.Replace('/', '\\'));
    }
}
