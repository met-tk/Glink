using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Glink.Core;

namespace Glink;

// ====================================================================
//  文本卡片的缩放适配：小缩放比例下充分利用卡片内部空间放大文字、
//  省略次要信息，且卡片框尺寸严格不变（含换行重排，一并不允许）。
//
//  可用区域核算（画布坐标，卡宽 360、Padding 20、正文与日期行距 10）：
//  - 水平：默认换行槽 320；小缩放下给正文负边距（每侧 -12）拓宽到 344
//    （文字距卡缘 8px），文字填满更多卡宽。
//  - 垂直：日期行隐藏后仍占位（10+17px），单行文本放大的字形可溢入这
//    块空间渲染（不参与布局），单行字号上限据此提到 3.0×（45px 字号
//    在 25% 缩放下屏幕约 11px，仍可读）。
//
//  可利用空间 = 内容盒（卡框减去四边内边距，业界 fit-to-container 的
//  共识拟合目标）：换行槽 = 卡宽 − 40，内容盒高 = 卡高 − 40。文字任何
//  时刻不进入内边距——侧边距如此，上下边距同样如此（行高随字号真实
//  增长、整行装进内容盒，不再靠字形溢出行盒渲染）。
//
//  - 单行：小缩放下字号放大（≤3.0×，宽度 ≤ 槽 × 96% 防折行），行高按
//    1.45× 随字号增长且封顶内容盒高；日期行 Collapsed 腾位。文本卡
//    结构性最小高度 116 保证预留空间，卡片框高度恒定零变化。
//  - 多行：换行槽内无宽度增益可用，保持原字号；日期行 Opacity=0 占位。
//  - 缩放 ≥ 0.8 全部还原（字号、行高、日期行）。
//
//  基线（未缩放时的自然宽度与行数）首次适配时测量一次并缓存在
//  TextBlock.Tag；卡片重建（刷新/编辑保存/换字体）后自然重新取基线。
// ====================================================================

public sealed partial class MainWindow
{
    private const double AdaptiveTextMinZoom = 0.8;     // 低于此缩放开始适配
    private const double AdaptiveTextMaxScale = 3.0;    // 单行文本字号最大放大倍数
    private const double AdaptiveWrapSafety = 0.96;     // 防折行边距：放大后宽度 ≤ 换行槽 × 96%
    private const double AdaptiveLineHeightFactor = 1.45; // 行高随字号增长系数（CJK 舒适行距）
    // 换行槽宽度按卡计算（基线携带）：= 卡宽 − 40（两侧内边距各 20）。
    // 文本卡初始宽度随内容自适应（150~360），槽宽每卡不同；360 标准卡即 320。
    private const double AdaptiveBaseFontSize = 15.0;
    private const double AdaptiveBaseLineHeight = 22.0;

    private double _adaptiveSeenZoom = 1.0;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _adaptiveTextTimer;

    /// <summary>文本基线：未缩放时的自然宽度（不换行）、行数，以及该卡自己的换行槽宽度。</summary>
    private sealed record TextFitBaseline(double NaturalWidth, int Lines, double DefaultWrapWidth);

    /// <summary>缩放值变化后去抖重算（滚轮/捏合手势期间不逐帧重排）。</summary>
    private void ScheduleAdaptiveTextScale()
    {
        if (_isListMode || _isWordCloudMode) return;
        var zoom = CanvasScroll.ZoomFactor;
        if (Math.Abs(zoom - _adaptiveSeenZoom) < 0.001) return;
        _adaptiveSeenZoom = zoom;
        _adaptiveTextTimer ??= _dispatcher.CreateTimer();
        _adaptiveTextTimer.IsRepeating = false;
        _adaptiveTextTimer.Interval = TimeSpan.FromMilliseconds(150);
        _adaptiveTextTimer.Tick -= AdaptiveTextTimer_Tick;
        _adaptiveTextTimer.Tick += AdaptiveTextTimer_Tick;
        _adaptiveTextTimer.Stop();
        _adaptiveTextTimer.Start();
    }

