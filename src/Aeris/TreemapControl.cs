using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Aeris;

/// <summary>
/// A squarified treemap of one folder of a <see cref="ScanResult"/>, drawn in a single OnRender pass so tens of
/// thousands of rectangles cost no WPF elements. Colors come from the theme's Kind.* brushes.
/// </summary>
public class TreemapControl : FrameworkElement
{
    private List<TreemapCell> cells = [];
    private ScanResult? result;
    private int root;
    private bool onDisk;
    private int hovered = -1;
    private readonly ToolTip tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, HorizontalOffset = 14, VerticalOffset = 14 };

    public event Action<int>? FolderOpened;
    public event Action<int>? NodeSelected;
    public event Action<int>? FileActivated;

    public int Selected { get; private set; } = -1;

    public TreemapControl()
    {
        ToolTip = tip;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
        Focusable = true;
        FocusVisualStyle = null;
        SizeChanged += (_, _) => Relayout();
    }

    public void Show(ScanResult? scan, int folder, bool disk)
    {
        result = scan;
        root = folder;
        onDisk = disk;
        Selected = -1;
        Relayout();
    }

    public void Select(int node)
    {
        Selected = node;
        InvalidateVisual();
    }

    /// <summary>Called after a theme change so the cached brushes are read again.</summary>
    public void Restyle() => InvalidateVisual();

    private void Relayout()
    {
        cells = result == null || ActualWidth < 4 || ActualHeight < 4 ? [] : Treemap.Layout(result.Nodes, root, new(0, 0, ActualWidth, ActualHeight), onDisk, maxDepth: 1, minSide: 36, maxItems: 24);
        hovered = -1;
        InvalidateVisual();
    }

    private Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        var canvas = Resource("Surface");
        dc.DrawRectangle(canvas, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (result == null) return;
        var nodes = result.Nodes;
        var kinds = Enum.GetValues<FileKind>().Select(k => Resource($"Kind.{k}")).ToArray();
        var ink = Resource("Ink");
        var muted = Resource("Muted");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        long total = Math.Max(1, nodes.Size(root, onDisk));
        foreach (var cell in cells)
        {
            var bounds = cell.Rect.Inset(3);
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            var rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            bool directory = cell.Node >= 0 && nodes.IsDirectory(cell.Node);
            var color = cell.Node < 0 ? Resource("Kind.Other") : directory ? Resource("Accent") : kinds[(int)nodes.Kind(cell.Node)];
            var fill = color.Clone(); fill.Opacity = 0.14; fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, rect, 6, 6);
            dc.DrawRectangle(color, null, new Rect(rect.X + 6, rect.Y + 6, Math.Max(0, rect.Width - 12), 2));
            if (rect.Width >= 85 && rect.Height >= 52)
            {
                string name = cell.Node < 0 ? Loc.Format("Map.OtherItems", cell.SmallCount) : nodes.Name(cell.Node);
                var label = new FormattedText(name, Loc.Culture, FlowDirection.LeftToRight, typeface, 14, ink, dpi)
                { MaxTextWidth = rect.Width - 20, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(label, new Point(rect.X + 10, rect.Y + 14));
                string detail = $"{Storage.Format(cell.Size)} · {100.0 * cell.Size / total:0.#}%";
                var size = new FormattedText(detail, Loc.Culture, FlowDirection.LeftToRight, typeface, 12, muted, dpi)
                { MaxTextWidth = rect.Width - 20, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(size, new Point(rect.X + 10, rect.Y + 34));
            }
        }
        if (cells.Count == 0)
        {
            var empty = new FormattedText(Loc.Get("Map.Empty"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 13, muted, dpi);
            dc.DrawText(empty, new Point((ActualWidth - empty.Width) / 2, (ActualHeight - empty.Height) / 2));
        }
        int selected = Selected < 0 ? -1 : cells.FindIndex(c => c.Node == Selected);
        DrawOutline(dc, selected, Resource("Accent"), 2.5);
        if (hovered != selected) DrawOutline(dc, hovered, Resource("Focus"), 1.5);
    }

    private void DrawOutline(DrawingContext dc, int index, Brush brush, double thickness)
    {
        if (index < 0 || index >= cells.Count) return;
        var r = cells[index].Rect.Inset(thickness / 2);
        dc.DrawRectangle(null, new Pen(brush, thickness), new Rect(r.X, r.Y, r.Width, r.Height));
    }

    /// <summary>Index of the deepest cell under the point, or -1. Children come after their folder in the list.</summary>
    private int HitIndex(Point point)
    {
        for (int i = cells.Count - 1; i >= 0; i--)
            if (cells[i].Rect.Contains(point.X, point.Y)) return i;
        return -1;
    }

    private TreemapCell? HitTest(Point point) => HitIndex(point) is var i and >= 0 ? cells[i] : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (result == null) return;
        int index = HitIndex(e.GetPosition(this));
        if (index == hovered) return;
        hovered = index;
        InvalidateVisual();
        tip.IsOpen = false;
        if (index < 0) return;
        tip.Content = Describe(cells[index]);
        tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hovered = -1;
        tip.IsOpen = false;
        InvalidateVisual();
    }

    private string Describe(TreemapCell cell)
    {
        var nodes = result!.Nodes;
        long parentSize = Math.Max(1, cell.Node >= 0 && nodes.Parent(cell.Node) >= 0 ? nodes.Size(nodes.Parent(cell.Node), onDisk) : nodes.Size(root, onDisk));
        if (cell.Node < 0) return Loc.Format("Map.OtherItemsTip", cell.SmallCount, Storage.Format(cell.Size));
        string path = nodes.FullPath(cell.Node);
        double share = 100.0 * nodes.Size(cell.Node, onDisk) / parentSize;
        return nodes.IsDirectory(cell.Node)
            ? Loc.Format("Map.FolderTip", path, Storage.Format(nodes.Size(cell.Node, onDisk)), share, nodes.Files(cell.Node))
            : Loc.Format("Map.FileTip", path, Storage.Format(nodes.Size(cell.Node, onDisk)), share, Loc.Get($"FileKind.{nodes.Kind(cell.Node)}"));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        if (result == null || HitTest(e.GetPosition(this)) is not { Node: >= 0 } cell) return;
        Select(cell.Node);
        NodeSelected?.Invoke(cell.Node);
        if (result.Nodes.IsDirectory(cell.Node)) FolderOpened?.Invoke(cell.Node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (result != null && HitTest(e.GetPosition(this)) is { Node: >= 0 } cell) { Select(cell.Node); NodeSelected?.Invoke(cell.Node); }
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && result != null && HitTest(e.GetPosition(this)) is { Node: >= 0 } cell && !result.Nodes.IsDirectory(cell.Node)) FileActivated?.Invoke(cell.Node);
    }
}
