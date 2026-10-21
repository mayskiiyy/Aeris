using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aeris;

internal sealed class MiddleButtonScroll
{
    private static MiddleButtonScroll? active;
    private readonly ScrollViewer viewer;
    private readonly Window window;
    private readonly Point origin;
    private Point offset;
    private readonly Cursor? previousCursor;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private bool dragged;

    public static void Register() => EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent,
        new MouseButtonEventHandler(MouseDown), true);

    private static void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (active != null)
        {
            active.Stop();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle) return;
        for (var node = e.OriginalSource as DependencyObject; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer scroll || (scroll.ScrollableHeight <= 0 && scroll.ScrollableWidth <= 0)) continue;
            if (!scroll.CaptureMouse()) return;
            active = new MiddleButtonScroll(scroll, (Window)sender, e.GetPosition(scroll));
            e.Handled = true;
            return;
        }
    }

    private MiddleButtonScroll(ScrollViewer viewer, Window window, Point origin)
    {
        this.viewer = viewer;
        this.window = window;
        this.origin = origin;
        offset = new Point(viewer.HorizontalOffset, viewer.VerticalOffset);
        previousCursor = viewer.Cursor;
        viewer.Cursor = Cursors.ScrollAll;
        viewer.PreviewMouseUp += MouseUp;
        viewer.LostMouseCapture += LostCapture;
        viewer.Unloaded += Unloaded;
        window.PreviewKeyDown += KeyDown;
        window.Deactivated += Deactivated;
        timer.Tick += Tick;
        timer.Start();
    }

    // Viewport units also handle virtualized lists, whose offsets count rows rather than pixels.
    internal static double Step(double distance, double viewport, double length, double seconds) =>
        Math.Sign(distance) * Math.Min(1800, Math.Max(0, Math.Abs(distance) - 8) * 10) *
        viewport / Math.Max(1, length) * seconds;

    private void Tick(object? sender, EventArgs e)
    {
        var delta = Mouse.GetPosition(viewer) - origin;
        if (Mouse.MiddleButton == MouseButtonState.Pressed && delta.Length > 8) dragged = true;
        double seconds = Math.Min(elapsed.Elapsed.TotalSeconds, 0.05);
        elapsed.Restart();
        offset.X = Math.Clamp(offset.X + Step(delta.X, viewer.ViewportWidth, viewer.ActualWidth, seconds), 0, viewer.ScrollableWidth);
        offset.Y = Math.Clamp(offset.Y + Step(delta.Y, viewer.ViewportHeight, viewer.ActualHeight, seconds), 0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(offset.Y);
        viewer.ScrollToHorizontalOffset(offset.X);
    }

    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((e.GetPosition(viewer) - origin).Length > 8) dragged = true;
        if (dragged) Stop();
        e.Handled = true;
    }

    private void KeyDown(object sender, KeyEventArgs e) { Stop(); e.Handled = true; }
    private void LostCapture(object sender, MouseEventArgs e) => Stop();
    private void Unloaded(object sender, RoutedEventArgs e) => Stop();
    private void Deactivated(object? sender, EventArgs e) => Stop();

    private void Stop()
    {
        active = null;
        timer.Stop();
        timer.Tick -= Tick;
        viewer.PreviewMouseUp -= MouseUp;
        viewer.LostMouseCapture -= LostCapture;
        viewer.Unloaded -= Unloaded;
        window.PreviewKeyDown -= KeyDown;
        window.Deactivated -= Deactivated;
        viewer.Cursor = previousCursor;
        if (viewer.IsMouseCaptured) viewer.ReleaseMouseCapture();
    }
}
