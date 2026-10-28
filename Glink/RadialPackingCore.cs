namespace Glink;

/// <summary>
/// 向心紧凑平铺的纯几何核心（无 UI 依赖，可被测试工程直接编译引用）。
///
/// 综合了三项公开实践中最完善的思路：
/// 1. Jylänki《A Thousand Ways to Pack the Bin》的 MaxRects：维护极大自由矩形，
///    放置后将所有相交自由矩形分裂为剩余子矩形并修剪被包含者，保证零重叠。
///    本实现把候选位置从常见的"自由矩形左上角"推广到四个角，这是向心对称的关键——
///    右侧空间可以用其左缘、下方空间可以用其上缘贴近中心，卡片才能从洞向四面生长。
/// 2. "从中心向外生长"（最大块居中、其余按面积降序贴合）：面积最大卡片预占容器
///    中心作为"洞"，逐卡选择放置点。
/// 3. 最小包围盒容器搜索（David Colson / Huang & Korf 的做法）：以包围盒面积增量
///    为主评分保持紧凑并趋于方形；容器宽度固定为期望宽与最宽卡片的较大者（任何卡
///    都放得下，杜绝放不下时的死循环），高度按内容面积估算、失败放大 1.2 倍重试、
///    成功后收缩到实际内容高重排，在几轮内收敛到接近最小的包围盒。
/// 评分词典序：包围盒面积增量（紧凑）→ 到洞中心距离（向心）→ 短边/长边贴合
/// （填缝）→ 包围盒最大边（偏方形）→ 位置（确定性）。
/// </summary>
public static class RadialPackingCore
{
    /// <summary>待平铺元素：真实渲染尺寸（不含间隙）。</summary>
    public readonly record struct Item(double Width, double Height);

    /// <summary>平铺结果：<see cref="Index"/> 指回输入列表，X/Y 为卡片左上角。</summary>
    public readonly record struct Placed(int Index, double X, double Y);

    /// <summary>内部统一用含间隙的装箱盒：卡片净距恒 ≥ gap。</summary>
    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    /// <summary>
    /// 向心紧凑平铺。返回每个输入元素的位置；首项恒为面积最大卡片，
    /// 全体坐标已平移为该卡片中心位于 (0,0)（与画布锚点约定一致）。
    /// </summary>
    /// <param name="items">卡片真实尺寸列表（不含间隙）。</param>
    /// <param name="targetWidth">期望容器宽（如视口宽）；最终内容宽不超过它。</param>
    /// <param name="gap">卡片之间的最小净距。</param>
    public static List<Placed> Pack(IReadOnlyList<Item> items, double targetWidth, double gap = 12.0)
    {
        var n = items.Count;
        if (n == 0) return [];

        var sizes = new Box[n];
        for (var i = 0; i < n; i++)
            sizes[i] = new Box(0, 0, items[i].Width + gap, items[i].Height + gap);

        // 面积降序、高降序、索引兜底：大卡先放（密度高），结果完全确定。
        var order = Enumerable.Range(0, n)
            .OrderByDescending(i => sizes[i].Width * sizes[i].Height)
            .ThenByDescending(i => sizes[i].Height)
            .ThenBy(i => i)
            .ToArray();

        var maxW = sizes.Max(s => s.Width);
        var maxH = sizes.Max(s => s.Height);
        var totalArea = sizes.Sum(s => s.Width * s.Height);
        var sumH = sizes.Sum(s => s.Height);

        // 容器宽取期望宽与最宽卡片（含间隙）的较大者：任何卡片都放得下，
        // 消除旧实现里"细长卡片宽于容器 → 永远放不下 → 无限放大重试"的死循环。
        var binW = Math.Max(targetWidth, maxW);
        // 起始高度按内容面积 / (宽 × 经验填充率 0.8) 估算，至少容得下最高卡片。
        var binH = Math.Max(maxH, Math.Ceiling(totalArea / (binW * 0.8)));

        List<Placed>? best = null;
        var shrinks = 0;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var (ok, placed, bounds) = TryPack(sizes, order, binW, binH);
            if (ok)
            {
                best = placed;
                // 成功后把容器高压到实际内容高重排一轮，取更紧凑的包围盒
                //（收缩后过紧会失败，走放大分支，best 保留上一轮成功结果）。
                if (shrinks < 3 && bounds.Height < binH - 1.0)
                {
                    binH = Math.Max(maxH, bounds.Height);
                    shrinks++;
                    continue;
                }
                break;
            }
            // binH ≥ sumH 时单列必能放下全部卡片，故有限步内必然成功；
            // 1.2 倍步进保证严格递增，attempt 上限只是兜底。
            binH = Math.Ceiling(binH * 1.2);
        }

        // 理论不可达的防御路径：以最大卡为中心竖列堆叠，保证 API 永不失败。
        if (best == null)
            best = StackFallback(sizes, order);

