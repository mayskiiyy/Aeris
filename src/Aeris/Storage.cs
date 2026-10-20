using System.ComponentModel;
using System.IO;

namespace Aeris;

public record LargeFile(string Path, long Size, long Modified = 0, long Created = 0)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DisplaySize => Storage.Format(Size);
}

public enum CacheKind { UserTemp, WindowsTemp, Chromium, Gecko }

public class CacheGroup(CacheKind kind, string[] roots, string browser = "") : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public CacheKind Kind { get; } = kind;
    public string[] Roots { get; } = roots;
    public bool Selected { get; set; }
    public bool Scanned { get; set; }
    public int Skipped { get; set; }
    public long Total { get; set; }
    public long Eligible { get; set; }
    public bool CanClean => Scanned && Eligible > 0;

    public bool IsBrowser => Kind is CacheKind.Chromium or CacheKind.Gecko;
    public string Name => IsBrowser ? browser : Loc.Get($"Cache.{Kind}.Name");
    public string Description => Loc.Get($"Cache.{Kind}.Description");
    public string FriendlyLocation => IsBrowser ? Loc.Format("Cache.Browser.Location", Name) : Loc.Get($"Cache.{Kind}.Location");

    public string EligibleText => !Scanned || (Skipped > 0 && Eligible == 0) ? "—" : Storage.Format(Eligible);
    public string TotalText => !Scanned ? Loc.Get("Home.NotChecked") : Skipped > 0 ? Loc.Get("Cache.HasSkipped") : Loc.Format("Cache.Total", Storage.Format(Total));
    public string ScanStatus => !Scanned ? Loc.Get("Cache.CheckFirst") : Skipped > 0 ? Loc.Format("Cache.SomeUnavailable", Skipped) : Roots.Length == 0 ? Loc.Get("Cache.NoFolders") : Loc.Get("Cache.Checked");
    public string RootsTitle => Loc.Format("Cache.ShowFolders", Roots.Length);
    public string RootsText => Roots.Length == 0 ? Loc.Get("Cache.NoFolders") : string.Join("\n\n", Roots);

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
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
        if (exclusions == null) return false;
        string full = Path.GetFullPath(path);
        return exclusions.Any(p => string.Equals(full, Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase) || full.StartsWith(Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<FileInfo> Walk(string root, CancellationToken token, Action skipped, IReadOnlyCollection<string>? exclusions = null)
    {
        if (!SafePath(root) || IsExcluded(root, exclusions)) { skipped(); yield break; }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            if (!SafePath(dir) || IsExcluded(dir, exclusions)) { skipped(); continue; }
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped(); continue; }
            foreach (string entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (IsExcluded(entry, exclusions)) { skipped(); continue; }
                FileInfo? file = null;
                try
                {
                    var attr = File.GetAttributes(entry);
                    if ((attr & FileAttributes.ReparsePoint) != 0) { skipped(); continue; }
                    if ((attr & FileAttributes.Directory) != 0) pending.Push(entry);
                    else { file = new FileInfo(entry); _ = file.Length; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped(); }
                if (file != null) yield return file;
            }
        }
    }

    private static readonly (string Name, string Path)[] ChromiumBrowsers =
    [
        ("Google Chrome", @"Google\Chrome\User Data"),
        ("Google Chrome Beta", @"Google\Chrome Beta\User Data"),
        ("Google Chrome Dev", @"Google\Chrome Dev\User Data"),
        ("Google Chrome Canary", @"Google\Chrome SxS\User Data"),
        ("Microsoft Edge", @"Microsoft\Edge\User Data"),
        ("Microsoft Edge Beta", @"Microsoft\Edge Beta\User Data"),
        ("Microsoft Edge Dev", @"Microsoft\Edge Dev\User Data"),
        ("Microsoft Edge Canary", @"Microsoft\Edge SxS\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("Brave Beta", @"BraveSoftware\Brave-Browser-Beta\User Data"),
        ("Brave Nightly", @"BraveSoftware\Brave-Browser-Nightly\User Data"),
        ("Opera", @"Opera Software\Opera Stable"),
        ("Opera GX", @"Opera Software\Opera GX Stable"),
        ("Vivaldi", @"Vivaldi\User Data"),
        ("Yandex Browser", @"Yandex\YandexBrowser\User Data"),
        ("Chromium", @"Chromium\User Data"),
        ("Thorium", @"Thorium\User Data"),
        ("Cent Browser", @"CentBrowser\User Data"),
        ("Comodo Dragon", @"Comodo\Dragon\User Data"),
        ("Epic Privacy Browser", @"Epic Privacy Browser\User Data"),
        ("Iridium", @"Iridium\User Data"),
        ("Slimjet", @"Slimjet\User Data"),
        ("Avast Secure Browser", @"AVAST Software\Browser\User Data"),
        ("AVG Secure Browser", @"AVG\Browser\User Data"),
        ("CCleaner Browser", @"CCleaner Browser\User Data"),
    ];

    private static readonly (string Name, string Path)[] GeckoBrowsers =
    [
        ("Mozilla Firefox", @"Mozilla\Firefox\Profiles"),
        ("Waterfox", @"Waterfox\Profiles"),
        ("LibreWolf", @"librewolf\Profiles"),
        ("Floorp", @"Floorp\Profiles"),
        ("Zen Browser", @"zen\Profiles"),
        ("Pale Moon", @"Moonchild Productions\Pale Moon\Profiles"),
        ("Basilisk", @"Moonchild Productions\Basilisk\Profiles"),
        ("SeaMonkey", @"Mozilla\SeaMonkey\Profiles"),
    ];

    public static List<CacheGroup> CacheRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var groups = new List<CacheGroup>
        {
            new(CacheKind.UserTemp, [Path.Combine(local, "Temp")]),
            new(CacheKind.WindowsTemp, [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")]),
        };
        foreach (var (name, relative) in ChromiumBrowsers)
            AddBrowser(groups, CacheKind.Chromium, name, ChromiumCaches(Path.Combine(local, relative)));
        foreach (var (name, relative) in GeckoBrowsers)
            AddBrowser(groups, CacheKind.Gecko, name, GeckoCaches(Path.Combine(local, relative)));
        return groups;
    }

    private static void AddBrowser(List<CacheGroup> groups, CacheKind kind, string name, List<string> roots)
    {
        var known = groups.SelectMany(g => g.Roots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        roots.RemoveAll(known.Contains);
        if (roots.Count > 0) groups.Add(new(kind, roots.ToArray(), name));
    }

    private static List<string> ChromiumCaches(string userData)
    {
        var roots = new List<string>();
        try
        {
            if (!Directory.Exists(userData) || !SafePath(userData)) return roots;
            var profiles = Directory.GetDirectories(userData)
                .Where(p => Path.GetFileName(p) is var leaf && (leaf == "Default" || leaf.StartsWith("Profile ", StringComparison.Ordinal)))
                .Prepend(userData);
            foreach (string profile in profiles)
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    string path = Path.Combine(profile, cache);
                    if (Directory.Exists(path)) roots.Add(path);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return roots;
    }

    private static List<string> GeckoCaches(string profiles)
    {
        var roots = new List<string>();
        try
        {
            if (!Directory.Exists(profiles) || !SafePath(profiles)) return roots;
            foreach (string profile in Directory.GetDirectories(profiles))
            {
                string cache = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache)) roots.Add(cache);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return roots;
    }

    public static bool Eligible(FileInfo file, DateTime cutoff) => file.LastWriteTimeUtc < cutoff && file.CreationTimeUtc < cutoff;

    internal static long DeleteEligible(FileInfo file, DateTime cutoff)
    {
        file.Refresh();
        if (!Eligible(file, cutoff)) return -1;
        if (!SafePath(file.DirectoryName!) || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Loc.Get("Error.Links"));
        long size = file.Length;
        file.Delete();
        return size;
    }

    public static (long bytes, int files, int skipped) Clean(IEnumerable<CacheGroup> groups, DateTime cutoff, CancellationToken token, IReadOnlyCollection<string>? exclusions = null)
    {
        if (cutoff.Kind != DateTimeKind.Utc || cutoff > DateTime.UtcNow.AddHours(-48))
            throw new ArgumentOutOfRangeException(nameof(cutoff), Loc.Get("Error.MinRetention"));
        long bytes = 0;
        int count = 0, skipped = 0;
        var allowed = CacheRoots().SelectMany(g => g.Roots).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = groups.SelectMany(g => g.Roots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(root => !allowed.Contains(root)))
            throw new InvalidOperationException(Loc.Get("Error.NotAllowed"));
        foreach (string root in roots)
        {
            foreach (var file in Walk(root, token, () => skipped++, exclusions))
            {
                try
                {
                    long size = DeleteEligible(file, cutoff);
                    if (size < 0) continue;
                    bytes += size;
                    count++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        return (bytes, count, skipped);
    }
}
