using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Aeris.Desktop;

public sealed class DiskMap : Control
{
    private ScanResult? scan;
    private int root;
    public event Action<ScanResult, int>? Navigated;
    private IReadOnlyList<TreemapCell> cells = [];
    private readonly IBrush[] colors = [Brush.Parse("#4976A8"), Brush.Parse("#527D65"), Brush.Parse("#A67B4B"), Brush.Parse("#826C9E"), Brush.Parse("#4D8D99"), Brush.Parse("#A46B73")];

    public DiskMap()
    {
        ClipToBounds = true;
        Focusable = true;
        KeyDown += (_, e) => { if (e.Key == Key.Back) { Up(); e.Handled = true; } };
        PointerPressed += (_, e) =>
        {
            if (scan == null) return;
            Focus();
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { Up(); return; }
            var point = e.GetPosition(this);
            var hit = cells.LastOrDefault(c => c.Node >= 0 && c.Rect.Contains(point.X, point.Y));
            if (hit.Size > 0 && scan.Nodes.IsDirectory(hit.Node)) Show(scan, hit.Node);
        };
        PointerMoved += (_, e) =>
        {
            if (scan == null) return;
            var point = e.GetPosition(this);
            var hit = cells.LastOrDefault(c => c.Node >= 0 && c.Rect.Contains(point.X, point.Y));
            ToolTip.SetTip(this, hit.Size > 0 ? $"{scan.Nodes.FullPath(hit.Node)}\n{Storage.Format(hit.Size)}" : scan.Nodes.FullPath(root));
        };
    }

    public void Show(ScanResult result, int node) { scan = result; root = node; InvalidateVisual(); Navigated?.Invoke(result, node); }
    public void Up() { if (scan != null && scan.Nodes.Parent(root) >= 0) Show(scan, scan.Nodes.Parent(root)); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (scan == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        cells = Treemap.Layout(scan.Nodes, root, new(0, 0, Bounds.Width, Bounds.Height), false, maxDepth: 1, minSide: 36, maxItems: 24);
        foreach (var cell in cells)
        {
            var rect = cell.Rect.Inset(1);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var color = cell.Node < 0 ? Brushes.Gray : colors[(int)scan.Nodes.Kind(cell.Node) % colors.Length];
            var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            if (color is ISolidColorBrush solid) context.FillRectangle(new SolidColorBrush(solid.Color, 0.18), bounds);
            context.FillRectangle(color, new Rect(rect.X, rect.Y, rect.Width, 3));
            if (rect.Width >= 85 && rect.Height >= 52)
            {
                var ink = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Brushes.White : Brushes.Black;
                string name = cell.Node < 0 ? Loc.Format("Map.OtherItems", cell.SmallCount) : scan.Nodes.Name(cell.Node);
                var label = new FormattedText(name, Loc.Culture, FlowDirection.LeftToRight, new Typeface("Inter"), 14, ink)
                { MaxTextWidth = rect.Width - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                context.DrawText(label, new Point(rect.X + 8, rect.Y + 12));
                var size = new FormattedText(Storage.Format(cell.Size), Loc.Culture, FlowDirection.LeftToRight, new Typeface("Inter"), 12, ink)
                { MaxTextWidth = rect.Width - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                context.DrawText(size, new Point(rect.X + 8, rect.Y + 32));
            }
        }
    }
}