    private void AdaptiveTextTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        ApplyAdaptiveTextScale();
    }

    /// <summary>按当前缩放比例调整所有文本卡片的字号与行高（画布模式）。</summary>
    private void ApplyAdaptiveTextScale()
    {
        if (_isListMode || _isWordCloudMode) return;
        var zoom = CanvasScroll.ZoomFactor > 0 ? CanvasScroll.ZoomFactor : 1.0;
        foreach (var card in CardCanvas.Children.OfType<Border>())
        {
            if (card.DataContext is not ItemVM { Type: ItemType.Text } vm) continue;
            if (_editingItem != null && ReferenceEquals(vm, _editingItem)) continue;
            var text = FindChildByName<TextBlock>(card, "ContentText");
            if (text == null) continue;

            if (text.Tag is not TextFitBaseline baseline)
            {
                // 只在默认状态（原字号、无负边距）下取基线，避免把适配态错当基准。
                if (Math.Abs(text.FontSize - AdaptiveBaseFontSize) > 0.01 || text.Margin != default || text.ActualHeight <= 0) continue;
                var wrapping = text.TextWrapping;
                text.TextWrapping = TextWrapping.NoWrap;
                text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var naturalWidth = text.DesiredSize.Width;
                text.TextWrapping = wrapping;
                var defaultWrap = card.Width - 40;
                text.Measure(new Windows.Foundation.Size(defaultWrap, double.PositiveInfinity));
                baseline = new TextFitBaseline(naturalWidth,
                    Math.Max(1, (int)Math.Round(text.DesiredSize.Height / AdaptiveBaseLineHeight)),
                    defaultWrap);
                text.Tag = baseline;
            }

            var meta = FindChildByName<TextBlock>(card, "ContentMeta");
            if (zoom < AdaptiveTextMinZoom && baseline.Lines == 1 && baseline.NaturalWidth > 1)
            {
                // 可读目标：屏幕字号尽量不低于 15 × 0.8 ≈ 12px。
                var target = Math.Clamp(AdaptiveTextMinZoom / zoom, 1.0, AdaptiveTextMaxScale);
                // 拟合目标 = 内容盒（业界 fit-to-container 的共识做法）：
                // - 水平：放大后宽度 ≤ 换行槽（卡宽−40）× 96%，不进侧边距；
                // - 垂直：行高随字号真实增长（font × 1.45），整行装进内容盒高
                //   （卡高−40）——日期行 Collapsed 腾出空间，四边边距真实存在。
                var innerHeight = Math.Max(card.ActualHeight, card.MinHeight) - 40;
                var fontCap = Math.Min(target,
                    Math.Min(baseline.DefaultWrapWidth * AdaptiveWrapSafety / baseline.NaturalWidth,
                        innerHeight / AdaptiveLineHeightFactor));
                var fitted = TryFitFontSize(text, baseline, fontCap / AdaptiveBaseFontSize);
                if (fitted > AdaptiveBaseFontSize)
                {
                    text.LineHeight = Math.Min(Math.Round(fitted * AdaptiveLineHeightFactor, 1), innerHeight);
                    if (meta != null) meta.Visibility = Visibility.Collapsed; // 腾位给放大的正文
                }
                else RestoreText(text, meta);
            }
            else if (text.FontSize != AdaptiveBaseFontSize || text.LineHeight != AdaptiveBaseLineHeight)
            {
                RestoreText(text, meta);
            }
            // 多行文本保持原字号：换行槽内没有宽度增益可用，任何放大都会重排折行。
            // 多行卡的日期行走 Opacity=0（保留占位，卡片高度不变）。
            if (meta is { Visibility: Visibility.Visible } visibleMeta)
            {
                var metaOpacity = zoom < AdaptiveTextMinZoom ? 0.0 : 1.0;
                if (Math.Abs(visibleMeta.Opacity - metaOpacity) > 0.01)
                    visibleMeta.Opacity = metaOpacity;
            }
        }
    }

    /// <summary>还原默认排版：原字号、原行高、日期行回到布局（可见）。</summary>
    private static void RestoreText(TextBlock text, TextBlock? meta)
    {
        text.FontSize = AdaptiveBaseFontSize;
        text.LineHeight = AdaptiveBaseLineHeight;
        if (meta != null)
        {
            meta.Visibility = Visibility.Visible;
            meta.Opacity = 1.0;
        }
    }

    /// <summary>
    /// 从目标字号逐档（0.1）向下试：实测高度必须与基线行数完全一致（多一行
    /// 撑高、少一行缩高，任何一种都不允许），失败则还原原字号。卡片高度由
    /// 此实测锁定，不依赖任何估算；换行测量始终用该卡自己的换行槽（不含侧边距）。
    /// </summary>
    private static double TryFitFontSize(TextBlock text, TextFitBaseline baseline, double kTarget)
    {
        var expectedHeight = baseline.Lines * AdaptiveBaseLineHeight;
        var max = Math.Round(AdaptiveBaseFontSize * kTarget, 1);
        for (var size = max; size > AdaptiveBaseFontSize; size -= 0.1)
        {
            size = Math.Round(size, 1);
            text.FontSize = size;
            text.Measure(new Windows.Foundation.Size(baseline.DefaultWrapWidth, double.PositiveInfinity));
            if (Math.Abs(text.DesiredSize.Height - expectedHeight) <= 0.5)
                return size;
        }
        text.FontSize = AdaptiveBaseFontSize;
        return AdaptiveBaseFontSize;
    }
}
