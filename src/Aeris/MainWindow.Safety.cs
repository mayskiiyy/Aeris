using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Aeris;

public record JunkEntry(string Kind, string Path, bool IsFolder, LargeFile? File)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public partial class MainWindow
{
    private long Limit => data.QuarantineLimitGb * 1024L * 1024 * 1024;

    /// <summary>Expired quarantine and old journals are cleared in the background on start.</summary>
    private void StartMaintenance()
    {
        int days = data.QuarantineDays;
        long limit = Limit;
        _ = Task.Run(() =>
        {
            try
            {
                Quarantine.Purge(days, limit);
                Journal.Rotate(pinned: Quarantine.Operations().ToHashSet());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }).ContinueWith(_ => Dispatcher.BeginInvoke(() => { RefreshQuarantine(); RefreshHistory(); }));
    }

    private void RestoreCleanupSettings()
    {
        bool previous = restoring;
        restoring = true;
        try
        {
            foreach (var item in QuarantineDaysSetting.Items.OfType<ComboBoxItem>()) item.Content = Loc.Plural("Option.Days", int.Parse((string)item.Tag));
            foreach (var item in QuarantineLimitSetting.Items.OfType<ComboBoxItem>()) item.Content = Storage.Format(int.Parse((string)item.Tag) * 1024L * 1024 * 1024);
            ModeSetting.SelectedIndex = data.CleanupMode == CleanupMode.Quarantine ? 1 : 0;
            QuarantineDaysSetting.SelectedItem = Closest(QuarantineDaysSetting, data.QuarantineDays);
            QuarantineLimitSetting.SelectedItem = Closest(QuarantineLimitSetting, data.QuarantineLimitGb);
            foreach (var combo in new[] { ModeSetting, QuarantineDaysSetting, QuarantineLimitSetting, KeepRule }) RefreshSelectionBox(combo);
        }
        finally { restoring = previous; }
    }

    private static ComboBoxItem Closest(ComboBox combo, int value) =>
        combo.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - value)).First();

    private void CleanupSettingChanged(object sender, RoutedEventArgs e)
    {
        if (restoring || !IsInitialized || ModeSetting == null) return;
        if (ModeSetting.SelectedItem is ComboBoxItem { Tag: string mode } && Enum.TryParse<CleanupMode>(mode, out var parsed)) data.CleanupMode = parsed;
        if (QuarantineDaysSetting.SelectedItem is ComboBoxItem { Tag: string days }) data.QuarantineDays = int.Parse(days);
        if (QuarantineLimitSetting.SelectedItem is ComboBoxItem { Tag: string limit }) data.QuarantineLimitGb = int.Parse(limit);
        data.SequentialHashing = SequentialSetting.IsChecked == true;
        SaveSettings();
        UpdateSelection();
        RefreshQuarantine();
    }

    private async void RefreshQuarantine()
    {
        if (QuarantineUsage == null) return;
        long limit = Limit;
        var (size, count) = await Task.Run(() => (Quarantine.Size(), Quarantine.Operations().Count()));
        QuarantineUsage.Text = Loc.Format("Settings.QuarantineUsage", Storage.Format(size), count, Storage.Format(limit));
        QuarantineInfo.Text = count == 0 ? Loc.Get("History.QuarantineEmpty") : Loc.Format("History.QuarantineInfo", Storage.Format(size), count, Loc.Plural("Option.Days", data.QuarantineDays));
        HashCacheInfo.Text = Loc.Format("Settings.HashCacheSize", Storage.Format(HashCache.Size()));
    }

    private async void EmptyQuarantine(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        long size = await Task.Run(() => Quarantine.Size());
        if (size == 0 && !Quarantine.Operations().Any()) { Status.Text = Loc.Get("History.QuarantineEmpty"); return; }
        if (MessageBox.Show(this, Loc.Format("Settings.EmptyQuarantineConfirm", Storage.Format(size)), Loc.Get("Mode.Quarantine"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var left = await Task.Run(() => Quarantine.Operations().Where(id => !Quarantine.Delete(id)).ToList());
            Status.Text = left.Count == 0 ? Loc.Get("Settings.QuarantineEmptied") : Loc.Format("Settings.QuarantinePartlyEmptied", left.Count);
        }
        finally { SetBusy(false); RefreshQuarantine(); RefreshHistory(); }
    }

    private void OpenQuarantine(object sender, RoutedEventArgs e) => OpenFolder(Quarantine.Folder);

    private void OpenRulesFolder(object sender, RoutedEventArgs e) => OpenFolder(Cleaners.UserFolder);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Status.Text = ex.Message; }
    }

    private void RefreshRulesInfo()
    {
        if (RulesInfo == null) return;
        int count = caches.Count(c => c.Rule.IsUser);
        RulesInfo.Text = ruleErrors.Count == 0 ? Loc.Format("Settings.RulesLoaded", count) : Loc.Format("Settings.RulesLoadedWithErrors", count, ruleErrors.Count);
    }

    private List<string> FolderSetting(object sender) => (sender as FrameworkElement)?.Tag as string == "Priority" ? data.PriorityFolders : data.ProtectedFolders;

    private void AddFolderSetting(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var list = FolderSetting(sender);
        var picker = new OpenFolderDialog { Title = Loc.Get("Settings.AddFolder") };
        if (picker.ShowDialog(this) != true) return;
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picker.FolderName));
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        (list == data.PriorityFolders ? PriorityList : ProtectedList).Items.Refresh();
        SaveSettings();
    }

    private void RemoveFolderSetting(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var list = FolderSetting(sender);
        var box = list == data.PriorityFolders ? PriorityList : ProtectedList;
        if (box.SelectedItem is not string path) return;
        list.Remove(path);
        box.Items.Refresh();
        SaveSettings();
    }

    private void ClearHashCache(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { HashCache.Clear(); Status.Text = Loc.Get("Settings.HashCacheCleared"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
        RefreshQuarantine();
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = !busy && HistoryGrid.SelectedItem is HistoryEntry { CanRestore: true };

    private void OpenRecycleBin(object sender, RoutedEventArgs e)
    {
        try { Recycle.Open(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status.Text = ex.Message; }
    }

    private async void RestoreSelected(object sender, RoutedEventArgs e)
    {
        if (busy || HistoryGrid.SelectedItem is not HistoryEntry { CanRestore: true } entry) return;
        var (_, items) = Journal.Read(entry.Id);
        var stored = items.Where(i => i.Result == Outcome.Quarantined).ToList();
        if (stored.Count == 0) { Status.Text = Loc.Get("History.NothingToRestore"); return; }
        string message = Loc.Format("History.RestoreConfirm", stored.Count, Storage.Format(stored.Sum(i => i.Size)), entry.DisplayTime);
        if (MessageBox.Show(this, message, Loc.Get("History.Restore"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        HistoryEntry history;
        try { history = BeginHistory(HistoryAction.Restore, entry.Details, entry.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Clean.HistoryFailed"); return; }
        cleaning = true;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => Quarantine.Restore(entry.Id, stored, CancellationToken.None));
            entry.Restored = !Directory.Exists(Quarantine.FolderFor(entry.Id));
            Status.Text = result.Summary;
            FinishHistory(history, result.Restored, stored.Where(i => !Path.Exists(i.Stored)).Sum(i => i.Size), result.Conflicts + result.Missing + result.Failed,
                result.Conflicts + result.Missing + result.Failed == 0 ? HistoryResult.Completed : HistoryResult.Partial);
            if (result.Conflicts > 0) MessageBox.Show(this, Loc.Format("History.RestoreConflicts", result.Conflicts), Loc.Get("History.Restore"));
            else if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(8)), Loc.Get("History.Restore"));
        }
        catch (Exception ex)
        {
            Status.Text = Loc.Format("Recycle.Interrupted", ex.Message);
            FinishHistory(history, 0, 0, 0, HistoryResult.Interrupted);
        }
        finally { cleaning = false; SetBusy(false); RefreshQuarantine(); RefreshDisk(); }
    }

    private void KeepRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring || KeepRule.SelectedItem is not ComboBoxItem { Tag: string rule }) return;
        data.KeepRule = rule;
        SaveSettings();
    }

    private void SelectExtraCopies(object sender, RoutedEventArgs e)
    {
        if (busy || duplicates.Count == 0) return;
        var extra = DuplicateRules.Extra(duplicates, data.KeepRule, data.PriorityFolders, data.ProtectedFolders).ToHashSet();
        DuplicatesGrid.SelectedItems.Clear();
        foreach (var item in duplicates.Where(extra.Contains)) DuplicatesGrid.SelectedItems.Add(item);
        Status.Text = Loc.Format("Dup.SelectedExtra", extra.Count, Storage.Format(extra.Sum(d => d.File.Size)));
        DuplicatesGrid.Focus();
    }

    private void ClearDuplicateSelection(object sender, RoutedEventArgs e) => DuplicatesGrid.UnselectAll();

    private async void FindJunk(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (!Storage.SafePath(folder)) { Status.Text = Loc.Get("Dup.ChooseFolder"); return; }
        SaveSettings();
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        var exclusions = data.Exclusions.ToArray();
        bool ignoreJunk = IgnoreJunkFiles.IsChecked == true;
        string root = folder;
        SetBusy(true);
        Status.Text = Loc.Get("Junk.Searching");
        var progress = new Progress<string>(path => { if (busy && token == cancellation?.Token && !token.IsCancellationRequested) { Status.Text = Loc.Get("Junk.Searching"); ScanLocation.Text = path; } });
        try
        {
            var result = await Task.Run(() => EmptyFolders.Find(root, exclusions, ignoreJunk, token, ((IProgress<string>)progress).Report), token);
            token.ThrowIfCancellationRequested();
            string folderKind = Loc.Get("Junk.KindFolder"), fileKind = Loc.Get("Junk.KindZero");
            JunkGrid.ItemsSource = result.Folders.Select(f => new JunkEntry(folderKind, f, true, null))
                .Concat(result.ZeroFiles.Select(f => new JunkEntry(fileKind, f.Path, false, f))).ToList();
            JunkGrid.Tag = ignoreJunk;
            Status.Text = Loc.Format("Junk.Result", result.Folders.Count, result.ZeroFiles.Count, result.Skipped);
        }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Dup.Cancelled"); }
        catch (Exception ex) { Status.Text = Loc.Format("Dup.Error", ex.Message); }
        finally { SetBusy(false); }
    }

    private void RevealJunk(object sender, RoutedEventArgs e)
    {
        if (JunkGrid.SelectedItem is not JunkEntry entry) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", entry.IsFolder ? $"\"{entry.Path}\"" : $"/select,\"{entry.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status.Text = ex.Message; }
    }

    private void SelectAllJunk(object sender, RoutedEventArgs e)
    {
        JunkGrid.SelectAll();
        JunkGrid.Focus();
    }

    private async void TrashJunk(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var selected = JunkGrid.SelectedItems.OfType<JunkEntry>().ToList();
        if (selected.Count == 0) { Status.Text = Loc.Get("Recycle.SelectFiles"); return; }
        var plan = EmptyFolders.Plan(selected.Where(j => j.IsFolder).Select(j => j.Path), selected.Where(j => j.File != null).Select(j => j.File!));
        await RecyclePlan(plan, JunkGrid.Tag as bool? ?? true);
    }
}
