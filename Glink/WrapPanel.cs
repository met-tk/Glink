using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Glink;

/// <summary>
/// 极简换行面板：子元素按自然宽度从左到右排列，放不下时换行。
/// WinUI 3 没有内置 WrapPanel，供列表模式的画廊视图使用。
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double contentWidth = 0, contentHeight = 0, lineWidth = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + Spacing + size.Width > limit)
            {
                contentHeight += lineHeight + Spacing;
                lineWidth = 0;
                lineHeight = 0;
            }
            lineWidth = lineWidth == 0 ? size.Width : lineWidth + Spacing + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            contentWidth = Math.Max(contentWidth, lineWidth);
        }
        return new Size(Math.Min(contentWidth, double.IsInfinity(limit) ? contentWidth : limit), contentHeight + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + Spacing + size.Width > finalSize.Width + 0.5)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            var placeX = x == 0 ? 0 : x + Spacing;
            child.Arrange(new Rect(placeX, y, size.Width, size.Height));
            x = placeX + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(finalSize.Width, y + lineHeight);
    }
}
