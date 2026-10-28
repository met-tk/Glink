using System.Text.Json;

namespace Glink;

/// <summary>布局预设中一张卡片的摆放：条目 Id + 画布坐标 / 层级 / 宽度。</summary>
public sealed class LayoutPresetItem
{
    public Guid Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Z { get; set; }
    public double W { get; set; }
}

/// <summary>
/// 画布布局预设：保存某一刻画布上"有哪些对象、各摆在哪"。以特殊条目出现在
/// 侧栏情绪意向列表中，点击后按保存的布局原位加载（不自动紧凑平铺）；
/// 普通分类不携带布局信息，行为不变。
/// </summary>
public sealed class LayoutPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<LayoutPresetItem> Items { get; set; } = new();
}

/// <summary>布局预设的旁路文件存储（与 category-groups/text-prefs 同模式，不经 SQLite）。</summary>
public static class LayoutPresetStore
{
    private static string Path => System.IO.Path.Combine(Glink.Core.Store.DataDirectory, "layout-presets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static List<LayoutPreset> Load()
    {
        try
        {
            if (!File.Exists(Path)) return new List<LayoutPreset>();
            var loaded = JsonSerializer.Deserialize<List<LayoutPreset>>(File.ReadAllText(Path), JsonOptions);
            return loaded ?? new List<LayoutPreset>();
        }
        catch
        {
            return new List<LayoutPreset>(); // 损坏文件按空处理，可用性优先
        }
    }

    public static void Save(List<LayoutPreset> presets)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(presets, JsonOptions));
            File.Move(temporary, Path, overwrite: true);
        }
        catch
        {
            // 写盘失败时本次会话内仍可使用已改动的内存列表。
        }
    }
}
