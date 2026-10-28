using Windows.Foundation;
using Glink.Core;

namespace Glink;

/// <summary>
/// MaxRects 二维装箱算法引擎，用于自由画布的紧凑平铺。
/// 基于物理测量与最优自由矩形切割，实现零重叠、最高密度的紧凑平铺。
/// </summary>
public static class BinPackingLayoutEngine
{
    /// <summary>一张卡片的装箱计算结果。</summary>
    public readonly record struct PackedPlacement(
        Microsoft.UI.Xaml.Controls.Border Card,
        double TargetX,
        double TargetY,
        int TargetZIndex);

    /// <summary>获取卡片的精确渲染宽度或预排宽度。</summary>
    public static double GetCardWidth(Microsoft.UI.Xaml.Controls.Border card)
    {
        if (card.ActualWidth > 0)
            return card.ActualWidth;
        if (!double.IsNaN(card.Width) && card.Width > 0)
            return card.Width;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return card.DesiredSize.Width > 0 ? card.DesiredSize.Width : 380.0;
    }

    /// <summary>获取卡片的精确渲染高度或即时测量高度，消除粗糙估算导致的间隙。</summary>
    public static double GetCardHeight(Microsoft.UI.Xaml.Controls.Border card)
    {
        if (card.ActualHeight > 0)
            return card.ActualHeight;
        if (!double.IsNaN(card.Height) && card.Height > 0)
            return card.Height;

        double w = GetCardWidth(card);
        card.Measure(new Size(w, double.PositiveInfinity));
        if (card.DesiredSize.Height > 0)
            return card.DesiredSize.Height;

        if (card.DataContext is ItemVM vm)
        {
            return vm.Type switch
            {
                ItemType.Text => 80.0,
                ItemType.Audio => 100.0,
                ItemType.Video => 260.0,
                ItemType.Image => 260.0,
                _ => 160.0
            };
        }
        return 160.0;
    }

    /// <summary>
    /// MaxRects (Best-Short-Side-Fit / Top-Left) 二维装箱算法，
    /// 将异构尺寸的卡片以零重叠、最大密度紧密排列。
    /// </summary>
    public static List<PackedPlacement> ComputeCompactBinPacking(
        IList<Microsoft.UI.Xaml.Controls.Border> cards,
        double targetContainerWidth,
        double gap = 12.0,
        double originX = 24.0,
        double originY = 24.0)
    {
        var results = new List<PackedPlacement>(cards.Count);
        if (cards.Count == 0)
            return results;

        // Fit the viewport rather than growing horizontally with the number of items.
        // Include one trailing gap so a card may end exactly at the usable right edge.
        double maxSingleWidth = cards.Max(c => GetCardWidth(c) + gap);
        double binWidth = Math.Max(maxSingleWidth, targetContainerWidth - originX * 2 + gap);

        // 按最大边长降序、面积降序排列（大卡先放，提高整体装箱密度）
        var sortedCards = cards
            .OrderByDescending(c => Math.Max(GetCardWidth(c), GetCardHeight(c)))
            .ThenByDescending(c => GetCardWidth(c) * GetCardHeight(c))
            .ToList();

        // 自由矩形列表：初始为一个半无限长条
        var freeRects = new List<Rect>
        {
            new Rect(0, 0, binWidth, 100000.0)
        };

        int z = 1;
        foreach (var card in sortedCards)
        {
            double w = GetCardWidth(card) + gap;
            double h = GetCardHeight(card) + gap;

            // Top-Left + Best-Short-Side-Fit 启发式搜索最优放置位置
            int bestIndex = -1;
            double bestY = double.MaxValue;
            double bestX = double.MaxValue;
            double bestShortSideFit = double.MaxValue;

            for (int i = 0; i < freeRects.Count; i++)
            {
                Rect free = freeRects[i];
                if (free.Width >= w && free.Height >= h)
                {
                    double leftoverHoriz = Math.Abs(free.Width - w);
                    double leftoverVert = Math.Abs(free.Height - h);
                    double shortSideFit = Math.Min(leftoverHoriz, leftoverVert);

                    // 优先最靠上(Y最小)，其次最靠左(X最小)，最后选贴合度最高
                    if (free.Y < bestY - 1.0 ||
                        (Math.Abs(free.Y - bestY) <= 1.0 && free.X < bestX - 1.0) ||
                        (Math.Abs(free.Y - bestY) <= 1.0 && Math.Abs(free.X - bestX) <= 1.0 && shortSideFit < bestShortSideFit))
                    {
                        bestIndex = i;
                        bestY = free.Y;
                        bestX = free.X;
                        bestShortSideFit = shortSideFit;
                    }
                }
            }

            Rect placedRect;
            if (bestIndex != -1)
            {
                placedRect = new Rect(freeRects[bestIndex].X, freeRects[bestIndex].Y, w, h);
            }
            else
            {
                // 回退：放在当前已排列区域底部
                double maxY = results.Count > 0
                    ? results.Max(r => (r.TargetY - originY) + GetCardHeight(r.Card) + gap)
                    : 0;
                placedRect = new Rect(0, maxY, w, h);
            }

            results.Add(new PackedPlacement(
                card,
                Math.Round(originX + placedRect.X),
                Math.Round(originY + placedRect.Y),
                z++));

            // MaxRects 分裂规则：将与已放置矩形相交的自由矩形分裂为剩余子矩形
            SplitFreeRects(freeRects, placedRect);
            PruneContainedFreeRects(freeRects);
        }

        return results;
    }

