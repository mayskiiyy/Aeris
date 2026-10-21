using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Aeris;

public partial class MainWindow
{
    private readonly DispatcherTimer statusDismiss = new() { Interval = TimeSpan.FromSeconds(10) };

    private void InitializeStatusBar()
    {
        var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        textProperty.AddValueChanged(Status, StatusChanged);
        statusDismiss.Tick += (_, _) => { statusDismiss.Stop(); StatusBar.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => { statusDismiss.Stop(); textProperty.RemoveValueChanged(Status, StatusChanged); };
    }

    private void StatusChanged(object? sender, EventArgs e) => RefreshStatusBar();

    private void RefreshStatusBar()
    {
        statusDismiss.Stop();
        StatusBar.Visibility = busy || !string.IsNullOrWhiteSpace(Status.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!busy && StatusBar.Visibility == Visibility.Visible) statusDismiss.Start();
    }

    private void WindowSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        MainTabs.SelectedItem = CacheTab;
        CacheSearch.Focus();
        CacheSearch.SelectAll();
        e.Handled = true;
    }

    private void ClearCacheSearch(object sender, RoutedEventArgs e)
    {
        CacheSearch.Clear();
        CacheSearch.Focus();
    }

    private void CacheSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CacheSearch.Clear();
        e.Handled = true;
    }
}
