using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;

namespace Aeris.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly UserData data = UserData.Load();
    private CancellationTokenSource? operation;
    private readonly CancellationTokenSource lifetime = new();
    private List<CacheGroup> caches = [];
    private List<LargeFile> files = [];
    private DateTime cutoff;
    private UpdateInfo? update;
    private bool checkingUpdate;
    private bool downloading;
    private bool cleaning;
    private bool startupChecked;
    private ScanResult? currentScan;
    private record FolderEntry(int Node, string Name, string Size);

    public MainWindow()
    {
        InitializeComponent();
        FolderBox.Text = data.Folder;
        Retention.Value = data.RetentionDays;
        Map.Navigated += (result, node) =>
        {
            currentScan = result;
            FolderList.ItemsSource = result.Nodes.Children(node).Where(result.Nodes.IsDirectory)
                .OrderByDescending(n => result.Nodes.Size(n)).Select(n => new FolderEntry(n, result.Nodes.Name(n), Storage.Format(result.Nodes.Size(n)))).ToList();
            ScanSummary.Text = result.Nodes.FullPath(node) + " · " + Storage.Format(result.Nodes.Size(node));
        };
        VersionText.Text = $"Aeris {Updater.Current.ToString(3)} · Linux";
        LanguagePicker.ItemsSource = Loc.Languages;
        LanguagePicker.SelectedItem = Loc.Current;
        ResetCaches();
        RefreshHistoryClick(null, new RoutedEventArgs());
        InitializeStatusBar();
        Opened += async (_, _) =>
        {
            if (startupChecked) return;
            startupChecked = true;
            await CheckUpdatesAsync();
        };
        Closing += (_, e) =>
        {
            if (cleaning) { e.Cancel = true; Status.Text = Loc.Get("Status.WaitForCleanup"); return; }
            operation?.Cancel(); lifetime.Cancel();
            data.Folder = FolderBox.Text ?? data.Folder;
            data.RetentionDays = (int)(Retention.Value ?? 7);
            try { data.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    private async Task Run(Func<CancellationToken, Task> action, bool writes = false)
    {
        if (operation != null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = cancel; cleaning = writes;
        RefreshStatusBar();
        foreach (var button in new[] { ScanButton, CacheScanButton, ReviewButton, DuplicateButton, RestoreButton, BrowseButton }) button.IsEnabled = false;
        Retention.IsEnabled = FolderBox.IsEnabled = LanguagePicker.IsEnabled = CacheList.IsEnabled = false;
        CancelButton.IsVisible = !writes;
        try { await action(cancel.Token); }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Status.ScanCancelled"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Status.Text = Loc.Format("Status.ScanError", e.Message); }
        finally
        {
            operation = null; cleaning = false;
            RefreshStatusBar();
            foreach (var button in new[] { ScanButton, CacheScanButton, DuplicateButton, RestoreButton, BrowseButton }) button.IsEnabled = true;
            ReviewButton.IsEnabled = caches.Any(c => c.CanClean);
            Retention.IsEnabled = FolderBox.IsEnabled = LanguagePicker.IsEnabled = CacheList.IsEnabled = true;
            CancelButton.IsVisible = false;
        }
    }

    private string SelectedFolder()
    {
        string folder = Path.GetFullPath(FolderBox.Text ?? "");
        if (!Directory.Exists(folder) || !Storage.SafePath(folder)) throw new IOException(Loc.Get("Dup.ChooseFolder"));
        return folder;
    }

    private async void Browse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = Loc.Get("Files.FolderCaption"), AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) FolderBox.Text = path;
    }

    private async void Scan(object? sender, RoutedEventArgs e) => await Run(async token =>
    {
        string folder = SelectedFolder();
        Status.Text = Loc.Get("Status.AnalyzingFolder");
        var progress = new Progress<ScanProgress>(p => Status.Text = Loc.Format("Status.FilesChecked", p.Files, Storage.Format(p.Bytes), p.Folder));
        var result = await new StandardScanner().ScanAsync(folder, new(data.Exclusions), progress, token);
        files = result.FileNodes().Where(n => (result.Nodes.Flags(n) & NodeFlags.HardLinkCopy) == 0).OrderByDescending(n => result.Nodes.Size(n)).Take(500).Select(result.ToLargeFile).ToList();
        FileList.ItemsSource = files;
        Map.Show(result, result.Root);
        Status.Text = Loc.Format("Status.ScanDone", result.Nodes.Files(0), Storage.Format(result.Nodes.Size(0)), result.Skipped);
    });

    private async void ScanCaches(object? sender, RoutedEventArgs e) => await Run(async token =>
    {
        var errors = new List<RuleError>();
        cutoff = DateTime.UtcNow.AddDays(-(double)(Retention.Value ?? 7));
        Status.Text = Loc.Get("Status.FindingCaches");
        var choices = caches.ToDictionary(c => c.Rule.Id, c => c.Scanned ? c.Selected : c.SelectionPreference);
        bool hasChoices = choices.Values.Any(choice => choice.HasValue);
        var found = await Task.Run(() =>
        {
            var groups = Cleaners.Groups(errors);
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                group.Scan(cutoff, data.Exclusions, token);
                group.RestoreSelection(choices.GetValueOrDefault(group.Rule.Id) ?? (hasChoices ? false : (bool?)null));
            }
            return groups;
        }, token);
        caches = found;
        ApplyCacheFilter();
        Status.Text = Loc.Format("Status.CachesChecked", Storage.Format(caches.Sum(c => c.Eligible)), caches.Sum(c => c.Skipped));
        if (errors.Count > 0) Status.Text += " " + string.Join("; ", errors.Select(x => $"{x.Source}: {x.Message}"));
    });

    private bool CacheMatches(CacheGroup group) => group.MatchesFilter((CacheCategory.SelectedItem as ComboBoxItem)?.Tag as string, CacheSearch.Text);
    private void CacheFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyCacheFilter();
    private void CacheSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (CacheSearchClear != null) CacheSearchClear.IsVisible = !string.IsNullOrEmpty(CacheSearch.Text);
        ApplyCacheFilter();
    }
    private void CacheSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CacheGroup group } box) group.Chosen = box.IsChecked == true;
        RefreshCacheFilterSummary();
    }
    private void CacheRowClicked(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (operation != null || sender is not Grid row) return;
        var box = row.Children.OfType<CheckBox>().First();
        if (e.Source is Visual source && (source == box || box.IsVisualAncestorOf(source))) return;
        box.Focus();
        box.SetCurrentValue(CheckBox.IsCheckedProperty, box.IsChecked != true);
        e.Handled = true;
    }
    private void ApplyCacheFilter()
    {
        if (CacheSearch == null || CacheCategory == null || CacheList == null) return;
        CacheList.ItemsSource = caches.Where(CacheMatches).ToList();
        RefreshCacheFilterSummary();
    }
    private void RefreshCacheFilterSummary()
    {
        if (CacheFilterSummary == null || CacheSearch == null) return;
        int visible = caches.Count(CacheMatches);
        CacheFilterSummary.Text = Loc.Format("Caches.FilterSummary", visible, caches.Count, caches.Count(c => c.Selected && !CacheMatches(c)));
        if (visible == 0) CacheFilterSummary.Text = Loc.Get("Caches.NoMatches") + " " + CacheFilterSummary.Text;
        if (caches.Any(c => !c.Scanned)) CacheFilterSummary.Text += " " + Loc.Get("Caches.ChooseThenCheck");
    }

    private void ResetCaches()
    {
        var choices = caches.ToDictionary(c => c.Rule.Id, c => c.Scanned ? c.Selected : c.SelectionPreference);
        bool hasChoices = choices.Values.Any(choice => choice.HasValue);
        caches = Cleaners.Groups();
        foreach (var group in caches) group.RestoreSelection(choices.GetValueOrDefault(group.Rule.Id) ?? (hasChoices ? false : (bool?)null));
        ApplyCacheFilter();
    }

    private void RetentionChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (CacheList == null) return;
        ResetCaches();
        if (ReviewButton != null) ReviewButton.IsEnabled = false;
    }

    private async void ReviewCleanup(object? sender, RoutedEventArgs e) => await Run(async token =>
    {
        var plan = Cleanup.CachePlan(caches, cutoff, CleanupMode.Quarantine);
        if (plan.Count == 0) { Status.Text = Loc.Get("Caches.NothingSelected"); return; }
        var review = new CleanupReview(plan);
        if (!await review.ShowDialog<bool>(this) || plan.Count == 0) return;
        Status.Text = Loc.Get("Status.WaitForCleanup");
        var result = await Task.Run(() =>
        {
            using var journal = Journal.Begin(Cleanup.NewId(), HistoryAction.Cleanup, CleanupMode.Quarantine, plan.Details);
            var result = Cleanup.ExecuteCache(plan, data.Exclusions, journal, token, null, Cleaners.Groups());
            journal.Finish(result);
            return result;
        }, token);
        foreach (var cache in caches) { cache.Scanned = false; cache.Selected = false; cache.Refresh(); }
        RefreshCacheFilterSummary();
        Status.Text = result.Summary;
        if (result.Errors.Count > 0) Status.Text += " " + result.Errors[0];
        RefreshHistory();
    }, writes: true);

    private async void FindDuplicates(object? sender, RoutedEventArgs e) => await Run(async token =>
    {
        string folder = SelectedFolder();
        Status.Text = Loc.Get("Dup.Grouping");
        int skipped = 0;
        var progress = new Progress<string>(text => Status.Text = text);
        var result = await Task.Run(() => Analysis.FindDuplicates(folder, data.Exclusions, token,
            text => ((IProgress<string>)progress).Report(text), () => skipped++, new(1, data.SequentialHashing, HashCache.Load())), token);
        DuplicateList.ItemsSource = result;
        var groups = result.GroupBy(f => f.Group).ToList();
        Status.Text = Loc.Format("Dup.Result", groups.Count, Storage.Format(groups.Sum(g => g.First().File.Size * (g.Count() - 1))), skipped);
    });

    private void RefreshHistory() => HistoryList.ItemsSource = data.History.Concat(Journal.Missing(data.History)).OrderByDescending(h => h.Time).ToList();
    private void RefreshHistoryClick(object? sender, RoutedEventArgs e)
    {
        try { RefreshHistory(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }

    private async void Restore(object? sender, RoutedEventArgs e) => await Run(async token =>
    {
        if (HistoryList.SelectedItem is not HistoryEntry { CanRestore: true } entry) { Status.Text = Loc.Get("History.NothingToRestore"); return; }
        var (header, items) = Journal.Read(entry.Id);
        if (header == null) throw new IOException(Loc.Get("History.NothingToRestore"));
        if (!await Confirm(Loc.Format("History.RestoreConfirm", items.Count(i => i.Result == Outcome.Quarantined), entry.DisplaySize, entry.DisplayTime), Loc.Get("Desktop.Restore"))) return;
        var result = await Task.Run(() => Quarantine.Restore(entry.Id, items, token), token);
        Status.Text = result.Summary;
        RefreshHistory();
    }, writes: true);

    private async void ExportCsv(object? sender, RoutedEventArgs e)
    {
        if (files.Count == 0) { Status.Text = Loc.Get("Files.NoneFound"); return; }
        var target = await StorageProvider.SaveFilePickerAsync(new() { Title = Loc.Get("Files.SaveTitle"), SuggestedFileName = "aeris-files.csv", DefaultExtension = "csv" });
        if (target?.TryGetLocalPath() is not { } path) return;
        try { Storage.ExportCsv(files, path); Status.Text = Loc.Get("Files.Saved"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Format("Files.SaveFailed", ex.Message); }
    }

    private async Task<bool> Confirm(string text, string action)
    {
        var dialog = new Window { Title = "Aeris", Width = 500, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = Loc.Get("Desktop.Cancel"), IsCancel = true };
        var accept = new Button { Content = action };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, accept } } } };
        return await dialog.ShowDialog<bool>(this);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => operation?.Cancel();
    private void ToggleTheme(object? sender, RoutedEventArgs e) => Application.Current!.RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    private void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is not Language language || language == Loc.Current) return;
        ((App)Application.Current!).ApplyLanguage(language.Code);
        foreach (var cache in caches) cache.Refresh();
        ApplyCacheFilter();
        RefreshHistory();
        try { Loc.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Language.SaveFailed"); }
    }

    private void OpenFileLocation(object? sender, Avalonia.Input.TappedEventArgs e)
    { if (FileList.SelectedItem is LargeFile file) OpenLocation(file.Path); }
    private void FileKey(object? sender, Avalonia.Input.KeyEventArgs e)
    { if (e.Key == Avalonia.Input.Key.Enter && FileList.SelectedItem is LargeFile file) { OpenLocation(file.Path); e.Handled = true; } }
    private void MapUp(object? sender, RoutedEventArgs e) => Map.Up();
    private void OpenMapFolder(object? sender, Avalonia.Input.TappedEventArgs e) => EnterMapFolder();
    private void MapFolderKey(object? sender, Avalonia.Input.KeyEventArgs e)
    { if (e.Key == Avalonia.Input.Key.Enter) { EnterMapFolder(); e.Handled = true; } }
    private void EnterMapFolder() { if (currentScan != null && FolderList.SelectedItem is FolderEntry folder) Map.Show(currentScan, folder.Node); }
    private void OpenDuplicateLocation(object? sender, Avalonia.Input.TappedEventArgs e)
    { if (DuplicateList.SelectedItem is DuplicateFile file) OpenLocation(file.Path); }
    private void OpenLocation(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) { Status.Text = Loc.Get("Files.Moved"); return; }
        OpenUrl(new Uri(folder).AbsoluteUri);
    }
    private async void OpenUrl(string url)
    {
        try { if (!await Launcher.LaunchUriAsync(new Uri(url))) Status.Text = Loc.Get("Desktop.OpenFailed"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { Status.Text = ex.Message; }
    }

    private void ShowUpdate(object? sender, RoutedEventArgs e) => Tabs.SelectedItem = SettingsTab;
    private async void CheckUpdates(object? sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdate || downloading) return;
        checkingUpdate = true; CheckButton.IsEnabled = false;
        UpdateText.Text = Loc.Get("Update.Checking");
        try
        {
            using var client = Updater.CreateClient();
            update = await new Updater(client).CheckAsync(Updater.Current, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            UpdateText.Text = update == null ? Loc.Format("Update.UpToDate", Updater.Current.ToString(3)) : Loc.Format("Desktop.UpdateAvailable", update.Version.ToString(3));
            UpdateBanner.IsVisible = update != null;
            BannerText.Text = UpdateText.Text;
            ReleaseNotes.Text = update?.Notes;
            ReleaseNotes.IsVisible = !string.IsNullOrEmpty(update?.Notes);
            DownloadButton.IsVisible = update?.LinuxDesktop != null && update.Checksums != null;
        }
        catch (UpdateException ex) { UpdateText.Text = ex.Message; }
        catch (OperationCanceledException) { }
        finally { checkingUpdate = false; CheckButton.IsEnabled = true; }
    }

    private async void DownloadUpdate(object? sender, RoutedEventArgs e)
    {
        if (downloading || update?.LinuxDesktop is not { } asset || operation != null) return;
        if (!await Confirm(Loc.Format("Desktop.ConfirmDownload", update.Version.ToString(3), Storage.Format(asset.Size)), Loc.Get("Desktop.Download"))) return;
        downloading = true; CheckButton.IsEnabled = DownloadButton.IsEnabled = false; DownloadProgress.IsVisible = true;
        try
        {
            using var client = Updater.CreateClient();
            var progress = new Progress<(long Done, long Total)>(p => DownloadProgress.Value = 100.0 * p.Done / p.Total);
            string path = await new Updater(client).DownloadAsync(update, asset, Updater.DownloadFolder, progress, lifetime.Token);
            UpdateText.Text = Loc.Format("Desktop.DownloadReady", path);
            OpenLocation(path);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException) { UpdateText.Text = ex.Message; }
        catch (OperationCanceledException) { }
        finally { downloading = false; CheckButton.IsEnabled = DownloadButton.IsEnabled = true; DownloadProgress.IsVisible = false; }
    }
    private void OpenReleasePage(object? sender, RoutedEventArgs e) => OpenUrl($"https://github.com/{Updater.Repository}/releases/latest");
}
