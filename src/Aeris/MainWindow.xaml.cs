using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Aeris;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    private string folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private CancellationTokenSource? cancellation;
    private List<CacheGroup> caches = [];
    private bool busy;
    private bool cleaning;
    private bool scanningCaches;
    private DateTime cacheCutoff;
    private bool choosingCustom;
    private List<RuleError> ruleErrors = [];
    private Func<string> filesHint = () => Loc.Get("Files.Hint");
    private record StatusProgress(string Summary, string Directory = "", string File = "");

    public MainWindow()
    {
        InitializeComponent();
        LanguagePicker.ItemsSource = Loc.Languages;
        SourceInitialized += (_, _) => UpdateTheme();
        Deactivated += (_, _) => DrivePopup.IsOpen = false;
        RestoreSettings();
        InitializeMap();
        ResetCaches();
        LocalizeView();
        InitializeStatusBar();
        StartMaintenance();
        ContentRendered += CheckStartupUpdate;
        Closed += (_, _) => { windowClosed = true; updating?.Cancel(); };
        Closing += (_, e) =>
        {
            if (cleaning) { e.Cancel = true; Status.Text = Loc.Get("Status.WaitForCleanup"); }
            else { cancellation?.Cancel(); SaveSettings(); }
        };
    }

    private void UpdateTheme()
    {
        bool isDark = ((App)Application.Current).DarkTheme;
        string label = Loc.Get(isDark ? "Theme.Light" : "Theme.Dark");
        ThemeButton.ToolTip = label;
        AutomationProperties.SetName(ThemeButton, label);
        ThemeGlyph.Data = Geometry.Parse(isDark
            ? "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22 M5 5 L6.5 6.5 M17.5 17.5 L19 19 M5 19 L6.5 17.5 M17.5 6.5 L19 5"
            : "M20 13 A8 8 0 1 1 11 4 A6 6 0 0 0 20 13 Z");
        int dark = isDark ? 1 : 0;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private void SwitchTheme(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.SetTheme(!app.DarkTheme, true);
        UpdateTheme();
        Treemap.Restyle();
        ShowMap();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(App.ThemeFile)!);
            File.WriteAllText(App.ThemeFile, app.DarkTheme ? "dark" : "light");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status.Text = Loc.Get("Theme.SaveFailed"); }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || LanguagePicker.SelectedItem is not Language language || language == Loc.Current) return;
        ((App)Application.Current).ApplyLanguage(language.Code);
        LocalizeView();
        try { Loc.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Language.SaveFailed"); }
    }

    private void LocalizeView()
    {
        Language = XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        restoring = true;
        try
        {
            LanguagePicker.SelectedItem = Loc.Current;
            LabelOptions(Retention);
            LabelOptions(Threshold);
            foreach (var combo in new[] { Retention, Threshold, FileType, DuplicateMode }) RefreshSelectionBox(combo);
        }
        finally { restoring = false; }
        UpdateTheme();
        RefreshDisk();
        foreach (var group in caches) group.Refresh();
        ApplyCacheFilter();
        FilesGrid.Items.Refresh();
        ShowMap();
        DuplicatesGrid.Items.Refresh();
        SimilarGrid.Items.Refresh();
        RefreshSimilarityLabel();
        DuplicateFooter.Text = Loc.Get(SimilarMode ? "Dup.SimilarFooter" : "Dup.Footer");
        RefreshHistory();
        RestoreCleanupSettings();
        RestoreIntegrationSettings();
        RefreshUpdateCard();
        RefreshRulesInfo();
        RefreshQuarantine();
        JunkGrid.Items.Refresh();
        FilesHint.Text = filesHint();
        CacheScanButton.Content = Loc.Get(busy && scanningCaches && !cleaning ? "Caches.Checking" : "Caches.Check");
        UpdateSelection();
        if (!busy) Status.Text = Loc.Get("Status.Ready");
    }

    private static void RefreshSelectionBox(ComboBox combo)
    {
        var item = combo.SelectedItem;
        combo.SelectedItem = null;
        combo.SelectedItem = item;
    }

    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || !IsLoaded) return;
        if (ReferenceEquals(MainTabs.SelectedItem, HomeTab)) { RefreshDisk(); RefreshHome(); }
        if (ReferenceEquals(MainTabs.SelectedItem, HistoryTab) || ReferenceEquals(MainTabs.SelectedItem, HomeTab)) ImportJournals();
        if (ReferenceEquals(MainTabs.SelectedItem, SettingsTab)) RefreshScheduleStatus();
        if (MainTabs.Template.FindName("PART_SelectedContentHost", MainTabs) is not ContentPresenter content) return;
        content.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation || InputManager.Current.MostRecentInputDevice is KeyboardDevice) return;
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void RefreshDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            double used = 100 * (1 - (double)drive.TotalFreeSpace / drive.TotalSize);
            DriveLabel.Text = Loc.Format("Drive.CaptionNamed", drive.Name);
            DiskSpace.Text = Storage.Format(drive.AvailableFreeSpace);
            DiskDetail.Text = Loc.Format("Drive.Detail", Storage.Format(drive.TotalSize), used);
            DiskBar.Value = used;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DriveLabel.Text = Loc.Get("Drive.Caption");
            DiskSpace.Text = Loc.Get("Drive.Unavailable");
            DiskDetail.Text = Loc.Get("Drive.NoData");
            DiskBar.Value = 0;
        }
    }

    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.Get("Folder.Choose"), InitialDirectory = folder };
        if (picker.ShowDialog(this) == true) SetFolder(picker.FolderName);
    }

    private void SetFilesHint(Func<string> hint)
    {
        filesHint = hint;
        FilesHint.Text = hint();
    }

    private void ResetFileResults()
    {
        if (lastScan != null && SameFolder(lastScan.RootPath, folder)) { _ = RefreshLargeFiles(); return; }
        FilesGrid.ItemsSource = null;
        ExportButton.IsEnabled = false;
        TrashFilesButton.IsEnabled = false;
        SetFilesHint(() => Loc.Get("Files.ParamsChanged"));
    }

    private void RetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || CacheList == null || !CustomChoice(Retention, e, "Retention.Prompt", 2, 36500)) return;
        ResetCaches();
        SaveSettings();
    }

    private void ThresholdChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || FilesGrid == null || !CustomChoice(Threshold, e, "Threshold.Prompt", 1, 1000000000)) return;
        ResetFileResults();
        SaveSettings();
    }

    private string OptionLabel(ComboBox combo, int value) =>
        combo == Retention ? Loc.Plural("Option.Days", value) : Storage.Format(value * 1024L * 1024);

    private void LabelOptions(ComboBox combo)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            item.Content = item.Tag is "custom" ? Loc.Get("Option.Custom") : OptionLabel(combo, int.Parse((string)item.Tag));
    }

    private bool CustomChoice(ComboBox combo, SelectionChangedEventArgs e, string prompt, int minimum, int maximum)
    {
        if (choosingCustom) return false;
        if (combo.SelectedItem is not ComboBoxItem { Tag: "custom" }) return true;
        var previous = e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault() ?? (ComboBoxItem)combo.Items[0];
        choosingCustom = true;
        try
        {
            combo.IsDropDownOpen = false;
            var dialog = new NumberDialog(Loc.Get(prompt), int.Parse((string)previous.Tag), minimum, maximum) { Owner = this };
            if (dialog.ShowDialog() != true) { combo.SelectedItem = previous; return false; }
            combo.SelectedItem = OptionItem(combo, dialog.Value);
            return true;
        }
        finally { choosingCustom = false; }
    }

    private ComboBoxItem OptionItem(ComboBox combo, int value)
    {
        string tag = value.ToString();
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (item != null) return item;
        if (combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Uid == "custom-value") is { } old) combo.Items.Remove(old);
        item = new ComboBoxItem { Content = OptionLabel(combo, value), Tag = tag, Uid = "custom-value" };
        combo.Items.Insert(combo.Items.Count - 1, item);
        return item;
    }

    private void SelectAllCaches(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var visible = VisibleCaches().ToArray();
        bool select = visible.Any(c => !c.IsAdvanced && !c.Selected);
        foreach (var group in visible) group.Chosen = select && (!group.IsAdvanced || group.Selected);
        CacheList.Items.Refresh();
        UpdateSelection();
    }

    private void CacheRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (busy || sender is not Grid row || row.DataContext is not CacheGroup group) return;
        var box = row.Children.OfType<CheckBox>().First();
        if (e.OriginalSource is Visual source && (source == box || box.IsAncestorOf(source))) return;
        CacheList.SelectedItem = group;
        box.Focus();
        box.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, box.IsChecked != true);
        e.Handled = true;
    }

    private void CacheSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CacheGroup changed } checkbox && !restoring)
            changed.Chosen = checkbox.IsChecked == true;
        if (sender is CheckBox { IsChecked: true, DataContext: CacheGroup group } box)
        {
            CacheList.SelectedItem = group;
            if (group.IsAdvanced && !restoring && (box.IsKeyboardFocusWithin || box.IsMouseOver)
                && MessageBox.Show(this, Loc.Format("Risk.Advanced.Confirm", group.Name, group.RiskHint), group.RiskText, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                box.IsChecked = false;
        }
        if (CleanButton != null) UpdateSelection();
    }

    private void ResetCaches()
    {
        var errors = new List<RuleError>();
        var choices = caches.ToDictionary(c => c.Rule.Id, c => c.Scanned ? c.Selected : c.SelectionPreference);
        bool hasChoices = choices.Values.Any(choice => choice.HasValue);
        caches = Cleaners.Groups(errors);
        foreach (var group in caches) group.RestoreSelection(choices.GetValueOrDefault(group.Rule.Id) ?? (hasChoices ? false : (bool?)null));
        ShowCaches(errors);
    }

    private void ShowCaches(IReadOnlyCollection<RuleError> errors)
    {
        ruleErrors = errors.ToList();
        var view = new ListCollectionView(caches);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CacheGroup.GroupName)));
        view.Filter = item => CacheMatches((CacheGroup)item);
        CacheList.ItemsSource = view;
        CacheList.SelectedIndex = 0;
        RuleNotice.Visibility = ruleErrors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleErrors.Text = string.Join("\n", ruleErrors.Select(e => $"{e.Source}: {e.Message}"));
        RefreshRulesInfo();
        UpdateSelection();
    }

    private bool CacheMatches(CacheGroup group) => group.MatchesFilter((CacheCategory.SelectedItem as ComboBoxItem)?.Tag as string, CacheSearch.Text);
    private IEnumerable<CacheGroup> VisibleCaches() => caches.Where(CacheMatches);

    private void CacheFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyCacheFilter();
    private void CacheSearchChanged(object sender, TextChangedEventArgs e) => ApplyCacheFilter();

    private void ApplyCacheFilter()
    {
        if (CacheList?.ItemsSource is not ListCollectionView view || CacheSearch == null) return;
        var selected = CacheList.SelectedItem;
        view.Refresh();
        CacheList.SelectedItem = selected != null && view.Contains(selected) ? selected : view.Cast<object>().FirstOrDefault();
        UpdateSelection();
    }

    private void CacheDetailChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootExpander != null) RootExpander.IsExpanded = false;
    }

    private void UpdateSelection()
    {
        var selected = caches.Where(c => c.Selected).ToArray();
        bool awaitingScan = selected.Any(c => !c.Scanned);
        CleanButton.IsEnabled = !busy && !awaitingScan && selected.Any(c => c.CanClean);
        var visible = VisibleCaches().ToArray();
        SelectAllButton.IsEnabled = !busy && visible.Length > 0;
        SelectAllButton.Content = Loc.Get(visible.Any(c => !c.IsAdvanced && !c.Selected) ? "Caches.SelectVisible" : "Caches.ClearVisible");
        CacheFilterSummary.Text = Loc.Format("Caches.FilterSummary", visible.Length, caches.Count, selected.Count(c => !CacheMatches(c)));
        if (visible.Length == 0) CacheFilterSummary.Text = Loc.Get("Caches.NoMatches") + " " + CacheFilterSummary.Text;
        SelectionSummary.Text = selected.Length == 0
            ? Loc.Get("Caches.NothingSelected")
            : awaitingScan ? Loc.Format("Caches.SelectedBeforeScan", selected.Length)
            : Loc.Format("Caches.SelectedSummary", Storage.Format(selected.Sum(c => c.Eligible)), selected.Length);
        CleanModeHint.Text = caches.Any(c => !c.Scanned) ? Loc.Get("Caches.ChooseThenCheck") : data.CleanupMode == CleanupMode.Quarantine ? Loc.Plural("Caches.ReviewQuarantine", data.QuarantineDays) : Loc.Get("Caches.ReviewPermanent");
        RefreshHome();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        RefreshStatusBar();
        DriveButton.IsEnabled = !value;
        if (value) DrivePopup.IsOpen = false;
        ChooseButton.IsEnabled = ScanButton.IsEnabled = CacheScanButton.IsEnabled = Threshold.IsEnabled = Retention.IsEnabled = !value;
        SizeMode.IsEnabled = !value;
        MapUpButton.IsEnabled = !value && lastScan != null && mapNode != 0;
        FileType.IsEnabled = MapScanButton.IsEnabled = MapChooseButton.IsEnabled = DuplicateScanButton.IsEnabled = DuplicateChooseButton.IsEnabled = AddExclusionButton.IsEnabled = RemoveExclusionButton.IsEnabled = !value;
        JunkScanButton.IsEnabled = JunkChooseButton.IsEnabled = IgnoreJunkFiles.IsEnabled = KeepRule.IsEnabled = !value;
        AddPriorityButton.IsEnabled = RemovePriorityButton.IsEnabled = AddProtectedButton.IsEnabled = RemoveProtectedButton.IsEnabled = !value;
        EmptyQuarantineButton.IsEnabled = ClearHashCacheButton.IsEnabled = ModeSetting.IsEnabled = ScheduleSaveButton.IsEnabled = ShellMenuEnabled.IsEnabled = !value;
        RestoreButton.IsEnabled = !value && HistoryGrid.SelectedItem is HistoryEntry { CanRestore: true };
        HomeCacheAction.IsEnabled = LanguagePicker.IsEnabled = !value;
        UpdateSelection();
        CacheList.IsEnabled = !value;
        ExportButton.IsEnabled = !value && FilesGrid.Items.Count > 0;
        TrashFilesButton.IsEnabled = !value && FilesGrid.Items.Count > 0;
        TrashDuplicatesButton.IsEnabled = SelectExtraButton.IsEnabled = ClearDuplicateSelectionButton.IsEnabled = !value && ActiveDuplicateGrid.Items.Count > 0;
        DuplicateMode.IsEnabled = SimilaritySlider.IsEnabled = !value;
        TrashJunkButton.IsEnabled = SelectAllJunkButton.IsEnabled = !value && JunkGrid.Items.Count > 0;
        CancelButton.Visibility = value && !cleaning ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = value;
        if (value) { ScanLocation.Text = ""; ScanFile.Text = ""; }
        CacheScanButton.Content = Loc.Get(value && scanningCaches && !cleaning ? "Caches.Checking" : "Caches.Check");
    }

    private void CancelScan(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Status.Text = Loc.Get("Status.Stopping"); }

    private async void StartScan(object sender, RoutedEventArgs e) => await Scan(false);
    private async void RefreshCaches(object sender, RoutedEventArgs e) => await Scan(true);

    private async Task Scan(bool cachesOnly)
    {
        if (busy) return;
        if (!cachesOnly) { await ScanFolder(); return; }
        SaveSettings();
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        DateTime cutoff = DateTime.UtcNow.AddDays(-int.Parse((string)((ComboBoxItem)Retention.SelectedItem).Tag));
        var exclusions = data.Exclusions.ToArray();
        scanningCaches = true;
        SetBusy(true);
        Status.Text = Loc.Get("Status.FindingCaches");
        var progress = new Progress<StatusProgress>(s =>
        {
            if (!busy || token != cancellation?.Token || token.IsCancellationRequested) return;
            Status.Text = s.Summary;
            ScanLocation.Text = s.Directory;
            ScanFile.Text = s.File;
        });
        IProgress<StatusProgress> report = progress;
        var choices = caches.ToDictionary(c => c.Rule.Id, c => c.Scanned ? c.Selected : c.SelectionPreference);
        bool hasChoices = choices.Values.Any(choice => choice.HasValue);
        try
        {
            var result = await Task.Run(() =>
            {
                int skipped = 0;
                var watch = Stopwatch.StartNew();
                var ruleProblems = new List<RuleError>();
                var foundCaches = Cleaners.Groups(ruleProblems);
                long cacheFiles = 0, cacheBytes = 0;
                int category = 0;
                string CacheProgress(CacheGroup group) => Loc.Format("Status.CacheProgress", category, foundCaches.Count, group.Name, cacheFiles, Storage.Format(cacheBytes));
                foreach (var group in foundCaches)
                {
                    category++;
                    report.Report(new(CacheProgress(group)));
                    watch.Restart();
                    group.Scan(cutoff, exclusions, token, (root, file) =>
                    {
                        if (file == null) { report.Report(new(CacheProgress(group), root)); return; }
                        cacheFiles++;
                        cacheBytes += file.Length;
                        if (watch.ElapsedMilliseconds < 250) return;
                        report.Report(new(CacheProgress(group), file.DirectoryName ?? root, file.Name));
                        watch.Restart();
                    });
                    skipped += group.Skipped;
                    group.RestoreSelection(choices.GetValueOrDefault(group.Rule.Id) ?? (hasChoices ? false : (bool?)null));
                }
                return (foundCaches, ruleProblems, skipped);
            }, token);
            token.ThrowIfCancellationRequested();
            caches = result.foundCaches;
            cacheCutoff = cutoff;
            ShowCaches(result.ruleProblems);
            Status.Text = Loc.Format("Status.CachesChecked", Storage.Format(caches.Sum(c => c.Eligible)), result.skipped);
        }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Status.ScanCancelled"); }
        catch (Exception ex) { Status.Text = Loc.Format("Status.ScanError", ex.Message); }
        finally { SetBusy(false); scanningCaches = false; RefreshDisk(); }
    }

    private void RevealFile(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not LargeFile file) { Status.Text = Loc.Get("Files.SelectFirst"); return; }
        if (!File.Exists(file.Path)) { Status.Text = Loc.Get("Files.Moved"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = Loc.Format("Files.ExplorerFailed", ex.Message); }
    }

    private void SaveCsv(object sender, RoutedEventArgs e)
    {
        if (busy || FilesGrid.ItemsSource is not IEnumerable<LargeFile> files || !files.Any()) return;
        var dialog = new SaveFileDialog { Title = Loc.Get("Files.SaveTitle"), Filter = "CSV (*.csv)|*.csv", FileName = "Aeris.csv", DefaultExt = ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { Storage.ExportCsv(files, dialog.FileName); Status.Text = Loc.Get("Files.Saved"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Format("Files.SaveFailed", ex.Message); }
    }

    private async void CleanSelected(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var selected = caches.Where(c => c.Selected && c.Eligible > 0).ToArray();
        if (selected.Length == 0) { Status.Text = Loc.Get("Clean.SelectCategories"); return; }
        var plan = Cleanup.CachePlan(selected, cacheCutoff, data.CleanupMode);
        if (!Preview(plan)) return;
        if (plan.Mode != data.CleanupMode) { data.CleanupMode = plan.Mode; RestoreCleanupSettings(); SaveSettings(); }
        string cutoffText = cacheCutoff.ToLocalTime().ToString("g", Loc.Culture);
        string details = string.Join("\n", plan.Categories.Where(c => c.IncludedCount > 0).Select(c => c.Name)) + "\n" + Loc.Format("History.Before", cutoffText);
        HistoryEntry history;
        Journal journal;
        try
        {
            string id = Cleanup.NewId();
            journal = Journal.Begin(id, HistoryAction.Cleanup, plan.Mode, details);
            history = BeginHistory(HistoryAction.Cleanup, details, id, plan.Mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Clean.HistoryFailed"); return; }
        cleaning = true;
        SetBusy(true);
        int total = plan.Count;
        Status.Text = Loc.Get("Clean.Running");
        var progress = new Progress<int>(n => { if (cleaning) Status.Text = Loc.Format("Clean.Progress", n, total); });
        try
        {
            var exclusions = data.Exclusions.ToArray();
            var result = await Task.Run(() =>
            {
                using (journal)
                {
                    var outcome = Cleanup.ExecuteCache(plan, exclusions, journal, CancellationToken.None, progress, Cleaners.Groups());
                    journal.Finish(outcome);
                    return outcome;
                }
            });
            Status.Text = result.Summary;
            FinishHistory(history, result.Done, result.Bytes, result.Skipped, result.Result);
            if (plan.Mode == CleanupMode.Quarantine) _ = Task.Run(() => Quarantine.Purge(data.QuarantineDays, data.QuarantineLimitGb * 1024L * 1024 * 1024));
            if (result.Errors.Count > 0 && result.Failed > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(8)), Loc.Get("Recycle.Skipped"));
        }
        catch (Exception ex)
        {
            journal.Dispose();
            Status.Text = Loc.Format("Clean.Stopped", ex.Message);
            FinishHistory(history, 0, 0, 0, HistoryResult.Interrupted);
        }
        finally { cleaning = false; ResetCaches(); SetBusy(false); RefreshDisk(); RefreshQuarantine(); }
    }

    private bool Preview(CleanupPlan plan, IEnumerable<string>? warnings = null)
    {
        var window = new PreviewWindow(plan, warnings ?? [], data.LargeOperationFiles, data.LargeOperationGb * 1024L * 1024 * 1024, data.QuarantineDays) { Owner = this };
        return window.ShowDialog() == true;
    }
}
