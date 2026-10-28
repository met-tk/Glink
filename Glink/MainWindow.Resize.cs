using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Glink.Core;
using Windows.Foundation;

namespace Glink;

// ====================================================================
//  画布卡片调整大小 —— 标准 8 向把手（edge-handle）交互层：
//  命中判定、光标反馈、指针捕获与内容联动；几何计算在 ResizeGeometry
//  （纯核心，ImportTests 覆盖：目标宽度、锚定、钳制、主导轴、能力矩阵）。
//
//  - 命中判定（悬停与按下共用）：指针落入卡片外缘的"把手环"即命中，
//    角（两轴同时命中）天然优先于边；环宽随缩放补偿并钳制边长 1/3。
//  - 各卡片类型的可用把手见 ResizeGeometry.CapabilitiesOf（图片 8 向、
//    视频无底部把手且让位放大按钮、文本/音频仅左右）。
//  - 光标：把手区显示对应标准系统调整光标（↔ ↕ ↖↘ ↗↙），悬停与拖拽
//    期间随指针移动持续刷新（系统每次移动会重置）。
//  - Ctrl/Shift + 左键保留给点选/框选；拖动卡片、多选整组拖拽不受影响。
//    多点触控下只跟踪起手那根指针；Esc/指针捕获丢失安全终止会话。
//  - 松手后不自动重排（自由画布语义）；尺寸为会话级状态，刷新后回到
//    按内容自动计算的大小。
// ====================================================================

