namespace Aeris;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    public TreemapRect Inset(double amount) => new(X + amount, Y + amount, Math.Max(0, Width - 2 * amount), Math.Max(0, Height - 2 * amount));
}

/// <summary>A laid-out node. <see cref="Node"/> is -1 for the block that gathers everything too small to draw.</summary>
public readonly record struct TreemapCell(int Node, TreemapRect Rect, int Depth, long Size, int SmallCount);

/// <summary>Squarified treemap layout (Bruls, Huizing and van Wijk), nested a few levels deep.</summary>
public static class Treemap
{
    /// <summary>
    /// Splits <paramref name="bounds"/> between <paramref name="sizes"/> (sorted largest first) so each rectangle is
    /// as close to square as possible. Returns one rectangle per size, in the same order.
    /// </summary>
    public static TreemapRect[] Squarify(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        var result = new TreemapRect[sizes.Count];
        double total = sizes.Sum();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        double scale = bounds.Area / total;
        var free = bounds;
        int start = 0;
        while (start < sizes.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            int end = start;
            double rowSum = 0, worst = double.MaxValue;
            while (end < sizes.Count)
            {
                double area = sizes[end] * scale;
                double sum = rowSum + area;
                double next = Worst(sizes, start, end, sum, side, scale);
                if (end > start && next > worst) break;
                worst = next; rowSum = sum; end++;
            }
            free = LayRow(sizes, start, end, rowSum, free, scale, result);
            start = end;
        }
        return result;
    }

    private static double Worst(IReadOnlyList<double> sizes, int start, int end, double sum, double side, double scale)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i <= end; i++) { double a = sizes[i] * scale; max = Math.Max(max, a); min = Math.Min(min, a); }
        if (min <= 0 || sum <= 0) return double.MaxValue;
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static TreemapRect LayRow(IReadOnlyList<double> sizes, int start, int end, double rowArea, TreemapRect free, double scale, TreemapRect[] result)
    {
        bool horizontal = free.Width >= free.Height;
        double thickness = horizontal ? rowArea / free.Height : rowArea / free.Width;
        double offset = 0;
        for (int i = start; i < end; i++)
        {
            double length = rowArea <= 0 ? 0 : sizes[i] * scale / thickness;
            result[i] = horizontal
                ? new(free.X, free.Y + offset, thickness, length)
                : new(free.X + offset, free.Y, length, thickness);
            offset += length;
        }
        return horizontal
            ? new(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height)
            : new(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
    }

    /// <summary>
    /// Lays out <paramref name="root"/>'s content down to <paramref name="maxDepth"/> levels. Children smaller than
    /// <paramref name="minSide"/> pixels on a side are merged into one "small files" cell per folder.
    /// </summary>
    public static List<TreemapCell> Layout(NodeStore nodes, int root, TreemapRect bounds, bool onDisk, int maxDepth = 10, double minSide = 3, double padding = 1.5, int maxItems = int.MaxValue)
    {
        var cells = new List<TreemapCell>();
        void Visit(int node, TreemapRect rect, int depth)
        {
            var children = nodes.Children(node).Where(c => nodes.Size(c, onDisk) > 0).OrderByDescending(c => nodes.Size(c, onDisk)).ToList();
            if (children.Count == 0) return;
            double total = children.Sum(c => (double)nodes.Size(c, onDisk));
            double minArea = minSide * minSide * total / Math.Max(1, rect.Area);
            int keep = Math.Min(children.Count, Math.Max(1, maxItems));
            while (keep > 1 && nodes.Size(children[keep - 1], onDisk) < minArea) keep--;
            var sizes = children.Take(keep).Select(c => (double)nodes.Size(c, onDisk)).ToList();
            long small = children.Skip(keep).Sum(c => nodes.Size(c, onDisk));
            if (small > 0) sizes.Add(small);
            var rects = Squarify(sizes, rect);
            for (int i = 0; i < keep; i++)
            {
                int child = children[i];
                var r = rects[i];
                if (r.Width < 1 || r.Height < 1) continue;
                cells.Add(new(child, r, depth, nodes.Size(child, onDisk), 0));
                var inner = r.Inset(padding);
                if (nodes.IsDirectory(child) && depth + 1 < maxDepth && inner.Width >= minSide * 4 && inner.Height >= minSide * 4)
                {
                    double header = inner.Width >= 60 && inner.Height >= 48 ? 15 : 0;
                    Visit(child, new(inner.X, inner.Y + header, inner.Width, inner.Height - header), depth + 1);
                }
            }
            if (small > 0 && rects[^1].Width >= 1 && rects[^1].Height >= 1) cells.Add(new(-1, rects[^1], depth, small, children.Count - keep));
        }
        Visit(root, bounds, 0);
        return cells;
    }
}
