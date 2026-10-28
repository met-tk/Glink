using Glink.Core;

namespace Glink;

/// <summary>
/// 分类显示顺序的前端持久化。
/// Core（Store.cs）没有提供排序写接口且前端禁止修改，因此与
/// appearance.txt / shortcuts.txt 一样走旁路文件：每行一个分类 Id。
/// 未记录的分类按 Core 的 SortOrder 追加在末尾。
/// </summary>
internal static class CategoryOrderService
{
    private static string OrderFile => System.IO.Path.Combine(Store.DataDirectory, "category-order.txt");

    public static List<Guid> Load()
    {
        try
        {
            if (!File.Exists(OrderFile)) return new List<Guid>();
            var ids = new List<Guid>();
            foreach (var line in File.ReadAllLines(OrderFile))
                if (Guid.TryParse(line.Trim(), out var id)) ids.Add(id);
            return ids;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 顺序文件损坏时静默回退到 Core 的 SortOrder。
            return new List<Guid>();
        }
    }

    public static void Save(IEnumerable<Guid> ids)
    {
        try
        {
            Directory.CreateDirectory(Store.DataDirectory);
            var temporary = OrderFile + ".tmp";
            File.WriteAllLines(temporary, ids.Select(id => id.ToString()));
            File.Move(temporary, OrderFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 写盘失败时本次会话内仍保持内存中的顺序。
        }
    }
}
