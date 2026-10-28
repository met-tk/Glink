using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Glink.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Glink;

public sealed partial class MainWindow
{
    private float _requestedZoom = 1;

    private static bool ControlIsDown =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private static bool ShiftIsDown =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;

    private static bool AltIsDown =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;

    private void ContentArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // 两种模式的 Ctrl+滚轮缩放都由 ScrollViewer 原生接管（锚点即光标），
        // 本处理器只在交互进行中（拖卡/波形 seek/画布平移）吞掉事件，
        // 避免缩放打断操作。
        if (_isListMode) return;
        var pointer = e.GetCurrentPoint(CanvasScroll);
        if (!ControlIsDown || pointer.Properties.IsHorizontalMouseWheel) return;
        if (_pendingDragCard != null || _seekCanvas != null || _isPanning || _resize != null)
        {
            e.Handled = true;
        }
    }

    private Point CanvasViewportCenter() => new(CanvasScroll.ActualWidth / 2, CanvasScroll.ActualHeight / 2);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_isListMode) { ListScroll.ChangeView(null, null, 1f, disableAnimation: true); SyncZoomLabel(); }
        else ZoomCanvas(1, CanvasViewportCenter());
    }

    private void ZoomCanvas(double factor, Point pointer)
    {
        if (_pendingDragCard != null || _seekCanvas != null || _isPanning || _resize != null || CanvasScroll.ActualWidth <= 0) return;
        var zoom = (float)Math.Clamp(factor, CanvasScroll.MinZoomFactor, CanvasScroll.MaxZoomFactor);
        var oldZoom = CanvasScroll.ZoomFactor;
        var padding = CanvasScroll.Padding;
        // Offsets are screen DIPs; card positions remain in unscaled canvas coordinates.
        var anchorX = Math.Clamp(pointer.X - padding.Left, 0, Math.Max(0, CanvasScroll.ActualWidth - padding.Left - padding.Right));
        var anchorY = Math.Clamp(pointer.Y - padding.Top, 0, Math.Max(0, CanvasScroll.ActualHeight - padding.Top - padding.Bottom));
        var x = Math.Max(0, (CanvasScroll.HorizontalOffset + anchorX) * zoom / oldZoom - anchorX);
        var y = Math.Max(0, (CanvasScroll.VerticalOffset + anchorY) * zoom / oldZoom - anchorY);
        if (CanvasScroll.ChangeView(x, y, zoom, disableAnimation: true)) _requestedZoom = zoom;
    }

    private void CanvasScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ZoomResetBtn == null || _isListMode) return;
        // 原生缩放也要同步 _requestedZoom，快捷键 zoomIn/zoomOut 与紧凑布局宽度
        // 依赖它与 ZoomFactor 一致。
        _requestedZoom = CanvasScroll.ZoomFactor;
        var label = $"{CanvasScroll.ZoomFactor:P0}";
        ZoomResetBtn.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ZoomResetBtn, $"缩放 {label}，点击恢复原始大小");
        // 小缩放比例下的文本自适应：缩放值变化时去抖重算（见 MainWindow.TextFit.cs）。
        ScheduleAdaptiveTextScale();
    }

    private double CompactLayoutWidth()
    {
        var width = CanvasScroll.ActualWidth > 0 ? CanvasScroll.ActualWidth : 824;
        return Math.Max(360, (width - CanvasScroll.Padding.Left - CanvasScroll.Padding.Right) / CanvasScroll.ZoomFactor);
    }

    private void ApplyCompactLayout()
    {
        var cards = CardCanvas.Children.OfType<Border>().ToList();
        var placements = _centeredPacking
            ? BinPackingLayoutEngine.ComputeRadialPacking(cards, CompactLayoutWidth(), gap: 12.0)
            : BinPackingLayoutEngine.ComputeCompactBinPacking(cards, CompactLayoutWidth(), originX: 0, originY: 0).ToList();
        double viewX = 0, viewY = 0;
        if (_centeredPacking)
        {
            var (ax, ay) = RadialAnchor(placements);
            (viewX, viewY) = AnchorPlacementsOnViewport(placements, ax, ay); // 最大卡片中心对齐视口中心
        }
        foreach (var placement in placements)
        {
            Canvas.SetLeft(placement.Card, placement.TargetX);
            Canvas.SetTop(placement.Card, placement.TargetY);
            Canvas.SetZIndex(placement.Card, placement.TargetZIndex);
        }
        _maxZIndex = cards.Count;
        UpdateCanvasExtent();
        // 布局/刷新后保持当前缩放下的文本自适应（新卡片在这里建立基线）。
        ApplyAdaptiveTextScale();
        if (_centeredPacking) CenterCanvasViewOn(viewX, viewY);
    }

    // ------------------------------------------------------------------
    //  紧凑平铺锚点：左上角（默认）或画布中心。偏好走旁路文件 layout-prefs.txt。
    // ------------------------------------------------------------------

    private bool _centeredPacking = LoadCenteredPref();

    // ------------------------------------------------------------------
    //  文本卡居中与字体：偏好走旁路文件 text-prefs.txt（多行 key=value，
    // 兼容旧版单行 center/top）。字体未安装时 WinUI 静默回退默认。
    // ------------------------------------------------------------------

    private bool _textCentered;
    private string _textFontFamily = DefaultTextFont;
    internal const string DefaultTextFont = "Segoe UI";

    /// <summary>可选字体：显示名 + FontFamily 名（Windows 自带中文西文字体）。</summary>
    internal static readonly (string Label, string Family)[] TextFontChoices =
    {
        ("默认（Segoe UI）", DefaultTextFont),
        ("微软雅黑", "Microsoft YaHei"),
        ("等线", "DengXian"),
        ("黑体", "SimHei"),
        ("楷体", "KaiTi"),
        ("宋体", "SimSun")
    };

    private static string TextPrefsFile => System.IO.Path.Combine(Store.DataDirectory, "text-prefs.txt");

    private void LoadTextPrefs()
    {
        _textCentered = true;
        _textFontFamily = DefaultTextFont;
        try
        {
            if (!System.IO.File.Exists(TextPrefsFile)) return;
            foreach (var raw in System.IO.File.ReadAllLines(TextPrefsFile))
            {
                var line = raw.Trim();
                if (line == "center") _textCentered = true;
                else if (line == "top") _textCentered = false;
                else if (line.StartsWith("font=", System.StringComparison.OrdinalIgnoreCase))
                {
                    var family = line[5..].Trim();
                    if (family.Length > 0 && TextFontChoices.Any(c => c.Family == family))
                        _textFontFamily = family;
                }
            }
        }
        catch { /* 读取失败按默认 */ }
    }

    private void SaveTextPrefs()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Store.DataDirectory);
            var temporary = TextPrefsFile + ".tmp";
            System.IO.File.WriteAllLines(temporary,
                new[] { _textCentered ? "center" : "top", "font=" + _textFontFamily });
            System.IO.File.Move(temporary, TextPrefsFile, overwrite: true);
        }
        catch { /* 写盘失败时本次会话内仍生效 */ }
    }

    private static string LayoutPrefsFile => System.IO.Path.Combine(Store.DataDirectory, "layout-prefs.txt");

    private static bool LoadCenteredPref()
    {
        try { return System.IO.File.Exists(LayoutPrefsFile) && System.IO.File.ReadAllText(LayoutPrefsFile).Trim() == "center"; }
        catch { return false; }
    }

    private void SaveCenteredPref()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Store.DataDirectory);
            var temporary = LayoutPrefsFile + ".tmp";
            System.IO.File.WriteAllText(temporary, _centeredPacking ? "center" : "topleft");
            System.IO.File.Move(temporary, LayoutPrefsFile, overwrite: true);
        }
        catch { /* 写盘失败时本次会话内仍生效 */ }
    }

    /// <summary>
    /// 中心锚点：把装箱结果的指定锚点（排布局部坐标，放射模式 = 最大卡片中心）
    /// 对到"当前可视区域的中心"（块整体保持 ≥24 边距，负坐标会被滚动区裁掉）。
    /// 返回锚点的最终内容坐标供取景。
    /// </summary>
    private (double AnchorX, double AnchorY) AnchorPlacementsOnViewport(
        List<BinPackingLayoutEngine.PackedPlacement> placements, double anchorX, double anchorY)
    {
        if (placements.Count == 0)
            return (CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);
        var minX = placements.Min(p => p.TargetX);
        var minY = placements.Min(p => p.TargetY);
        var center = CanvasViewportCenterInContent();
        var dx = center.X - anchorX;
        var dy = center.Y - anchorY;
        if (minX + dx < 24) dx = 24 - minX;
        if (minY + dy < 24) dy = 24 - minY;
        for (var i = 0; i < placements.Count; i++)
            placements[i] = placements[i] with { TargetX = placements[i].TargetX + dx, TargetY = placements[i].TargetY + dy };
        return (anchorX + dx, anchorY + dy);
    }

    /// <summary>放射排布的锚点：首个放置的卡片（面积最大）的中心。</summary>
    private static (double X, double Y) RadialAnchor(List<BinPackingLayoutEngine.PackedPlacement> placements)
    {
        if (placements.Count == 0) return (0, 0);
        var first = placements[0];
        return (first.TargetX + BinPackingLayoutEngine.GetCardWidth(first.Card) / 2,
                first.TargetY + BinPackingLayoutEngine.GetCardHeight(first.Card) / 2);
    }

    /// <summary>当前视口中心在画布内容坐标系里的位置（含缩放与内边距）。</summary>
    private Point CanvasViewportCenterInContent()
    {
        if (CanvasScroll.ActualWidth <= 0 || CanvasScroll.ActualHeight <= 0) return new Point(0, 0);
        return CanvasScroll.TransformToVisual(CardCanvas)
            .TransformPoint(new Point(CanvasScroll.ActualWidth / 2, CanvasScroll.ActualHeight / 2));
    }

    /// <summary>把视口中心对准内容坐标（中心锚点布局后的取景；块已在视口中心时零跳变）。</summary>
    private void CenterCanvasViewOn(double contentX, double contentY)
    {
        if (CanvasScroll.ActualWidth <= 0) return;
        var zoom = CanvasScroll.ZoomFactor > 0 ? CanvasScroll.ZoomFactor : 1;
        var center = CanvasViewportCenterInContent();
        var targetX = Math.Clamp(CanvasScroll.HorizontalOffset + (contentX - center.X) * zoom, 0, Math.Max(0, CanvasScroll.ScrollableWidth));
        var targetY = Math.Clamp(CanvasScroll.VerticalOffset + (contentY - center.Y) * zoom, 0, Math.Max(0, CanvasScroll.ScrollableHeight));
        CanvasScroll.ChangeView((float)targetX, (float)targetY, null, disableAnimation: true);
    }

    /// <summary>布局/重置后的取景：左上锚点滚到内容块左上；中心锚点的取景由 ApplyCompactLayout 完成。</summary>
    private void FocusCanvasContent(float? zoom = null)
    {
        if (_centeredPacking)
        {
            if (zoom != null) CanvasScroll.ChangeView(null, null, zoom, disableAnimation: true);
            return;
        }
        var cards = CardCanvas.Children.OfType<Border>().ToList();
        if (cards.Count == 0)
        {
            CanvasScroll.ChangeView(0, 0, zoom, disableAnimation: true);
            return;
        }
        var x = (float)Math.Max(0, cards.Min(c => Canvas.GetLeft(c)));
        var y = (float)Math.Max(0, cards.Min(c => Canvas.GetTop(c)));
        CanvasScroll.ChangeView(x, y, zoom, disableAnimation: true);
    }

    private void UpdateCanvasExtent()
    {
        var cards = CardCanvas.Children.OfType<Border>().ToList();
        // Keep room for free placement and panning after the initial compact arrangement.
        CanvasHost.Width = Math.Max(6000, cards.Select(c => Canvas.GetLeft(c) + BinPackingLayoutEngine.GetCardWidth(c) + 48).DefaultIfEmpty(0).Max());
        CanvasHost.Height = Math.Max(4000, cards.Select(c => Canvas.GetTop(c) + BinPackingLayoutEngine.GetCardHeight(c) + 48).DefaultIfEmpty(0).Max());
    }
}
