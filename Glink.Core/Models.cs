namespace Glink.Core;

public enum ItemType { Text, Image, Audio, Video }

public class MoodCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AccentHex { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class Item
{
    public Guid Id { get; set; }
    public ItemType Type { get; set; }
    public string? Content { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public Guid? CategoryId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Note { get; set; }
    public bool IsFavorite { get; set; }
    // 画布模式位置/Z层级（null=未设置，使用自动布局）
    public double? PosX { get; set; }
    public double? PosY { get; set; }
    public int ZIndex { get; set; }
}