using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Aeris;

public partial class MainWindow
{
    private readonly UserData data = UserData.Load();
    private bool restoring;
    private List<DuplicateFile> duplicates = [];
    private int driveMenuVersion;
    public record DriveChoice(string Root, string Label, string Detail, double Used, bool Ready, bool Selected);

    internal static DriveChoice DescribeDrive(DriveInfo drive, string currentRoot)
    {
        try
        {
            if (!drive.IsReady) return new(drive.Name, drive.Name, Loc.Get("Drive.Unavailable"), 0, false, false);
            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Loc.Format("Drive.Named", drive.Name) : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            double used = drive.TotalSize == 0 ? 0 : 100 * (1 - (double)drive.TotalFreeSpace / drive.TotalSize);
            return new(drive.Name, label, Loc.Format("Drive.FreeOf", Storage.Format(drive.AvailableFreeSpace), Storage.Format(drive.TotalSize)), used, true, string.Equals(drive.Name, currentRoot, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(drive.Name, drive.Name, Loc.Get("Drive.Unavailable"), 0, false, false); }
    }

    private async void OpenDriveMenu(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (DrivePopup.IsOpen) { DrivePopup.IsOpen = false; return; }
        int version = ++driveMenuVersion;
        string currentRoot = Path.GetPathRoot(folder) ?? "";
        DriveChoices.Items.Clear();
        DriveLoading.Text = Loc.Get("Drive.Searching");
        DriveLoading.Visibility = Visibility.Visible;
        DrivePopup.IsOpen = true;
        var progress = new Progress<DriveChoice>(choice =>
        {
            if (version != driveMenuVersion || !DrivePopup.IsOpen) return;
            DriveChoices.Items.Add(choice);
            DriveLoading.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (version == driveMenuVersion && DrivePopup.IsOpen && Keyboard.FocusedElement == DriveButton)
                    DrivePopup.Child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }));
        });
        try
        {
            await Task.Run(() =>
            {
                foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
                    ((IProgress<DriveChoice>)progress).Report(DescribeDrive(drive, currentRoot));
            });
            if (version != driveMenuVersion || !DrivePopup.IsOpen) return;
            DriveLoading.Text = Loc.Get("Drive.NoneFound");
            DriveLoading.Visibility = DriveChoices.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (version == driveMenuVersion) DriveLoading.Text = Loc.Get("Drive.ListFailed"); }
    }

    private void DriveMenuClosed(object sender, EventArgs e)
    {
        driveMenuVersion++;
        DriveButton.Focus();
    }

    private void DismissDrivePopup(object sender, MouseButtonEventArgs e)
    {
        if (DrivePopup.IsOpen && !DriveButton.IsMouseOver && !DrivePopup.Child.IsMouseOver) DrivePopup.IsOpen = false;
    }

    private void DrivePopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DrivePopup.IsOpen = false; e.Handled = true; }
    }

    private void SelectDrive(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: DriveChoice { Ready: true } drive }) return;
        if (!Storage.SafePath(drive.Root)) { Status.Text = Loc.Get("Drive.Gone"); DrivePopup.IsOpen = false; return; }
        DrivePopup.IsOpen = false;
        SetFolder(drive.Root);
        Status.Text = Loc.Format("Drive.Selected", drive.Root);
    }

    private void SetFolder(string path)
    {
        folder = path;
        UpdateFolderLabels(); ResetFileResults();
        ShowMap(); duplicates = []; DuplicatesGrid.ItemsSource = null; JunkGrid.ItemsSource = null;
        similar = []; SimilarGrid.ItemsSource = null; SimilarPreviews.ItemsSource = null; SimilarStrip.Visibility = Visibility.Collapsed;
        SetBusy(false); SaveSettings(); RefreshDisk();
    }

    private void RestoreSettings()
    {
        restoring = true;
        try
        {
            folder = data.Folder;
            LabelOptions(Retention);
            LabelOptions(Threshold);
            Retention.SelectedItem = OptionItem(Retention, data.RetentionDays);
            Threshold.SelectedItem = OptionItem(Threshold, data.MinimumMb);
            FileType.SelectedIndex = data.FileType;
            ExclusionsList.ItemsSource = data.Exclusions;
            PriorityList.ItemsSource = data.PriorityFolders;
            ProtectedList.ItemsSource = data.ProtectedFolders;
            SequentialSetting.IsChecked = data.SequentialHashing;
            KeepRule.SelectedItem = KeepRule.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, data.KeepRule)) ?? KeepRule.Items[0];
            RestoreCleanupSettings();
            RefreshHistory();
        }
        finally { restoring = false; }
        UpdateFolderLabels();
    }

    private void SaveSettings()
    {
        if (restoring || !IsInitialized) return;
        data.Folder = folder;
        if (Retention.SelectedItem is ComboBoxItem r && int.TryParse(r.Tag?.ToString(), out int days)) data.RetentionDays = days;
        if (Threshold.SelectedItem is ComboBoxItem t && int.TryParse(t.Tag?.ToString(), out int mb)) data.MinimumMb = mb;
        data.FileType = FileType.SelectedIndex;
        try { data.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Status.SettingsSaveFailed"); }
    }

    private void UpdateFolderLabels()
    {
        FolderLabel.Text = MapFolder.Text = DuplicateFolder.Text = JunkFolder.Text = folder;
    }

    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || FilesGrid == null) return;
        ResetFileResults();
        SaveSettings();
    }

    private void RefreshHistory()
    {
        HistoryGrid.ItemsSource = data.History.OrderByDescending(h => h.Time).ToList();
        RefreshHome();
    }

    private void RefreshHome()
    {
        if (HomeCacheSize == null) return;
        bool scanned = caches.Any(c => c.Scanned);
        HomeCacheSize.Text = scanned ? Storage.Format(caches.Sum(c => c.Eligible)) : Loc.Get("Home.NotChecked");
        HomeCacheHint.Text = Loc.Get(scanned ? "Home.CanClean" : "Home.SizeAfterCheck");
        var last = data.History.OrderByDescending(h => h.Time).FirstOrDefault();
        HomeLastAction.Text = last == null ? Loc.Get("Home.NoOperations") : $"{last.DisplayTime} · {last.DisplayAction} · {last.DisplaySize} · {last.DisplayResult}";
    }

    private async void HomeAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        switch (button.Tag)
        {
            case "cache": MainTabs.SelectedItem = CacheTab; await Scan(true); break;
            case "files": MainTabs.SelectedItem = FilesTab; break;
            case "map": MainTabs.SelectedItem = MapTab; break;
            case "duplicates": MainTabs.SelectedItem = DuplicatesTab; break;
            case "history": MainTabs.SelectedItem = HistoryTab; break;
        }
    }

    private HistoryEntry BeginHistory(string action, string details, string id = "", CleanupMode? mode = null)
    {
        var entry = new HistoryEntry { Action = action, Details = details, Id = id, Mode = mode?.ToString() ?? "" };
        data.History.Add(entry);
        if (data.History.Count > UserData.MaxHistory) data.History.RemoveRange(0, data.History.Count - UserData.MaxHistory);
        try { data.Save(); }
        catch { data.History.Remove(entry); throw; }
        RefreshHistory();
        return entry;
    }

    private void FinishHistory(HistoryEntry entry, int files, long bytes, int skipped, string result)
    {
        entry.Files = files; entry.Bytes = bytes; entry.Skipped = skipped; entry.Result = result;
        try { data.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text += " " + Loc.Get("Status.HistoryNotSaved"); }
        RefreshHistory();
    }

    private void AddExclusion(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFolderDialog { Title = Loc.Get("Excl.PickerTitle") };
        if (picker.ShowDialog(this) != true) return;
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picker.FolderName));
        if (!data.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)) data.Exclusions.Add(path);
        ExclusionsChanged();
    }

    private void RemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (!busy && ExclusionsList.SelectedItem is string path) { data.Exclusions.Remove(path); ExclusionsChanged(); }
    }

    private void ExclusionsChanged()
    {
        ExclusionsList.Items.Refresh();
        ResetCaches();
        FilesGrid.ItemsSource = null;
        lastScan = null;
        ShowMap();
        duplicates = [];
        DuplicatesGrid.ItemsSource = null;
        JunkGrid.ItemsSource = null;
        SetBusy(false);
        SaveSettings();
        Status.Text = Loc.Get("Status.ExclusionsChanged");
    }

    private async void FindDuplicates(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (SimilarMode) { await FindSimilar(); return; }
        if (!Storage.SafePath(folder)) { Status.Text = Loc.Get("Dup.ChooseFolder"); return; }
        SaveSettings();
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        var exclusions = data.Exclusions.ToArray();
        SetBusy(true);
        Status.Text = Loc.Get("Dup.Grouping");
        int skipped = 0;
        var progress = new Progress<string>(text => { if (busy && token == cancellation?.Token && !token.IsCancellationRequested) Status.Text = text; });
        try
        {
            var options = new DuplicateOptions(Sequential: data.SequentialHashing);
            var result = await Task.Run(() => Analysis.FindDuplicates(folder, exclusions, token, text => ((IProgress<string>)progress).Report(text), () => skipped++, options with { Cache = HashCache.Load() }), token);
            token.ThrowIfCancellationRequested();
            duplicates = result;
            DuplicatesGrid.ItemsSource = duplicates;
            long extra = duplicates.GroupBy(f => f.Group).Sum(g => g.First().File.Size * (g.Count() - 1));
            Status.Text = Loc.Format("Dup.Result", duplicates.Select(d => d.Group).Distinct().Count(), Storage.Format(extra), skipped);
        }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Dup.Cancelled"); }
        catch (Exception ex) { Status.Text = Loc.Format("Dup.Error", ex.Message); }
        finally { SetBusy(false); }
    }

    private void RevealDuplicate(object sender, RoutedEventArgs e)
    {
        if (SimilarMode) { RevealSimilar(); return; }
        if (DuplicatesGrid.SelectedItem is not DuplicateFile file) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void TrashSelected(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool duplicateMode = sender is Button { Name: "TrashDuplicatesButton" };
        CleanupPlan plan;
        if (duplicateMode && SimilarMode)
        {
            if (!TrySimilarPlan(out var similarPlan)) return;
            plan = similarPlan!;
        }
        else if (duplicateMode)
        {
            var selectedCopies = DuplicatesGrid.SelectedItems.OfType<DuplicateFile>().ToArray();
            if (selectedCopies.Length == 0) { Status.Text = Loc.Get("Recycle.SelectFiles"); return; }
            try { plan = Duplicates.Plan(selectedCopies, duplicates); }
            catch (InvalidOperationException) { Status.Text = Loc.Get("Recycle.KeepOne"); return; }
        }
        else
        {
            var files = FilesGrid.SelectedItems.OfType<LargeFile>().ToArray();
            if (files.Length == 0) { Status.Text = Loc.Get("Recycle.SelectFiles"); return; }
            plan = new(HistoryAction.Recycle, CleanupMode.Recycle, [new("large", Loc.Get("Nav.Files"), files.Select(f => PlanItem.From(f, "large")).ToList())])
            { Details = string.Join("\n", files.Select(f => f.Path)) };
        }
        await RecyclePlan(plan);
    }

    /// <summary>Shows the plan, then moves what is still included to the Recycle Bin on an STA thread and logs every file.</summary>
    private async Task RecyclePlan(CleanupPlan plan, bool ignoreJunk = true)
    {
        if (!Preview(plan)) return;
        string details = string.Join("\n", plan.Included.Select(i => i.Path));
        HistoryEntry history;
        Journal journal;
        try
        {
            string id = Cleanup.NewId();
            journal = Journal.Begin(id, HistoryAction.Recycle, CleanupMode.Recycle, details);
            history = BeginHistory(HistoryAction.Recycle, details, id, CleanupMode.Recycle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Recycle.HistoryFailed"); return; }
        cleaning = true;
        SetBusy(true);
        var owner = new WindowInteropHelper(this).Handle;
        var exclusions = data.Exclusions.ToArray();
        int total = plan.Count;
        var progress = new Progress<int>(n => { if (cleaning) Status.Text = Loc.Format("Recycle.Progress", n, total); });
        var completion = new TaskCompletionSource<CleanupResult>();
        var thread = new Thread(() =>
        {
            try
            {
                using (journal)
                {
                    var outcome = Recycle.Execute(plan, exclusions, owner, journal, CancellationToken.None, progress, ignoreJunk);
                    journal.Finish(outcome);
                    completion.SetResult(outcome);
                }
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        CleanupResult? result = null;
        try
        {
            result = await completion.Task;
            Status.Text = Loc.Format("Recycle.Done", result.Done, Storage.Format(result.Bytes), result.Skipped);
            if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(8)), Loc.Get("Recycle.Skipped"));
        }
        catch (Exception ex) { Status.Text = Loc.Format("Recycle.Interrupted", ex.Message); }
        finally
        {
            FinishHistory(history, result?.Done ?? 0, result?.Bytes ?? 0, result?.Skipped ?? 0, result?.Result ?? HistoryResult.Interrupted);
            FilesGrid.ItemsSource = FilesGrid.Items.OfType<LargeFile>().Where(f => File.Exists(f.Path)).ToList();
            duplicates = duplicates.Where(d => File.Exists(d.Path)).GroupBy(d => d.Group).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            DuplicatesGrid.ItemsSource = duplicates;
            similar = similar.Where(i => File.Exists(i.Path)).GroupBy(i => i.Group).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            SimilarGrid.ItemsSource = similar;
            previewGroup = -1;
            ShowPreviews(similar.FirstOrDefault()?.Group ?? 0);
            if (JunkGrid.ItemsSource is IEnumerable<JunkEntry> junk) JunkGrid.ItemsSource = junk.Where(j => Path.Exists(j.Path)).ToList();
            cleaning = false; SetBusy(false); RefreshDisk();
        }
    }
}
