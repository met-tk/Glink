using System.Globalization;
using System.Text;

namespace Glink.Core;

public enum ExportScope { All, Type, Category, Folder }
public sealed record ExportFolder(Guid Id, string Name, IReadOnlyCollection<Guid> CategoryIds);
public sealed record ExportRequest(ExportScope Scope, IReadOnlyCollection<ItemType> Types,
    IReadOnlyCollection<Guid?> Categories, IReadOnlyCollection<Guid> Folders,
    IReadOnlyCollection<Guid>? ItemIds = null);
public sealed record ExportProgress(int Completed, int Total);
public sealed record ExportResult(string Directory, int TextCount, int MediaCount, IReadOnlyList<string> Failures);

public static class ExportService
{
    public static string TypeName(ItemType type) => type switch
    {
        ItemType.Text => "文本", ItemType.Image => "图片", ItemType.Audio => "音频", ItemType.Video => "视频",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static List<Item> Select(IEnumerable<Item> items, ExportRequest request, IReadOnlyList<ExportFolder> folders)
    {
        if (request.ItemIds is { } itemIds)
        {
            var selectedIds = itemIds.ToHashSet();
            return items.Where(item => selectedIds.Contains(item.Id))
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
        }
        var folderCategories = folders.Where(f => request.Folders.Contains(f.Id)).SelectMany(f => f.CategoryIds).ToHashSet();
        return items.Where(item => request.Scope switch
        {
            ExportScope.All => true,
            ExportScope.Type => request.Types.Contains(item.Type),
            ExportScope.Category => request.Categories.Contains(item.CategoryId),
            ExportScope.Folder => item.CategoryId is Guid id && folderCategories.Contains(id),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        }).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
    }

    public static async Task<ExportResult> ExportAsync(string destination, string mediaRoot,
        IReadOnlyList<Item> items, IReadOnlyList<MoodCategory> categories, IReadOnlyList<ExportFolder> folders,
        ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = Select(items, request, folders);
        if (selected.Count == 0) throw new InvalidOperationException("所选范围没有可导出的灵感。");
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("导出位置不存在，请重新选择文件夹。");
        mediaRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaRoot));
        // Each run owns a new staging directory; existing exports are never overwritten.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"Glink-导出-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}";
        var staging = Path.Combine(destination, "." + name + ".partial");
        var output = Path.Combine(destination, name);
        Directory.CreateDirectory(staging);
        var failures = new List<string>();
        var textCount = 0;
        var mediaCount = 0;
        try
        {
            var categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);
            string CategoryName(Item item) => item.CategoryId is Guid id && categoryNames.TryGetValue(id, out var label) ? label : "未分类";
            var categoryDirectories = UniqueNames(categories.Select(c => (c.Id, c.Name)));
            var folderDirectories = UniqueNames(folders.Select(f => (f.Id, f.Name)));
            StreamWriter? writer = null;
            try
            {
                for (var index = 0; index < selected.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = selected[index];
                    if (item.Type == ItemType.Text)
                    {
                        writer ??= new StreamWriter(new FileStream(Path.Combine(staging, "灵感文本.txt"), FileMode.CreateNew), new UTF8Encoding(true));
                        var date = item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        await writer.WriteLineAsync($"[{date}] · {CategoryName(item)}".AsMemory(), cancellationToken);
                        await writer.WriteLineAsync((item.Content ?? string.Empty).AsMemory(), cancellationToken);
                        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                        textCount++;
                    }
                    else
                    {
                        string source;
                        try
                        {
                            source = MediaSource(mediaRoot, item.FilePath);
                            // Open the source before creating the destination so a missing source leaves no empty file.
                            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                            var parts = new List<string> { staging };
                            if (request.Scope == ExportScope.Type) parts.Add(TypeName(item.Type));
                            if (request.Scope == ExportScope.Folder)
                            {
                                var folder = folders.First(f => request.Folders.Contains(f.Id) && item.CategoryId is Guid id && f.CategoryIds.Contains(id));
                                parts.Add(folderDirectories[folder.Id]);
                            }
                            if (request.Scope is ExportScope.Category or ExportScope.Folder)
                                parts.Add(item.CategoryId is Guid id && categoryDirectories.TryGetValue(id, out var dir) ? dir : "未分类");
                            var directory = Path.Combine(parts.ToArray());
                            Directory.CreateDirectory(directory);
                            var original = Path.GetFileName(item.FileName ?? source);
                            var extension = Path.GetExtension(source);
                            // The stored media suffix is authoritative; never change a file's format during export.
                            var filename = SafeName(Path.GetFileNameWithoutExtension(original)) + extension;
                            var target = Path.Combine(directory, $"{index + 1:D4}_{filename}");
                            await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                            await input.CopyToAsync(file, cancellationToken);
                            mediaCount++;
                        }
                        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
                        {
                            failures.Add($"{item.FileName ?? item.Id.ToString()}：{error.Message}");
                        }
                    }
                    progress?.Report(new ExportProgress(index + 1, selected.Count));
                }
            }
            finally
            {
                if (writer != null) await writer.DisposeAsync();
            }
            if (failures.Count > 0)
                await File.WriteAllLinesAsync(Path.Combine(staging, "未导出文件.txt"), failures, new UTF8Encoding(true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            return new ExportResult(output, textCount, mediaCount, failures);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string MediaSource(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("媒体路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("媒体路径超出灵感库。");
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("媒体路径包含链接，请重新导入原始文件。");
        return path;
    }

    private static Dictionary<Guid, string> UniqueNames(IEnumerable<(Guid Id, string Name)> values)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "未分类" };
        var result = new Dictionary<Guid, string>();
        foreach (var (id, name) in values)
        {
            var safe = SafeName(name);
            var candidate = safe;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{safe} ({n})";
            result[id] = candidate;
        }
        return result;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 80) safe = safe[..80].TrimEnd('.');
        if (safe.Length == 0) safe = "未命名";
        var stem = safe.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            safe = "_" + safe;
        return safe;
    }
}