public sealed partial class MainWindow
{
    // WinUI3 中 XAML 框架自管光标，Win32 SetCursor 会在每次指针移动时被框架
    // 重置覆盖，必须走 UIElement.ProtectedCursor（protected，反射写入根容器）。
    private static readonly System.Reflection.PropertyInfo? _protectedCursorProperty =
        typeof(UIElement).GetProperty("ProtectedCursor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static readonly InputCursor _resizeCursorNwse = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly InputCursor _resizeCursorNesw = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly InputCursor _resizeCursorSizeWe = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor _resizeCursorSizeNs = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    /// <summary>把手对应的标准系统调整光标（无把手返回 null）。</summary>
    private static InputCursor? CursorFor(ResizeGeometry.Edge zone) => ResizeGeometry.CursorIdFor(zone) switch
    {
        ResizeGeometry.IdcSizenwse => _resizeCursorNwse,
        ResizeGeometry.IdcSizenesw => _resizeCursorNesw,
        ResizeGeometry.IdcSizewe => _resizeCursorSizeWe,
        ResizeGeometry.IdcSizens => _resizeCursorSizeNs,
        _ => null
    };

    /// <summary>通过 ProtectedCursor 设置窗口光标；null 还原默认箭头。
    /// 记录最后设置的值，供画布层在无任何把手/会话时兜底还原。</summary>
    private InputCursor? _activeCursor;

    private void SetWindowCursor(InputCursor? cursor)
    {
        _activeCursor = cursor;
        if (_protectedCursorProperty == null) return;
        try { _protectedCursorProperty.SetValue(RootGrid, cursor); }
        catch { /* 反射不可用时静默降级 */ }
    }

    /// <summary>进行中的调整大小会话：几何核心快照 + 交互状态（卡片、起手指针、光标）。</summary>
    private sealed class ResizeSession
    {
        public required Border Card { get; init; }
        public required ResizeGeometry.Edge Handle { get; init; }
        public required uint PointerId { get; init; }
        public required InputCursor Cursor { get; init; }
        public required ResizeGeometry.Session Geometry { get; init; }
    }

    private ResizeSession? _resize;
    private Border? _edgeHoverCard;

    /// <summary>视频放大按钮的缩小让位判定：仅按钮中心 24×24 区域保留按钮语义，
    /// 其余底部区域开放为调整把手。</summary>
    private static bool ResizeVideoButtonHot(Border card, VideoCardView video, Point buttonLocal)
    {
        var b = video.ExpandButtonBounds;
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;
        const double half = 12;
        return buttonLocal.X >= cx - half && buttonLocal.X <= cx + half &&
               buttonLocal.Y >= cy - half && buttonLocal.Y <= cy + half;
    }

    /// <summary>指针命中的调整把手（悬停/按下共用）。列表模式与内容缺失的卡片不可调。</summary>
    private ResizeGeometry.Edge ResizeEdgeZone(Border card, Point local)
    {
        if (_isListMode || IsWordCloudTextCard(card) || card.DataContext is not ItemVM vm) return ResizeGeometry.Edge.None;
        switch (vm.Type)
        {
            case ItemType.Image:
                // 坏图（占位文本）没有照片与比率，不提供把手。
                if (FindChildByName<Border>(card, "ContentPhoto") is not { } photo || photo.Tag is not double ratio || ratio <= 0)
                    return ResizeGeometry.Edge.None;
                break;
            case ItemType.Video:
                if (card.Child is not VideoCardView video)
                    return ResizeGeometry.Edge.None;
                // 底边/下角已开放把手：让位区缩小为放大按钮实际点击区（按钮中心 24×24），
                // 其余时间轴/页脚区域仍可从卡片边缘起手调整大小。
                var buttonLocal = new Point(local.X - card.BorderThickness.Left, local.Y - card.BorderThickness.Top);
                if (ResizeVideoButtonHot(card, video, buttonLocal))
                    return ResizeGeometry.Edge.None;
                break;
        }
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return ResizeGeometry.Edge.None;
        var zoom = CanvasScroll.ZoomFactor > 0 ? CanvasScroll.ZoomFactor : 1;
        var bandX = ResizeGeometry.BandWidth(zoom, card.ActualWidth);
        var bandY = ResizeGeometry.BandWidth(zoom, card.ActualHeight);
        if (bandX <= 0 || bandY <= 0) return ResizeGeometry.Edge.None;
        var zone = ResizeGeometry.Edge.None;
        if (local.X <= bandX) zone |= ResizeGeometry.Edge.West;
        else if (local.X >= card.ActualWidth - bandX) zone |= ResizeGeometry.Edge.East;
        if (local.Y <= bandY) zone |= ResizeGeometry.Edge.North;
        else if (local.Y >= card.ActualHeight - bandY) zone |= ResizeGeometry.Edge.South;
        return zone & ResizeGeometry.CapabilitiesOf(vm.Type);
    }

    /// <summary>指针在卡片上移动时调用：进入把手环点亮把手样式并刷新光标，离开还原。</summary>
    private void UpdateEdgeHover(Border card, Point local)
    {
        var zone = ResizeEdgeZone(card, local);
        var on = zone != ResizeGeometry.Edge.None || ReferenceEquals(_resize?.Card, card);
        if (!on)
        {
            ClearEdgeHover(card, stillOnCard: true);
            return;
        }
        if (!ReferenceEquals(_edgeHoverCard, card))
        {
            _edgeHoverCard = card;
            card.BorderThickness = new Thickness(2);
            card.BorderBrush = ResourceBrush("AccentBrush");
        }
        var cursor = _resize is { } session && ReferenceEquals(session.Card, card)
            ? (InputCursor?)session.Cursor
            : CursorFor(zone);
        SetWindowCursor(cursor); // ProtectedCursor 由框架维持，无需逐移动刷新
    }

    /// <summary>还原边缘把手样式（stillOnCard = 指针仍在卡内，恢复悬停边框而非常态边框）。</summary>
    private void ClearEdgeHover(Border card, bool stillOnCard)
    {
        if (!ReferenceEquals(_edgeHoverCard, card)) return;
        _edgeHoverCard = null;
        SetWindowCursor(null);
        if (IsWordCloudTextCard(card))
        {
            card.BorderThickness = new Thickness(0);
            return;
        }
        card.BorderThickness = new Thickness(card.DataContext is ItemVM { CategoryId: not null } ? 2 : 1);
        card.BorderBrush = BorderBrushForCard(card, hovering: stillOnCard);
    }

    /// <summary>边缘普通左键按下：接管为 resize（在拖动/多选分流之前调用）。</summary>
    private bool TryStartResize(Border card, PointerRoutedEventArgs e)
    {
        if (_resize != null) return false;
        var zone = ResizeEdgeZone(card, e.GetCurrentPoint(card).Position);
        if (zone == ResizeGeometry.Edge.None) return false;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return false;
        if (e.KeyModifiers != Windows.System.VirtualKeyModifiers.None) return false;
        if (!BuildResizeSession(card, zone, e.Pointer.PointerId, out var session)) return false;
        if (!card.CapturePointer(e.Pointer)) return false;
        _resize = session;
        e.Handled = true;
        return true;
    }

    /// <summary>视频卡专用兜底：画面表面把内部普通左键标记为已处理（点按播放），低缩放下
    /// 把手环宽超过表面 12px 让位环时，边缘按下需要收听已处理事件才能起手调整。</summary>
    private void CanvasCard_ResizePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_resize != null) return;
        if (sender is not Border card || card.DataContext is not ItemVM { Type: ItemType.Video }) return;
        TryStartResize(card, e);
    }

    /// <summary>按下瞬间的几何快照与宽高换算；内容不可调（坏图等）时拒绝起手。</summary>
    private bool BuildResizeSession(Border card, ResizeGeometry.Edge zone, uint pointerId, out ResizeSession session)
    {
        session = new ResizeSession
        {
            Card = card,
            Handle = zone,
            PointerId = pointerId,
            Cursor = CursorFor(zone)!,
            Geometry = new ResizeGeometry.Session
            {
                Handle = zone,
                StartX = Canvas.GetLeft(card),
                StartY = Canvas.GetTop(card),
                StartW = card.ActualWidth > 0 ? card.ActualWidth : card.Width,
                StartH = card.ActualHeight > 0 ? card.ActualHeight : card.Height
            }
        };
        var g = session.Geometry;
        if (double.IsNaN(g.StartW) || double.IsNaN(g.StartH) || g.StartW <= 0 || g.StartH <= 0)
            return false;
        if (card.DataContext is not ItemVM vm) return false;
        ResizeGeometry.Session typed = vm.Type switch
        {
            // 图片：照片纵横比联动；换算口径与构建一致（照片高 = (卡宽−2)/比率，卡高再叠加上下边框）。
            ItemType.Image when FindChildByName<Border>(card, "ContentPhoto") is { } photo &&
                                  photo.Tag is double ratio && ratio > 0
                => g with { Aspect = true, Ratio = ratio, OverheadW = 2, OverheadH = card.BorderThickness.Top * 2 },
            // 视频：画面表面高 = clamp((卡宽−2)/比率, 140, 760)，其余为时间轴+页脚固定高度。
            ItemType.Video when card.Child is VideoCardView video && video.AspectRatio > 0 &&
                                  g.StartH - video.SurfaceHeight >= 0
                => g with
                {
                    Aspect = true, Ratio = video.AspectRatio, OverheadW = 2,
                    OverheadH = g.StartH - video.SurfaceHeight, ContentMinH = 140, ContentMaxH = 760
                },
            ItemType.Image or ItemType.Video => null!, // 内容缺失（坏图/无视图）：不可调
            _ => g                                    // 文本/音频：只调宽，高度随内容自适应
        };
        if (typed is null) return false;
        session = new ResizeSession
        {
            Card = card,
            Handle = zone,
            PointerId = pointerId,
            Cursor = CursorFor(zone)!,
            Geometry = typed
        };
        return true;
    }

    private void UpdateResize(PointerRoutedEventArgs e)
    {
        if (_resize is not { } s || e.Pointer.PointerId != s.PointerId) return;
        var p = e.GetCurrentPoint(CardCanvas).Position;
        var (width, left, top) = ResizeGeometry.Apply(s.Geometry, p.X, p.Y);
        // 对边锚定：拖左缘锚右缘、拖上缘锚下缘（位置随尺寸反向补偿）。
        if (s.Handle.HasFlag(ResizeGeometry.Edge.West))
            Canvas.SetLeft(s.Card, left);
        if (s.Handle.HasFlag(ResizeGeometry.Edge.North))
            Canvas.SetTop(s.Card, top);
        if (Math.Abs(s.Card.Width - width) > 0.01)
        {
            s.Card.Width = width;
            ApplyCardWidthToContent(s.Card, width);
        }
        SetWindowCursor(s.Cursor);
        e.Handled = true;
    }

    private void EndResizeSession(PointerRoutedEventArgs? e)
    {
        if (_resize is not { } s) return;
        _resize = null;
        var card = s.Card;
        try
        {
            if (e != null) card.ReleasePointerCapture(e.Pointer);
            else card.ReleasePointerCaptures();
        }
        catch { /* 已释放 */ }
        ClearEdgeHover(card, stillOnCard: true);
        // 无条件还原光标：调整结束后指针往往已落在卡外（对边锚定/拖出边界），
        // 卡片不再收到 PointerMoved/Exited，必须在此直接还原，否则光标残留。
        SetWindowCursor(null);
        try { card.UpdateLayout(); } catch { /* Silent */ }
        // 不自动重排：卡片保持原位原样，仅按需扩展画布边界防止内容被裁。
        UpdateCanvasExtent();
        // 宽度已变：重建文本缩放适配基线（换行槽随新宽度失效）。
        ApplyAdaptiveTextScale();
    }

    /// <summary>宽度变化联动内容：图片按原纵横比换高；视频画面表面按宽换高；音频强制收敛
    /// 布局后按新宽度重画波形；文本作废缩放适配基线并复位到基础排版（换行已变，旧放大态不再可信）。</summary>
    private void ApplyCardWidthToContent(Border card, double width)
    {
        if (card.DataContext is ItemVM { Type: ItemType.Image } &&
            FindChildByName<Border>(card, "ContentPhoto") is { } photo && photo.Tag is double ratio && ratio > 0)
            photo.Height = Math.Max(1, (width - 2) / ratio);
        if (card.DataContext is ItemVM { Type: ItemType.Video } && card.Child is VideoCardView video)
            video.ApplySurfaceWidth(width - 2);
        // 音频：调宽后波形画布尺寸变化依赖 SizeChanged 兜底，但捕获/复用时序可能错过；
        // 这里先收敛布局再按当前进度重画，保证柱状图始终铺满新宽度。
        if (card.DataContext is ItemVM { Type: ItemType.Audio } vm &&
            FindChildByName<Canvas>(card, "WaveformCanvas") is { } wave)
        {
            try { card.UpdateLayout(); } catch { /* Silent */ }
            RedrawWaveform(wave, vm, WaveformProgressFor(vm));
        }
        if (FindChildByName<TextBlock>(card, "ContentText") is { } text)
        {
            text.Tag = null; // TextFit 基线随宽度失效，下次适配重测
            text.FontSize = AdaptiveBaseFontSize;
            text.LineHeight = AdaptiveBaseLineHeight;
            if (FindChildByName<TextBlock>(card, "ContentMeta") is { } meta)
            {
                meta.Visibility = Visibility.Visible;
                meta.Opacity = 1.0;
            }
        }
    }
}
