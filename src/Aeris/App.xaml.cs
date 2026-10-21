using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aeris;

public partial class App : Application
{
    private Mutex? instance;
    private readonly CancellationTokenSource listening = new();
    public bool DarkTheme { get; private set; }
    public static string ThemeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "theme.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--scheduled-clean") { Shutdown(RunScheduled(e.Args[1])); return; }
        instance = new Mutex(true, SingleInstance.MutexName, out bool first);
        if (!first && SingleInstance.Send(e.Args)) { Shutdown(); return; }
        ApplyLanguage(Loc.Preferred());
        bool dark = true;
        try { if (File.Exists(ThemeFile)) dark = File.ReadAllText(ThemeFile).Trim() == "dark"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        SetTheme(dark);
        MiddleButtonScroll.Register();
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.Launch(LaunchRequest.Parse(e.Args), false);
        // A fallback second window (the first one didn't answer in time) must not fight over the pipe.
        if (first) SingleInstance.Listen(args => Dispatcher.BeginInvoke(() => window.Launch(LaunchRequest.Parse(args), true)), listening.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        listening.Cancel();
        instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>The Task Scheduler entry point: the scheduled profile through the CLI code, with no window and no output.</summary>
    private static int RunScheduled(string profile)
    {
        Loc.Load(Loc.Preferred());
        var data = UserData.Load();
        if (!data.Schedule.Enabled || data.Profiles.All(p => !string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase))) return Cli.ExitCode.Usage;
        var io = new Cli.CliIO(TextWriter.Null, TextWriter.Null, TextReader.Null, false);
        return Cli.Cli.Run(["clean", "--profile", profile, "--yes", "--quiet", "--scheduled"], io, data, CancellationToken.None);
    }

    public void ApplyLanguage(string code)
    {
        Loc.Load(code);
        var strings = new ResourceDictionary();
        foreach (var (key, text) in Loc.Strings) strings[key] = text;
        var merged = Resources.MergedDictionaries;
        while (merged.Count > 1) merged.RemoveAt(merged.Count - 1);
        merged.Add(strings);
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
