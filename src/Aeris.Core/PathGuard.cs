using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Aeris;

/// <summary>Decides which folders may be cleaned and whether a file really lies inside one of them.</summary>
public static class PathGuard
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint buffer);

    private static readonly ConcurrentDictionary<string, string> Problems = new(FileSystemPaths.Comparer);

    private static string[] Folders(params string?[] paths) =>
        paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => Canonical(p!)).Distinct(FileSystemPaths.Comparer).ToArray();

    private static string Known(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    private static readonly Lazy<string[]> PersonalFolders = new(() => Folders(
        Known(Environment.SpecialFolder.MyDocuments), Known(Environment.SpecialFolder.DesktopDirectory),
        Known(Environment.SpecialFolder.MyPictures), Known(Environment.SpecialFolder.MyVideos), Known(Environment.SpecialFolder.MyMusic),
        Path.Combine(Known(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetEnvironmentVariable("OneDrive")));

    private static readonly Lazy<string[]> WindowsFolders = new(() => Folders(Known(Environment.SpecialFolder.Windows)));

    private static readonly Lazy<string[]> ProgramFolders = new(() => Folders(
        Known(Environment.SpecialFolder.ProgramFiles), Known(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432")));

    private static readonly Lazy<string[]> Containers = new(() => Folders(
        Known(Environment.SpecialFolder.UserProfile), Path.GetDirectoryName(Known(Environment.SpecialFolder.UserProfile)),
        Known(Environment.SpecialFolder.ApplicationData), Known(Environment.SpecialFolder.LocalApplicationData),
        Known(Environment.SpecialFolder.CommonApplicationData), Path.Combine(Known(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        Path.Combine(Known(Environment.SpecialFolder.UserProfile), "AppData")));

    private static readonly Lazy<string[]> AllowedWindows = new(() => Folders(Path.Combine(Known(Environment.SpecialFolder.Windows), "Temp")));

    /// <summary>Full path with "..", trailing separators and 8.3 short names resolved for the part that exists.</summary>
    public static string Canonical(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows() || !full.Contains('~')) return full;
        string existing = full, rest = "";
        while (!Path.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent == null) return full;
            rest = rest.Length == 0 ? Path.GetFileName(existing) : Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }
        var buffer = new StringBuilder(32768);
        uint length = GetLongPathName(existing, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) return full;
        return Path.TrimEndingDirectorySeparator(rest.Length == 0 ? buffer.ToString() : Path.Combine(buffer.ToString(), rest));
    }

    public static bool IsInside(string path, string root) => Inside(Canonical(path), Canonical(root));

    private static bool Inside(string full, string parent) =>
        full.Length > parent.Length && full.StartsWith(parent, FileSystemPaths.Comparison)
        && (Path.EndsInDirectorySeparator(parent) || full[parent.Length] == Path.DirectorySeparatorChar);

    private static bool SameOrInside(string full, string parent) => string.Equals(full, parent, FileSystemPaths.Comparison) || Inside(full, parent);

    /// <summary>
    /// The string key explaining why a folder can't be a cleanup root, or null if it can.
    /// Built-in rules may pass <paramref name="allowPrograms"/> for one exact folder inside Program Files.
    /// </summary>
    public static string? RootProblem(string root, bool allowPrograms = false)
    {
        string full;
        try { full = Canonical(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "Rule.Error.BadPath"; }
        string problem = Problems.GetOrAdd(full + "|" + allowPrograms, _ => Check(full, allowPrograms) ?? "");
        return problem.Length == 0 ? null : problem;
    }

    private static string? Check(string full, bool allowPrograms)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!Path.IsPathFullyQualified(full)) return "Rule.Error.BadPath";
            if (full == "/") return "Rule.Error.DriveRoot";
            string[] system = ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run", "/sbin", "/sys", "/usr", "/var", "/opt", "/snap", "/root"];
            if (system.Any(p => SameOrInside(full, p))) return "Rule.Error.System";
        }
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal)) return "Rule.Error.BadPath";
        string? drive = Path.GetPathRoot(full);
        if (drive == null || string.Equals(Path.TrimEndingDirectorySeparator(drive), full, FileSystemPaths.Comparison)) return "Rule.Error.DriveRoot";
        var guarded = PersonalFolders.Value.Concat(WindowsFolders.Value).Concat(ProgramFolders.Value).Concat(Containers.Value);
        if (guarded.Any(folder => SameOrInside(folder, full)) || (OperatingSystem.IsLinux() && full is "/home" or "/mnt" or "/media")) return "Rule.Error.Protected";
        if (PersonalFolders.Value.Any(f => Inside(full, f))) return "Rule.Error.Personal";
        if (WindowsFolders.Value.Any(f => Inside(full, f)) && !AllowedWindows.Value.Any(a => SameOrInside(full, a))) return "Rule.Error.System";
        if (!allowPrograms && ProgramFolders.Value.Any(f => Inside(full, f))) return "Rule.Error.System";
        return null;
    }

    private static readonly Lazy<string[]> AppAreas = new(() => Folders(
        Known(Environment.SpecialFolder.ApplicationData), Known(Environment.SpecialFolder.LocalApplicationData),
        Known(Environment.SpecialFolder.CommonApplicationData), Path.Combine(Known(Environment.SpecialFolder.UserProfile), "AppData")));

    /// <summary>Inside Windows, Program Files, AppData or ProgramData: places where apps expect their folders to exist.</summary>
    public static bool IsProtectedArea(string path)
    {
        string full = Canonical(path);
        if (OperatingSystem.IsLinux() && new[] { "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run", "/sbin", "/sys", "/usr", "/var", "/opt", "/snap", "/root" }.Any(p => SameOrInside(full, p))) return true;
        return WindowsFolders.Value.Concat(ProgramFolders.Value).Concat(AppAreas.Value).Any(f => SameOrInside(full, f));
    }

    /// <summary>A drive root or one of the known Windows, profile and personal folders themselves.</summary>
    public static bool IsKnownFolder(string path)
    {
        string full = Canonical(path);
        if (Path.GetPathRoot(full) is { } drive && string.Equals(Path.TrimEndingDirectorySeparator(drive), full, FileSystemPaths.Comparison)) return true;
        return PersonalFolders.Value.Concat(WindowsFolders.Value).Concat(ProgramFolders.Value).Concat(Containers.Value)
            .Any(f => string.Equals(f, full, FileSystemPaths.Comparison));
    }

    /// <summary>A file may be cleaned only inside an allowed root and with no links anywhere on its path.</summary>
    public static bool IsAllowedTarget(string path, string root, bool allowPrograms = false)
    {
        try
        {
            if (RootProblem(root, allowPrograms) != null) return false;
            string full = Canonical(path);
            if (!Inside(full, Canonical(root)) || !Storage.SafePath(Path.GetDirectoryName(full)!)) return false;
            return (File.GetAttributes(full) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
