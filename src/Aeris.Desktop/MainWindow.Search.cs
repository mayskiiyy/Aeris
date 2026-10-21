using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aeris.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer statusDismiss = new() { Interval = TimeSpan.FromSeconds(10) };

    private void InitializeStatusBar()
    {
        Status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) RefreshStatusBar(); };
        statusDismiss.Tick += (_, _) => { statusDismiss.Stop(); StatusBar.IsVisible = false; };
        Closed += (_, _) => statusDismiss.Stop();
    }

    private void RefreshStatusBar()
    {
        statusDismiss.Stop();
        StatusBar.IsVisible = operation != null || !string.IsNullOrWhiteSpace(Status.Text);
        if (operation == null && StatusBar.IsVisible) statusDismiss.Start();
    }

    private void DesktopTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && CacheSearchBox != null) CacheSearchBox.IsVisible = Tabs.SelectedIndex == 1;
    }

    private void WindowSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || e.KeyModifiers != KeyModifiers.Control) return;
        Tabs.SelectedIndex = 1;
        CacheSearch.Focus();
        CacheSearch.SelectAll();
        e.Handled = true;
    }

    private void ClearCacheSearch(object? sender, RoutedEventArgs e)
    {
        CacheSearch.Text = "";
        CacheSearch.Focus();
    }

    private void CacheSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CacheSearch.Text = "";
        e.Handled = true;
    }
}
