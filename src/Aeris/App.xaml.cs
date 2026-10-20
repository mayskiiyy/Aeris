using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aeris;

public partial class App : Application
{
    public bool DarkTheme { get; private set; }
    public static string ThemeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "theme.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.Apply(Resources, Loc.Preferred());
        bool dark = true;
        try { if (File.Exists(ThemeFile)) dark = File.ReadAllText(ThemeFile).Trim() == "dark"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        SetTheme(dark);
        base.OnStartup(e);
    }

    public void SetTheme(bool dark, bool animate = false)
    {
        var current = Resources.MergedDictionaries[0];
        var next = new ResourceDictionary { Source = new Uri($"/Aeris;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (animate && SystemParameters.ClientAreaAnimation)
            foreach (var key in next.Keys.Cast<object>().ToArray())
                if (next[key] is SolidColorBrush target && current[key] is SolidColorBrush previous)
                {
                    var brush = new SolidColorBrush(target.Color);
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous.Color, target.Color, TimeSpan.FromMilliseconds(240))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
                    next[key] = brush;
                }
        Resources.MergedDictionaries[0] = next;
        DarkTheme = dark;
    }

    private void HoverFeedback(object sender, MouseEventArgs e)
    {
        if (sender is Control control) AnimateHover(control, e);
    }

    private void NavigationHover(object sender, MouseEventArgs e)
    {
        if (sender is Border { TemplatedParent: Control control }) AnimateHover(control, e);
    }

    private static void AnimateHover(Control control, MouseEventArgs e)
    {
        if (control.Template.FindName("Hover", control) is not Border hover) return;
        double target = e.RoutedEvent == UIElement.MouseEnterEvent && control.IsEnabled ? 1 : 0;
        double current = hover.Opacity;
        hover.BeginAnimation(UIElement.OpacityProperty, null);
        hover.Opacity = current;
        if (!SystemParameters.ClientAreaAnimation) { hover.Opacity = target; return; }
        hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
    }
}
