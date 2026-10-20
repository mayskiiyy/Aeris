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
        MapList.ItemsSource = null; duplicates = []; DuplicatesGrid.ItemsSource = null;
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
        FolderLabel.Text = MapFolder.Text = DuplicateFolder.Text = folder;
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

    private HistoryEntry BeginHistory(string action, string details)
    {
        var entry = new HistoryEntry { Action = action, Details = details };
        data.History.Add(entry);
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
        MapList.ItemsSource = null;
        duplicates = [];
        DuplicatesGrid.ItemsSource = null;
        SetBusy(false);
        SaveSettings();
        Status.Text = Loc.Get("Status.ExclusionsChanged");
    }

    private void OpenMapFolder(object sender, RoutedEventArgs e)
    {
        if (MapList.SelectedItem is not FolderUsage usage) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{usage.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void FindDuplicates(object sender, RoutedEventArgs e)
    {
        if (busy) return;
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
            var result = await Task.Run(() => Analysis.FindDuplicates(folder, exclusions, token, text => ((IProgress<string>)progress).Report(text), () => skipped++), token);
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
        if (DuplicatesGrid.SelectedItem is not DuplicateFile file) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void TrashSelected(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool duplicateMode = sender is Button { Name: "TrashDuplicatesButton" };
        var selectedCopies = DuplicatesGrid.SelectedItems.OfType<DuplicateFile>().ToArray();
        var files = duplicateMode ? selectedCopies.Select(f => f.File).ToArray() : FilesGrid.SelectedItems.OfType<LargeFile>().ToArray();
        if (files.Length == 0) { Status.Text = Loc.Get("Recycle.SelectFiles"); return; }
        if (duplicateMode && selectedCopies.GroupBy(f => f.Group).Any(g => duplicates.Where(d => d.Group == g.Key).All(selectedCopies.Contains)))
        { Status.Text = Loc.Get("Recycle.KeepOne"); return; }
        string preview = string.Join("\n", files.Take(8).Select(f => f.Path)) + (files.Length > 8 ? "\n…" : "");
        string message = Loc.Format("Recycle.Confirm", files.Length, Storage.Format(files.Sum(f => f.Size)), preview);
        if (MessageBox.Show(this, message, Loc.Get("Recycle.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        HistoryEntry history;
        try { history = BeginHistory(HistoryAction.Recycle, string.Join("\n", files.Select(f => f.Path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Recycle.HistoryFailed"); return; }
        cleaning = true;
        SetBusy(true);
        var owner = new WindowInteropHelper(this).Handle;
        var exclusions = data.Exclusions.ToArray();
        int count = 0, skipped = 0;
        long bytes = 0;
        var errors = new List<string>();
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var file in files)
                {
                    try
                    {
                        if (duplicateMode)
                        {
                            var copy = selectedCopies.First(c => c.File == file);
                            var keeper = duplicates.FirstOrDefault(d => d.Group == copy.Group && !selectedCopies.Contains(d) && Analysis.Unchanged(d.File));
                            if (keeper == null) throw new IOException(Loc.Get("Error.KeeperChanged"));
                            using var original = new FileStream(keeper.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var candidate = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                            if (Analysis.Identity(original) == Analysis.Identity(candidate) || Analysis.Hash(original, CancellationToken.None) != copy.Hash || Analysis.Hash(candidate, CancellationToken.None) != copy.Hash) throw new IOException(Loc.Get("Error.ContentChanged"));
                        }
                        Recycle.Send(file, exclusions, owner);
                        count++; bytes += file.Size;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                    { skipped++; errors.Add($"{file.Name}: {ex.Message}"); }
                }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try { await completion.Task; Status.Text = Loc.Format("Recycle.Done", count, Storage.Format(bytes), skipped); if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors.Take(8)), Loc.Get("Recycle.Skipped")); }
        catch (Exception ex) { Status.Text = Loc.Format("Recycle.Interrupted", ex.Message); }
        finally
        {
            FinishHistory(history, count, bytes, skipped, count == files.Length ? HistoryResult.Completed : HistoryResult.Partial);
            FilesGrid.ItemsSource = FilesGrid.Items.OfType<LargeFile>().Where(f => File.Exists(f.Path)).ToList();
            duplicates = duplicates.Where(d => File.Exists(d.Path)).GroupBy(d => d.Group).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            DuplicatesGrid.ItemsSource = duplicates;
            MapList.ItemsSource = null;
            cleaning = false; SetBusy(false); RefreshDisk();
        }
    }
}
