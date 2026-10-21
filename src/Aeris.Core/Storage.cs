using System.ComponentModel;
using System.IO;

namespace Aeris;

public record LargeFile(string Path, long Size, long Modified = 0, long Created = 0)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DisplaySize => Storage.Format(Size);
}

public class CacheGroup(CleanerRule rule, ResolvedTarget[] targets) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public CleanerRule Rule { get; } = rule;
    public ResolvedTarget[] Targets { get; } = targets;
    public string[] Roots => Targets.Select(t => t.Display).ToArray();
    public bool Selected { get; set; }
    public bool Scanned { get; set; }
    public int Skipped { get; set; }
    public long Total { get; set; }
    public long Eligible { get; set; }
    public List<PlanItem> Items { get; } = [];
    public bool CanClean => Scanned && Eligible > 0;

    public bool IsBrowser => Rule.Group == CleanerGroup.Browsers;
    public string Name => Rule.Name ?? Loc.Get(Rule.NameKey ?? Rule.Id);
    public string Description => Rule.Description ?? (Rule.DescriptionKey is { } key ? Loc.Get(key) : "");
    public string FriendlyLocation => Rule.LocationKey is { } key ? Loc.Format(key, Name) : Loc.Get("Cache.Custom.Location");
    public string GroupName => Loc.Get($"CacheGroup.{Rule.Group}");
    public string RiskText => Loc.Get($"Risk.{Rule.Risk}");
    public string RiskHint => Loc.Get($"Risk.{Rule.Risk}.Hint");
    public bool IsAdvanced => Rule.Risk == Risk.Advanced;
    public bool IsSafe => Rule.Risk == Risk.Safe;
    public string Source => Rule.IsUser ? Loc.Format("Cache.UserRule", System.IO.Path.GetFileName(Rule.Source)) : "";

    public string EligibleText => !Scanned || (Skipped > 0 && Eligible == 0) ? "—" : Storage.Format(Eligible);
    public string TotalText => !Scanned ? Loc.Get("Home.NotChecked") : Skipped > 0 ? Loc.Get("Cache.HasSkipped") : Loc.Format("Cache.Total", Storage.Format(Total));
    public string ScanStatus => !Scanned ? Loc.Get("Cache.CheckFirst") : Skipped > 0 ? Loc.Format("Cache.SomeUnavailable", Skipped) : Roots.Length == 0 ? Loc.Get("Cache.NoFolders") : Loc.Get("Cache.Checked");
    public string RootsTitle => Loc.Format("Cache.ShowFolders", Roots.Length);
    public string RootsText => Roots.Length == 0 ? Loc.Get("Cache.NoFolders") : string.Join("\n\n", Roots);

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>Walks the group's folders and records every file old enough to clean.</summary>
    public void Scan(DateTime cutoff, IReadOnlyCollection<string>? exclusions, CancellationToken token, Action<string, FileInfo?>? progress = null)
    {
        Items.Clear(); Total = Eligible = 0; Skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in Targets)
        {
            progress?.Invoke(target.Root, null);
            foreach (var file in Storage.Walk(target.Root, token, () => Skipped++, exclusions, target.Recurse))
            {
                try
                {
                    if (!target.Matches(file.FullName, file.DirectoryName ?? "") || !seen.Add(file.FullName)) continue;
                    long size = file.Length;
                    Total += size;
                    if (Storage.Eligible(file, cutoff))
                    {
                        Eligible += size;
                        Items.Add(new(file.FullName, size, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, Rule.Id, target.Root, target.AllowPrograms));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Skipped++; }
                progress?.Invoke(target.Root, file);
            }
        }
        Scanned = true;
    }
}

public static class Storage
{
    public static void ExportCsv(IEnumerable<LargeFile> files, string path)
    {
        static string Cell(string text)
        {
            string trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".aeris-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(temporary, false, new System.Text.UTF8Encoding(true)))
            {
                writer.WriteLine(Loc.Get("Csv.Header"));
                foreach (var file in files) writer.WriteLine($"{Cell(file.Name)};{file.Size};{Cell(file.Path)}");
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Format(long bytes)
    {
        string[] units = Loc.Get("Units").Split('|');
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return string.Format(Loc.Culture, "{0:0.#} {1}", value, units[unit]);
    }

    public static bool SafePath(string path)
    {
        try
        {
            for (var p = new DirectoryInfo(Path.GetFullPath(path)); p != null; p = p.Parent)
                if ((p.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool IsExcluded(string path, IReadOnlyCollection<string>? exclusions)
    {
        if (exclusions == null || exclusions.Count == 0) return false;
        string full = Path.GetFullPath(path);
        return exclusions.Any(p => string.Equals(full, Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase) || full.StartsWith(Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    /// <summary>
    /// Files under <paramref name="root"/>, depth first. Links, junctions and excluded folders are skipped and
    /// reported through <paramref name="skipped"/>; so are folders that can't be read.
    /// </summary>
    public static IEnumerable<FileInfo> Walk(string root, CancellationToken token, Action skipped, IReadOnlyCollection<string>? exclusions = null, bool recurse = true)
    {
        if (!SafePath(root) || IsExcluded(root, exclusions)) { skipped(); yield break; }
        var pending = new Stack<string>();
        pending.Push(root);
        bool first = true;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            if (!first && !Plain(dir)) { skipped(); continue; }
            first = false;
            IEnumerator<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Listing).GetEnumerator(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped(); continue; }
            using (entries)
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    FileSystemInfo entry;
                    try { if (!entries.MoveNext()) break; entry = entries.Current; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped(); break; }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || IsExcluded(entry.FullName, exclusions)) { skipped(); continue; }
                    if (entry is DirectoryInfo) { if (recurse) pending.Push(entry.FullName); }
                    else if (entry is FileInfo file) yield return file;
                }
        }
    }

    private static bool Plain(string dir)
    {
        try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool Eligible(FileInfo file, DateTime cutoff) => file.LastWriteTimeUtc < cutoff && file.CreationTimeUtc < cutoff;
}
