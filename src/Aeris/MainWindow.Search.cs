using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            MainTabs.SelectedItem = HelpTab;
            HelpScroll.Focus();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.K || (e.Key == Key.F && MainTabs.SelectedItem == HomeTab)) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MainTabs.SelectedItem = HomeTab;
            HomeSearch.Focus();
            HomeSearch.SelectAll();
            e.Handled = true;
            return;
        }
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

    private record SearchResult(string Title, string Description, Action Open);

    internal static bool MatchesSearch(string text, string[] words) =>
        words.All(word => Loc.Culture.CompareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    private void HomeSearchChanged(object sender, TextChangedEventArgs e) => RefreshHomeSearch();

    private void RefreshHomeSearch()
    {
        if (HomeSearchPanel == null) return;
        string query = HomeSearch.Text.Trim();
        bool opening = HomeSearchPanel.Visibility != Visibility.Visible;
        HomeSearchPanel.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Length == 0)
        {
            HomeSearchPanel.BeginAnimation(MaxHeightProperty, null);
            HomeSearchPanel.BeginAnimation(OpacityProperty, null);
            HomeSearchResults.ItemsSource = null;
            return;
        }
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<SearchResult>();
        void Add(TabItem tab, string title, string description, FrameworkElement? target = null) =>
            entries.Add(new(Loc.Get(title), Loc.Get(description), () => NavigateSearch(tab, target)));
        Add(CacheTab, "Nav.Caches", "Home.CheckCachesHint", CacheSearch);
        Add(FilesTab, "Nav.Files", "Home.FindLargeHint", Threshold);
        Add(MapTab, "Nav.Map", "Home.MapHint");
        entries.Add(new(Loc.Get("Dup.ModeExact"), Loc.Get("Home.DuplicatesHint"), () => { NavigateSearch(DuplicatesTab); if (!busy) DuplicateMode.SelectedIndex = 0; }));
        entries.Add(new(Loc.Get("Dup.ModeSimilar"), Loc.Get("Dup.SimilarityHint"), () => { NavigateSearch(DuplicatesTab); if (!busy) DuplicateMode.SelectedIndex = 1; }));
        Add(JunkTab, "Junk.Title", "Junk.Footer");
        Add(HistoryTab, "Nav.History", "Help.Recovery");
        Add(SettingsTab, "Nav.Settings", "Help.SettingsWindows");
        Add(SettingsTab, "Settings.Exclusions", "Excl.Hint", ExclusionsList);
        Add(SettingsTab, "Settings.Mode", "Settings.ModeHint", ModeSetting);
        Add(SettingsTab, "Settings.QuarantineDays", "Settings.QuarantineDaysHint", QuarantineDaysSetting);
        entries.Add(new(Loc.Get("Settings.QuarantineLimit"), QuarantineUsage.Text, () => NavigateSearch(SettingsTab, QuarantineLimitSetting)));
        Add(SettingsTab, "Settings.Priority", "Settings.PriorityHint", PriorityList);
        Add(SettingsTab, "Settings.Protected", "Settings.ProtectedHint", ProtectedList);
        Add(SettingsTab, "Settings.Sequential", "Settings.SequentialHint", SequentialSetting);
        Add(SettingsTab, "Settings.Automation", "Schedule.Hint", ScheduleEnabled);
        Add(SettingsTab, "Shell.Enable", "Shell.Hint", ShellMenuEnabled);
        Add(SettingsTab, "Settings.Rules", "Settings.RulesHint", RulesInfo);
        Add(SettingsTab, "Settings.Updates", "Update.Privacy", UpdateCheckButton);
        Add(HomeTab, "Language.Label", "Language.Label", LanguagePicker);
        Add(HomeTab, "Theme.Light", "Theme.Dark", ThemeButton);
        entries.Add(new(Loc.Get("Dup.KeepRule"), Loc.Get("Settings.PriorityHint"), () =>
        {
            NavigateSearch(DuplicatesTab, KeepRule);
            if (!busy) DuplicateMode.SelectedIndex = 0;
        }));
        Add(HelpTab, "Nav.Help", "Help.Intro", HelpScroll);
        foreach (var card in ((StackPanel)HelpScroll.Content).Children.OfType<Border>())
        {
            var text = ((StackPanel)card.Child).Children.OfType<TextBlock>().ToArray();
            entries.Add(new(Loc.Get("Nav.Help") + " · " + text[0].Text, string.Join(" ", text.Skip(1).Select(block => block.Text)),
                () => NavigateSearch(HelpTab, card)));
        }
        var results = entries.Where(item => MatchesSearch(item.Title + " " + item.Description, words))
            .OrderByDescending(item => MatchesSearch(item.Title, words)).ToList();
        foreach (var group in caches.Where(group => group.MatchesFilter(null, query)))
            results.Add(new(group.Name, group.Description, () =>
            {
                NavigateSearch(CacheTab, CacheList);
                CacheCategory.SelectedIndex = 0;
                CacheSearch.Text = group.Name;
                CacheList.SelectedItem = group;
                CacheList.ScrollIntoView(group);
            }));
        HomeSearchResults.ItemsSource = results;
        HomeSearchSummary.Text = results.Count == 0 ? Loc.Get("Search.None") : Loc.Format("Search.Count", results.Count);
        if (opening && SystemParameters.ClientAreaAnimation)
        {
            HomeSearchPanel.Measure(new Size(((FrameworkElement)HomeSearchPanel.Parent).ActualWidth, double.PositiveInfinity));
            double height = Math.Max(0, HomeSearchPanel.DesiredSize.Height - HomeSearchPanel.Margin.Top - HomeSearchPanel.Margin.Bottom);
            var duration = TimeSpan.FromMilliseconds(180);
            HomeSearchPanel.BeginAnimation(MaxHeightProperty, new DoubleAnimation(0, height, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            HomeSearchPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
        }
    }

    private void NavigateSearch(TabItem tab, FrameworkElement? target = null)
    {
        MainTabs.SelectedItem = tab;
        if (target != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            target.BringIntoView();
            target.Focus();
        }));
    }

    private void OpenHomeSearchResult(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SearchResult result }) result.Open();
    }

    private void ClearHomeSearch(object sender, RoutedEventArgs e) { HomeSearch.Clear(); HomeSearch.Focus(); }

    private void HomeSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HomeSearch.Clear(); e.Handled = true; }
        else if (e.Key == Key.Enter && HomeSearchResults.Items.Count > 0)
        {
            ((SearchResult)HomeSearchResults.Items[0]).Open();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && HomeSearchResults.ItemContainerGenerator.ContainerFromIndex(0) is UIElement first)
        {
            first.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            e.Handled = true;
        }
    }
}
