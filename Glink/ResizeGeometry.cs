using Glink.Core;

namespace Glink;

/// <summary>
/// 卡片边缘调整大小的纯几何核心（脱离 UI 可单测，模式同 RadialPackingCore）：
/// 把手能力矩阵、屏幕空间把手环宽换算、把手光标映射，以及标准边缘拖拽几何
/// ——被拖边跟随指针、对边锚定不动；四角等比缩放取主导轴（换算到宽度空间
/// 后位移更大的轴）；宽度钳制 [MinWidth, MaxWidth]；左/上锚定不得越过画布
/// 原点（负坐标会被滚动区裁掉、不可达）。
/// </summary>
internal static class ResizeGeometry
{
    [Flags]
    public enum Edge { None = 0, West = 1, East = 2, North = 4, South = 8 }

    public const double MinWidth = 150.0;
    public const double MaxWidth = 800.0;
    public const double BandDips = 12.0; // 把手环宽（屏幕 DIP）

    // 标准系统光标资源 ID（IDC_*）
    public const int IdcSizenwse = 32642;
    public const int IdcSizenesw = 32643;
    public const int IdcSizewe = 32644;
    public const int IdcSizens = 32645;

    /// <summary>各卡片类型的可用把手：图片 8 向；视频 8 向（画面高随宽度按比率联动，
    /// 拖底边/下角等效换算为宽度；放大按钮区域在命中层单独让位）；文本/音频高度随内容
    /// 自适应，仅左右。</summary>
    public static Edge CapabilitiesOf(ItemType type) => type switch
    {
        ItemType.Image or ItemType.Video => Edge.West | Edge.East | Edge.North | Edge.South,
        _ => Edge.West | Edge.East
    };

    /// <summary>把手环宽（卡片局部坐标）：随缩放补偿（屏幕上恒 ~8 DIP），
    /// 并钳制不超过该方向边长的 1/3——小卡片上各把手区永不重叠。</summary>
    public static double BandWidth(double zoom, double dimension) =>
        Math.Min(BandDips / Math.Max(zoom, 0.01), dimension / 3.0);

    /// <summary>把手 → 标准系统调整光标（IDC_*，LoadCursor 用）：西+北/东+南 → ↖↘，
    /// 西+南/东+北 → ↗↙；无把手返回 0。</summary>
    public static int CursorIdFor(Edge edge)
    {
        var horizontal = edge & (Edge.West | Edge.East);
        var vertical = edge & (Edge.North | Edge.South);
        if (horizontal != Edge.None && vertical != Edge.None)
            return (horizontal == Edge.West) == (vertical == Edge.North) ? IdcSizenwse : IdcSizenesw;
        if (horizontal != Edge.None) return IdcSizewe;
        if (vertical != Edge.None) return IdcSizens;
        return 0;
    }

    /// <summary>一次调整大小会话的几何快照（画布坐标系）。</summary>
    public sealed record Session
    {
        public required Edge Handle { get; init; }
        public bool Aspect { get; init; }        // 图片/视频：内容高度由宽度按比率联动
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double StartW { get; init; }
        public double StartH { get; init; }
        public double Ratio { get; init; }       // 内容宽/高
        public double OverheadW { get; init; }   // 卡宽 − 内容宽
        public double OverheadH { get; init; }   // 卡高 − 内容高（图片=上下边框；视频=时间轴+页脚）
        public double ContentMinH { get; init; } // 视频画面高度钳制（图片 0）
        public double ContentMaxH { get; init; } = double.PositiveInfinity;
    }

    /// <summary>Aspect 卡：卡高 = 内容高 + 固定开销；内容高按比率由宽换算（视频画面另有高度钳制）。</summary>
    public static double HeightForWidth(Session s, double width) =>
        Math.Clamp((width - s.OverheadW) / s.Ratio, s.ContentMinH, s.ContentMaxH) + s.OverheadH;

    /// <summary>Aspect 卡：由目标卡高反推卡宽（含同样的内容高度钳制，保证与正向换算一致）。</summary>
    public static double WidthForHeight(Session s, double height) =>
        Math.Clamp(height - s.OverheadH, s.ContentMinH, s.ContentMaxH) * s.Ratio + s.OverheadW;

    /// <summary>标准边缘拖拽几何的目标宽度：被拖边跟随指针、对边锚定；
    /// 四角等比缩放取主导轴（两个候选轴里离起点更远的）。</summary>
    public static double TargetWidth(Session s, double px, double py)
    {
        var fromX = s.Handle.HasFlag(Edge.West)
            ? s.StartX + s.StartW - px
            : s.Handle.HasFlag(Edge.East) ? px - s.StartX : s.StartW;
        if (!s.Aspect || (s.Handle & (Edge.North | Edge.South)) == Edge.None)
            return fromX;
        var fromY = s.Handle.HasFlag(Edge.North)
            ? WidthForHeight(s, s.StartY + s.StartH - py)
            : WidthForHeight(s, py - s.StartY);
        if ((s.Handle & (Edge.West | Edge.East)) == Edge.None)
            return fromY;
        return Math.Abs(fromX - s.StartW) >= Math.Abs(fromY - s.StartW) ? fromX : fromY;
    }

    /// <summary>指针位置（画布坐标）→ 最终几何：宽度钳制 + 左/上锚定的画布原点地板。
    /// 返回 (宽, 左, 上)；左/上仅在对应锚定轴上有意义（其余等于起点）。</summary>
    public static (double Width, double Left, double Top) Apply(Session s, double px, double py)
    {
        var width = Math.Clamp(TargetWidth(s, px, py), MinWidth, MaxWidth);
        if (s.Handle.HasFlag(Edge.West))
            width = Math.Min(width, s.StartX + s.StartW);
        if (s.Aspect && s.Handle.HasFlag(Edge.North))
            width = Math.Min(width, WidthForHeight(s, s.StartY + s.StartH));
        var left = s.Handle.HasFlag(Edge.West) ? s.StartX + s.StartW - width : s.StartX;
        var top = s.Aspect && s.Handle.HasFlag(Edge.North)
            ? s.StartY + s.StartH - HeightForWidth(s, width)
            : s.StartY;
        return (width, left, top);
    }
}
