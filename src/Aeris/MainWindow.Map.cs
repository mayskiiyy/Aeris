using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Aeris;

public record MapEntry(int Node, string Name, long Size, double Share, long Modified, bool IsDirectory)
{
    public string DisplaySize => Storage.Format(Size);
    public string DisplayShare => string.Format(Loc.Culture, "{0:0.#}", Share);
    public string DisplayModified => Modified == 0 ? "" : new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("d", Loc.Culture);
}

public record Breadcrumb(int Node, string Name, bool First);

public record LegendEntry(string Name, string Size, Brush Brush);

public partial class MainWindow
{
    private readonly IDiskScanner scanner = new StandardScanner();
    private ScanResult? lastScan;
    private int mapNode;
    private bool OnDisk => SizeMode.SelectedIndex == 1;

    private void InitializeMap()
    {
        Treemap.FolderOpened += node => { mapNode = node; ShowMap(); };
        Treemap.NodeSelected += SelectInMapGrid;
        Treemap.FileActivated += node => Reveal(lastScan!.Nodes.FullPath(node), false);
    }

    private async Task ScanFolder()
    {
        if (!Storage.SafePath(folder)) { MessageBox.Show(this, Loc.Get("Scan.FolderUnavailable"), Loc.Get("Scan.CannotStart")); return; }
        SaveSettings();
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        var exclusions = data.Exclusions.ToArray();
        SetBusy(true);
        Status.Text = Loc.Get("Status.AnalyzingFolder");
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!busy || token != cancellation?.Token || token.IsCancellationRequested) return;
            Status.Text = Loc.Format("Status.FilesChecked", p.Files, Storage.Format(p.Bytes), p.Folder);
            ScanLocation.Text = p.Folder;
        });
        try
        {
            var result = await scanner.ScanAsync(folder, new(exclusions), progress, token);
            token.ThrowIfCancellationRequested();
            lastScan = result;
            mapNode = result.Root;
            await RefreshLargeFiles();
            ShowMap();
            Status.Text = Loc.Format("Status.ScanDoneTimed", result.Nodes.Files(0), Storage.Format(result.Nodes.Size(0)), result.Skipped, result.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Status.ScanCancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Format("Status.ScanError", ex.Message); }
        finally { SetBusy(false); RefreshDisk(); }
    }

    /// <summary>The largest files of the last scan for the current size and type filters, without scanning again.</summary>
    private async Task RefreshLargeFiles()
    {
        if (lastScan == null) return;
        var scan = lastScan;
        long minimum = long.Parse((string)((ComboBoxItem)Threshold.SelectedItem).Tag) * 1024 * 1024;
        int fileType = FileType.SelectedIndex;
        var (files, matches) = await Task.Run(() =>
        {
            var largest = new PriorityQueue<int, long>();
            long found = 0;
            foreach (int node in scan.FileNodes())
            {
                long size = scan.Nodes.Size(node);
                if (size < minimum || !Analysis.Matches(scan.Nodes.Name(node), fileType)) continue;
                found++;
                largest.Enqueue(node, size);
                if (largest.Count > 200) largest.Dequeue();
            }
            var list = new List<LargeFile>();
            foreach (int node in largest.UnorderedItems.Select(i => i.Element))
                try
                {
                    var info = new FileInfo(scan.Nodes.FullPath(node));
                    if (info.Exists) list.Add(Analysis.Snapshot(info));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (list.OrderByDescending(f => f.Size).ToList(), found);
        });
        if (scan != lastScan) return;
        FilesGrid.ItemsSource = files;
        int shown = files.Count;
        SetFilesHint(() => matches == 0 ? Loc.Get("Files.NoneFound") : Loc.Format("Files.Found", matches, shown));
        ExportButton.IsEnabled = TrashFilesButton.IsEnabled = !busy && files.Count > 0;
    }

    private void ShowMap()
    {
        MapScanButton.Content = Loc.Get(lastScan != null && SameFolder(lastScan.RootPath, folder) ? "Map.Rescan" : "Common.Scan");
        if (lastScan == null || !SameFolder(lastScan.RootPath, folder))
        {
            Treemap.Show(null, 0, false);
            MapGrid.ItemsSource = null;
            Breadcrumbs.ItemsSource = null;
            Legend.ItemsSource = null;
            MapUpButton.IsEnabled = false;
            MapFooter.Text = Loc.Get("Map.ScanFirst");
            return;
        }
        var nodes = lastScan.Nodes;
        bool disk = OnDisk;
        Treemap.Show(lastScan, mapNode, disk);
        long total = Math.Max(1, nodes.Size(mapNode, disk));
        MapGrid.ItemsSource = nodes.Children(mapNode)
            .Select(c => new MapEntry(c, nodes.Name(c), nodes.Size(c, disk), 100.0 * nodes.Size(c, disk) / total, nodes.Modified(c), nodes.IsDirectory(c)))
            .OrderByDescending(e => e.Size).ToList();
        var trail = new List<Breadcrumb>();
        for (int n = mapNode; n >= 0; n = nodes.Parent(n)) trail.Insert(0, new(n, n == 0 ? Path.TrimEndingDirectorySeparator(nodes.Name(n)) : nodes.Name(n), n == 0));
        Breadcrumbs.ItemsSource = trail;
        MapUpButton.IsEnabled = !busy && mapNode != 0;
        long[] kinds = lastScan.KindTotals(mapNode, disk);
        Legend.ItemsSource = Enum.GetValues<FileKind>().Where(k => kinds[(int)k] > 0).OrderByDescending(k => kinds[(int)k])
            .Select(k => new LegendEntry(Loc.Get($"FileKind.{k}"), Storage.Format(kinds[(int)k]), TryFindResource($"Kind.{k}") as Brush ?? Brushes.Gray)).ToList();
        MapFooter.Text = Loc.Format(disk ? "Map.FooterDisk" : "Map.FooterLogical", nodes.Files(mapNode), Storage.Format(nodes.Size(mapNode, disk)));
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private void SizeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Treemap != null && IsLoaded) ShowMap();
    }

    private void MapUp(object sender, RoutedEventArgs e)
    {
        if (lastScan == null || mapNode == 0) return;
        mapNode = lastScan.Nodes.Parent(mapNode);
        ShowMap();
    }

    private void BreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int node } && lastScan != null) { mapNode = node; ShowMap(); }
    }

    private void SelectInMapGrid(int node)
    {
        if (lastScan == null || MapGrid.ItemsSource is not List<MapEntry> entries) return;
        int child = node;
        while (child >= 0 && lastScan.Nodes.Parent(child) != mapNode) child = lastScan.Nodes.Parent(child);
        var entry = entries.FirstOrDefault(x => x.Node == child);
        MapGrid.SelectedItem = entry;
        if (entry != null) MapGrid.ScrollIntoView(entry);
    }

    private void MapGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapGrid.SelectedItem is MapEntry entry && Treemap.Selected != entry.Node) Treemap.Select(entry.Node);
    }

    private void MapGridOpen(object sender, MouseButtonEventArgs e)
    {
        if (MapGrid.SelectedItem is not MapEntry entry || lastScan == null) return;
        if (entry.IsDirectory) { mapNode = entry.Node; ShowMap(); }
        else Reveal(lastScan.Nodes.FullPath(entry.Node), false);
    }

    /// <summary>The node chosen on the map or in the list, or the open folder when nothing is chosen.</summary>
    private int ChosenNode => Treemap.Selected >= 0 ? Treemap.Selected : MapGrid.SelectedItem is MapEntry entry ? entry.Node : mapNode;

    private void OpenMapFolder(object sender, RoutedEventArgs e)
    {
        if (lastScan == null) { OpenFolder(folder); return; }
        int node = ChosenNode;
        Reveal(lastScan.Nodes.FullPath(node), lastScan.Nodes.IsDirectory(node));
    }

    private void Reveal(string path, bool folderItself)
    {
        if (!Path.Exists(path)) { Status.Text = Loc.Get("Files.Moved"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", folderItself ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status.Text = Loc.Format("Files.ExplorerFailed", ex.Message); }
    }

    private void MapReveal(object sender, RoutedEventArgs e)
    {
        if (lastScan != null) Reveal(lastScan.Nodes.FullPath(ChosenNode), false);
    }

    private void MapCopyPath(object sender, RoutedEventArgs e)
    {
        if (lastScan == null) return;
        try { Clipboard.SetText(lastScan.Nodes.FullPath(ChosenNode)); Status.Text = Loc.Get("Map.PathCopied"); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Status.Text = ex.Message; }
    }

    private void MapExclude(object sender, RoutedEventArgs e)
    {
        if (busy || lastScan == null) return;
        int node = ChosenNode;
        string path = lastScan.Nodes.FullPath(node);
        if (!lastScan.Nodes.IsDirectory(node)) path = Path.GetDirectoryName(path)!;
        if (MessageBox.Show(this, Loc.Format("Map.ExcludeConfirm", path), Loc.Get("Nav.Settings"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!data.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)) data.Exclusions.Add(path);
        ExclusionsChanged();
    }

    private async void MapRecycle(object sender, RoutedEventArgs e)
    {
        if (busy || lastScan == null) return;
        int node = ChosenNode;
        if (lastScan.Nodes.IsDirectory(node)) { Status.Text = Loc.Get("Map.RecycleFilesOnly"); return; }
        FileInfo info;
        try { info = new FileInfo(lastScan.Nodes.FullPath(node)); _ = info.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = Loc.Get("Files.Moved"); return; }
        var file = Analysis.Snapshot(info);
        var plan = new CleanupPlan(HistoryAction.Recycle, CleanupMode.Recycle, [new("map", Loc.Get("Nav.Map"), [PlanItem.From(file, "map")])]) { Details = file.Path };
        await RecyclePlan(plan);
    }
}