    /// <summary>MaxRects 分裂：将与 placed 相交的自由矩形分裂为剩余子矩形。</summary>
    private static void SplitFreeRects(List<Rect> freeRects, Rect placedRect)
    {
        for (int i = freeRects.Count - 1; i >= 0; i--)
        {
            if (!Intersects(freeRects[i], placedRect)) continue;
            Rect current = freeRects[i];
            freeRects.RemoveAt(i);
            if (placedRect.X > current.X)
                freeRects.Add(new Rect(current.X, current.Y, placedRect.X - current.X, current.Height));
            if (placedRect.Right < current.Right)
                freeRects.Add(new Rect(placedRect.Right, current.Y, current.Right - placedRect.Right, current.Height));
            if (placedRect.Y > current.Y)
                freeRects.Add(new Rect(current.X, current.Y, current.Width, placedRect.Y - current.Y));
            if (placedRect.Bottom < current.Bottom)
                freeRects.Add(new Rect(current.X, placedRect.Bottom, current.Width, current.Bottom - placedRect.Bottom));
        }
    }

    private static bool Intersects(Rect a, Rect b)
    {
        return a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    }

    /// <summary>
    /// 向心紧凑平铺：面积最大卡片居于中心，其余卡片围绕它从内向外对称生长。
    /// 几何算法在 <see cref="RadialPackingCore"/>（MaxRects 四角候选 + 向心评分 +
    /// 最小包围盒容器搜索），这里只负责用物理测量尺寸调用它。
    /// 返回坐标已平移为最大卡片中心在 (0,0)，首个结果即最大卡片（锚点约定）；
    /// Z 按面积降序 1..n，与左上模式一致。
    /// </summary>
    public static List<PackedPlacement> ComputeRadialPacking(
        IList<Microsoft.UI.Xaml.Controls.Border> cards, double binWidth, double gap = 12.0)
    {
        var results = new List<PackedPlacement>(cards.Count);
        if (cards.Count == 0)
            return results;

        var items = new List<RadialPackingCore.Item>(cards.Count);
        foreach (var card in cards)
            items.Add(new RadialPackingCore.Item(GetCardWidth(card), GetCardHeight(card)));

        var placed = RadialPackingCore.Pack(items, binWidth, gap);
        for (var i = 0; i < placed.Count; i++)
        {
            var p = placed[i];
            results.Add(new PackedPlacement(cards[p.Index], Math.Round(p.X), Math.Round(p.Y), i + 1));
        }
        return results;
    }

    private static void PruneContainedFreeRects(List<Rect> rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                if (IsContainedIn(rects[i], rects[j]))
                {
                    rects.RemoveAt(i);
                    i--;
                    break;
                }
                if (IsContainedIn(rects[j], rects[i]))
                {
                    rects.RemoveAt(j);
                    j--;
                }
            }
        }
    }

    private static bool IsContainedIn(Rect a, Rect b)
    {
        return a.X >= b.X && a.Y >= b.Y && a.Right <= b.Right && a.Bottom <= b.Bottom;
    }
}
