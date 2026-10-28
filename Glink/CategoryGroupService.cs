using Glink.Core;

namespace Glink;

/// <summary>侧栏分类集合（文件夹）：纯前端概念，Core 不感知。</summary>
internal sealed class CategoryFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新集合";
    public bool Collapsed { get; set; }
}

/// <summary>
/// 分类树结构：Top 是顶层顺序（"c:分类Id" / "f:集合Id"），
/// Children 是集合 → 子分类 Id 顺序。文件一旦存在即取代
/// category-order.txt 成为顺序权威。
/// </summary>
internal sealed class CategoryGroupState
{
    public List<CategoryFolder> Folders { get; set; } = new();
    public List<string> Top { get; set; } = new();
    public Dictionary<string, List<string>> Children { get; set; } = new();
}

/// <summary>
/// 分类集合结构的旁路持久化（与 appearance/shortcuts 同款原子写模式）。
/// 注意：本应用发布时禁用了 System.Text.Json 的反射序列化（裁剪），
/// 旁路文件一律用竖线分隔的纯文本行，勿用 JsonSerializer。
/// 损坏或缺失时整体回退：顶层平铺 + Core SortOrder。
/// </summary>
internal static class CategoryGroupService
{
    private static string GroupFile => Path.Combine(Store.DataDirectory, "category-groups.txt");

    public static CategoryGroupState? Load()
    {
        try
        {
            if (!File.Exists(GroupFile)) return null;
            var state = new CategoryGroupState();
            foreach (var line in File.ReadAllLines(GroupFile))
            {
                var parts = line.Split('|');
                if (parts[0] == "f" && parts.Length >= 4 && Guid.TryParse(parts[1], out var folderId))
                {
                    state.Folders.Add(new CategoryFolder
                    {
                        Id = folderId,
                        Name = parts[2],
                        Collapsed = parts[3] == "1"
                    });
                    state.Top.Add("f:" + folderId);
                    state.Children[folderId.ToString()] = new List<string>();
                }
                else if (parts[0] == "fc" && parts.Length >= 3
                         && Guid.TryParse(parts[1], out var owner) && Guid.TryParse(parts[2], out var child)
                         && state.Children.TryGetValue(owner.ToString(), out var kids))
                {
                    kids.Add(child.ToString());
                }
                else if (parts[0] == "c" && parts.Length >= 2 && Guid.TryParse(parts[1], out var cat))
                {
                    state.Top.Add("c:" + cat);
                }
            }
            return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(CategoryGroupState state)
    {
        try
        {
            Directory.CreateDirectory(Store.DataDirectory);
            // 行序即结构：f 行（集合头）后跟 fc 行（子分类），c 行是顶层分类。
            var lines = new List<string>();
            foreach (var token in state.Top)
            {
                if (token.StartsWith("f:") && Guid.TryParse(token.AsSpan(2), out var folderId))
                {
                    var folder = state.Folders.FirstOrDefault(f => f.Id == folderId);
                    if (folder == null) continue;
                    var name = folder.Name.Replace("|", "／").Replace("\r", " ").Replace("\n", " ");
                    lines.Add($"f|{folderId}|{name}|{(folder.Collapsed ? 1 : 0)}");
                    if (state.Children.TryGetValue(folderId.ToString(), out var kids))
                        foreach (var child in kids) lines.Add($"fc|{folderId}|{child}");
                }
                else if (token.StartsWith("c:") && Guid.TryParse(token.AsSpan(2), out var catId))
                {
                    lines.Add("c|" + catId);
                }
            }
            var temporary = GroupFile + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, GroupFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 写盘失败时本次会话内仍保持内存中的结构。
        }
    }
}