        // 平移坐标，使最大卡片（首项）的中心位于 (0,0)。
        // 中心用真实尺寸（不含间隙）计算，与画布锚点 RadialAnchor 的取法一致。
        var anchorCard = items[order[0]];
        var anchor = best[0];
        var ax = anchor.X + anchorCard.Width / 2;
        var ay = anchor.Y + anchorCard.Height / 2;
        for (var i = 0; i < best.Count; i++)
            best[i] = best[i] with { X = best[i].X - ax, Y = best[i].Y - ay };
        return best;
    }

    /// <summary>单轮装箱：洞预占容器中心，其余卡片按 order 在自由矩形四角中择优放置。</summary>
    private static (bool Ok, List<Placed> Placed, Box Bounds) TryPack(
        Box[] sizes, int[] order, double binW, double binH)
    {
        var n = order.Length;
        var holeSize = sizes[order[0]];
        var hole = new Box(
            (binW - holeSize.Width) / 2, (binH - holeSize.Height) / 2,
            holeSize.Width, holeSize.Height);
        var cx = hole.X + holeSize.Width / 2;
        var cy = hole.Y + holeSize.Height / 2;

        var free = new List<Box> { new(0, 0, binW, binH) };
        SplitFreeRects(free, hole);
        PruneContained(free);

        var positions = new Box[n];
        positions[order[0]] = hole;
        var bounds = hole;

        for (var k = 1; k < n; k++)
        {
            var size = sizes[order[k]];
            var found = false;
            (double Grow, double Dist, double ShortFit, double LongFit, double MaxSide, double Y, double X) bestKey = default;
            var bestBox = default(Box);

            foreach (var fr in free)
            {
                if (fr.Width < size.Width || fr.Height < size.Height) continue;
                for (var corner = 0; corner < 4; corner++)
                {
                    // 四角候选：右下角（3）对齐自由矩形右/下缘，其余类推。
                    var px = (corner & 1) != 0 ? fr.Right - size.Width : fr.X;
                    var py = corner >= 2 ? fr.Bottom - size.Height : fr.Y;
                    var left = Math.Min(bounds.X, px);
                    var top = Math.Min(bounds.Y, py);
                    var right = Math.Max(bounds.Right, px + size.Width);
                    var bottom = Math.Max(bounds.Bottom, py + size.Height);
                    var dx = px + size.Width / 2 - cx;
                    var dy = py + size.Height / 2 - cy;
                    var key = (
                        (right - left) * (bottom - top) - bounds.Width * bounds.Height,
                        dx * dx + dy * dy,
                        Math.Min(fr.Width - size.Width, fr.Height - size.Height),
                        Math.Max(fr.Width - size.Width, fr.Height - size.Height),
                        Math.Max(right - left, bottom - top),
                        py, px);
                    if (!found || key.CompareTo(bestKey) < 0)
                    {
                        found = true;
                        bestKey = key;
                        bestBox = new Box(px, py, size.Width, size.Height);
                    }
                }
            }

            if (!found) return (false, [], default);
            positions[order[k]] = bestBox;
            bounds = new Box(
                Math.Min(bounds.X, bestBox.X), Math.Min(bounds.Y, bestBox.Y),
                Math.Max(bounds.Right, bestBox.Right) - Math.Min(bounds.X, bestBox.X),
                Math.Max(bounds.Bottom, bestBox.Bottom) - Math.Min(bounds.Y, bestBox.Y));
            SplitFreeRects(free, bestBox);
            PruneContained(free);
        }

        var placed = new List<Placed>(n);
        foreach (var i in order)
            placed.Add(new Placed(i, positions[i].X, positions[i].Y));
        return (true, placed, bounds);
    }

    /// <summary>防御回退：全部卡片竖列堆叠（零重叠、永不失败）；最终仍按最大卡中心平移到原点。</summary>
    private static List<Placed> StackFallback(Box[] sizes, int[] order)
    {
        var placed = new List<Placed>(order.Length);
        var y = 0.0;
        foreach (var i in order)
        {
            placed.Add(new Placed(i, 0, y));
            y += sizes[i].Height;
        }
        return placed;
    }

    /// <summary>MaxRects 分裂：将与 placed 相交的自由矩形分裂为至多四个剩余子矩形。</summary>
    private static void SplitFreeRects(List<Box> free, Box placed)
    {
        for (var i = free.Count - 1; i >= 0; i--)
        {
            var current = free[i];
            if (current.X >= placed.Right || current.Right <= placed.X ||
                current.Y >= placed.Bottom || current.Bottom <= placed.Y)
                continue;
            free.RemoveAt(i);
            if (placed.X > current.X)
                free.Add(new Box(current.X, current.Y, placed.X - current.X, current.Height));
            if (placed.Right < current.Right)
                free.Add(new Box(placed.Right, current.Y, current.Right - placed.Right, current.Height));
            if (placed.Y > current.Y)
                free.Add(new Box(current.X, current.Y, current.Width, placed.Y - current.Y));
            if (placed.Bottom < current.Bottom)
                free.Add(new Box(current.X, placed.Bottom, current.Width, current.Bottom - placed.Bottom));
        }
    }

    /// <summary>修剪互相包含的冗余自由矩形，保持自由矩形集合极小。</summary>
    private static void PruneContained(List<Box> free)
    {
        for (var i = 0; i < free.Count; i++)
        {
            for (var j = i + 1; j < free.Count; j++)
            {
                if (Contains(free[j], free[i]))
                {
                    free.RemoveAt(i);
                    i--;
                    break;
                }
                if (Contains(free[i], free[j]))
                {
                    free.RemoveAt(j);
                    j--;
                }
            }
        }
    }

    private static bool Contains(Box outer, Box inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
