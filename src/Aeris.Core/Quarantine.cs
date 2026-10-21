using System.IO;

namespace Aeris;

public record RestoreResult(int Restored, int Conflicts, int Missing, int Failed, List<string> Errors)
{
    public string Summary => Loc.Format("Restore.Summary", Restored, Conflicts, Missing + Failed);
}

/// <summary>
/// Files moved aside instead of being deleted: %LOCALAPPDATA%\Aeris\Quarantine\&lt;operation&gt;\&lt;drive&gt;\&lt;path&gt;.
/// Only files on the same volume are moved, so a quarantine never copies data.
/// </summary>
public static class Quarantine
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "Quarantine");
    public static string Folder { get; set; } = DefaultFolder;

    public static string FolderFor(string id) => Path.Combine(Folder, id);

    public static string PathFor(string operationFolder, string original)
    {
        string full = Path.GetFullPath(original);
        string root = Path.GetPathRoot(full)!;
        string drive = OperatingSystem.IsWindows() ? root.TrimEnd('\\', ':').Replace(":", "") : "root";
        return Path.Combine(operationFolder, drive, full[root.Length..]);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string path, System.Text.StringBuilder volume, int length);

    /// <summary>The mount point a path lives on, so a volume mounted inside a folder counts as a different volume.</summary>
    private static string VolumeOf(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            string existing = Path.GetFullPath(path);
            while (!Path.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
            return LinuxFiles.Read(existing).Device;
        }
        var buffer = new System.Text.StringBuilder(1024);
        return GetVolumePathName(Path.GetFullPath(path), buffer, buffer.Capacity) ? buffer.ToString() : Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
    }

    public static bool SameVolume(string path) => string.Equals(VolumeOf(path), VolumeOf(Folder), StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Operations() =>
        Directory.Exists(Folder) ? Directory.GetDirectories(Folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal) : [];

    public static long Size(string? id = null)
    {
        string folder = id == null ? Folder : FolderFor(id);
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var file in Storage.Walk(folder, CancellationToken.None, () => { }))
            try { total += file.Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Moves quarantined files back. A file whose original place is taken is left in quarantine, never overwritten.</summary>
    public static RestoreResult Restore(string id, IEnumerable<JournalItem> items, CancellationToken token)
    {
        int restored = 0, conflicts = 0, missing = 0, failed = 0;
        var errors = new List<string>();
        string folder = Path.GetFullPath(FolderFor(id));
        foreach (var item in items.Where(i => i.Result == Outcome.Quarantined && i.Stored != null))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string stored = Path.GetFullPath(item.Stored!);
                if (!PathGuard.IsInside(stored, folder) || !File.Exists(stored)) { missing++; continue; }
                if (File.Exists(item.Path) || Directory.Exists(item.Path)) { conflicts++; continue; }
                string parent = Path.GetDirectoryName(item.Path)!;
                string existing = parent;
                while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } up) existing = up;
                if (!Storage.SafePath(existing)) { failed++; errors.Add($"{item.Path}: {Loc.Get("Error.Links")}"); continue; }
                Directory.CreateDirectory(parent);
                if (!Storage.SafePath(parent)) { failed++; errors.Add($"{item.Path}: {Loc.Get("Error.Links")}"); continue; }
                File.Move(stored, item.Path);
                restored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { failed++; if (errors.Count < 200) errors.Add($"{item.Path}: {e.Message}"); }
        }
        RemoveEmpty(folder);
        return new(restored, conflicts, missing, failed, errors);
    }

    /// <summary>Deletes operations older than <paramref name="days"/>, then the oldest ones until the quarantine fits in <paramref name="limit"/>.</summary>
    public static List<string> Purge(int days, long limit, DateTime? now = null)
    {
        var removed = new List<string>();
        var cutoff = (now ?? DateTime.Now).AddDays(-days);
        var operations = Operations().Select(id => (Id: id, Time: TimeOf(id), Size: Size(id))).ToList();
        long total = operations.Sum(o => o.Size);
        foreach (var operation in operations)
        {
            if (operation.Time >= cutoff && total <= limit) continue;
            if (Delete(operation.Id)) { removed.Add(operation.Id); total -= operation.Size; }
        }
        return removed;
    }

    public static bool Delete(string id)
    {
        string folder = FolderFor(id);
        try
        {
            if (!Directory.Exists(folder)) return true;
            foreach (var file in Storage.Walk(folder, CancellationToken.None, () => { }))
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
            }
            RemoveEmpty(folder);
            return !Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static DateTime TimeOf(string id) =>
        id.Length >= 15 && DateTime.TryParseExact(id[..15], "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var time)
            ? time : Directory.GetCreationTime(FolderFor(id));

    private static void RemoveEmpty(string folder)
    {
        if (!Directory.Exists(folder) || !Storage.SafePath(folder)) return;
        foreach (string sub in Directory.GetDirectories(folder))
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) RemoveEmpty(sub);
        try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
