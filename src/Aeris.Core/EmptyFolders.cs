using System.IO;

namespace Aeris;

public record JunkResult(List<string> Folders, List<LargeFile> ZeroFiles, int Skipped);

/// <summary>Empty folders and zero-byte files. Nothing system-owned, linked or inside app data is ever offered.</summary>
public static class EmptyFolders
{
    private static readonly string[] Junk = ["desktop.ini", "thumbs.db"];
    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };

    public static bool IsJunk(string name) => Junk.Contains(name, FileSystemPaths.Comparer);

    /// <summary>No files and no non-empty subfolders, recursively. A link or unreadable entry counts as content.</summary>
    public static bool IsEmpty(string path, bool ignoreJunk)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Listing))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is DirectoryInfo dir) { if (!IsEmpty(dir.FullName, ignoreJunk)) return false; }
                else if (!ignoreJunk || !IsJunk(entry.Name)) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A folder Windows or an app relies on: system or hidden-system folders, links, known folders and anything in AppData, Windows or Program Files.</summary>
    public static bool IsProtected(string path, FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return true;
        return PathGuard.IsKnownFolder(path) || PathGuard.IsProtectedArea(path);
    }

    public static JunkResult Find(string root, IReadOnlyCollection<string> exclusions, bool ignoreJunk, CancellationToken token, Action<string>? progress = null)
    {
        var folders = new List<string>();
        var zero = new List<LargeFile>();
        int skipped = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        bool Visit(DirectoryInfo dir, bool isRoot)
        {
            token.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds >= 200) { progress?.Invoke(dir.FullName); watch.Restart(); }
            if (Storage.IsExcluded(dir.FullName, exclusions)) { skipped++; return false; }
            int mark = folders.Count;
            bool empty = true;
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", Listing).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; return false; }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { empty = false; continue; }
                if (entry is DirectoryInfo sub)
                {
                    if (!Visit(sub, false)) empty = false;
                }
                else if (entry is FileInfo file)
                {
                    if (ignoreJunk && IsJunk(file.Name)) continue;
                    empty = false;
                    if (file.Length == 0 && (file.Attributes & FileAttributes.System) == 0 && !Storage.IsExcluded(file.FullName, exclusions) && !PathGuard.IsProtectedArea(file.FullName))
                        zero.Add(Analysis.Snapshot(file));
                }
            }
            if (empty && !isRoot && !IsProtected(dir.FullName, dir.Attributes))
            {
                folders.RemoveRange(mark, folders.Count - mark);
                folders.Add(dir.FullName);
            }
            return empty && !IsProtected(dir.FullName, dir.Attributes);
        }

        if (!Storage.SafePath(root)) return new([], [], 1);
        Visit(new DirectoryInfo(root), true);
        return new(folders, zero, skipped);
    }

    public static CleanupPlan Plan(IEnumerable<string> folders, IEnumerable<LargeFile> zeroFiles)
    {
        var categories = new List<PlanCategory>();
        var folderItems = folders.Select(f => new PlanItem(f, 0, 0, 0, "empty-folders") { IsFolder = true }).ToList();
        var fileItems = zeroFiles.Select(f => PlanItem.From(f, "zero-files")).ToList();
        if (folderItems.Count > 0) categories.Add(new("empty-folders", Loc.Get("Junk.EmptyFolders"), folderItems));
        if (fileItems.Count > 0) categories.Add(new("zero-files", Loc.Get("Junk.ZeroFiles"), fileItems));
        return new(HistoryAction.Recycle, CleanupMode.Recycle, categories) { Details = string.Join("\n", folderItems.Concat(fileItems).Select(i => i.Path)) };
    }
}
