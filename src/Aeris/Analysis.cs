using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aeris;

public record FolderUsage(string Path, string Name, long Bytes, long Files, double Share)
{
    public string DisplaySize => Storage.Format(Bytes);
    public string Detail => Loc.Format("Map.Detail", Files, Share);
}

public class DuplicateFile(LargeFile file, string hash, string identity, int group)
{
    public LargeFile File { get; } = file;
    public string Hash { get; } = hash;
    public string Identity { get; } = identity;
    public int Group { get; } = group;
    public string Path => File.Path;
    public string Name => File.Name;
    public string DisplaySize => File.DisplaySize;
}

public static class Analysis
{
    private static readonly string[][] Extensions = [[], [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv"], [".zip", ".rar", ".7z", ".tar", ".gz", ".iso"], [".exe", ".msi", ".msix", ".appx"], [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".odt"], [".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".tiff", ".bmp"]];
    public static bool Matches(string path, int type) => type == 0 || (type > 0 && type < Extensions.Length && Extensions[type].Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    public static LargeFile Snapshot(FileInfo file) => new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
    public static bool Unchanged(LargeFile file)
    {
        try
        {
            var current = new FileInfo(file.Path);
            return current.Exists && current.Length == file.Size && current.LastWriteTimeUtc.Ticks == file.Modified && current.CreationTimeUtc.Ticks == file.Created && Storage.SafePath(current.DirectoryName!) && (current.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string FolderKey(string root, string file)
    {
        string relative = System.IO.Path.GetRelativePath(root, file);
        int separator = relative.IndexOf(System.IO.Path.DirectorySeparatorChar);
        return separator < 0 ? root : System.IO.Path.Combine(root, relative[..separator]);
    }

    public static List<FolderUsage> FolderMap(string root, Dictionary<string, (long Bytes, long Files)> sizes)
    {
        long total = sizes.Values.Sum(v => v.Bytes);
        return sizes.OrderByDescending(p => p.Value.Bytes).Select(p => new FolderUsage(p.Key,
            p.Key == root ? Loc.Get("Map.RootFiles") : System.IO.Path.GetFileName(p.Key), p.Value.Bytes, p.Value.Files,
            total == 0 ? 0 : 100d * p.Value.Bytes / total)).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    public static string Identity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) throw new IOException(Loc.Get("Error.FileIdentity"));
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    public static string Hash(FileStream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static List<DuplicateFile> FindDuplicates(string root, IReadOnlyCollection<string> exclusions, CancellationToken token, Action<string> report, Action skipped)
    {
        var sizes = new Dictionary<long, List<LargeFile>>();
        int count = 0;
        foreach (var info in Storage.Walk(root, token, skipped, exclusions))
        {
            try
            {
                var file = Snapshot(info);
                if (file.Size == 0) continue;
                if (!sizes.TryGetValue(file.Size, out var group)) sizes[file.Size] = group = [];
                group.Add(file);
                if (++count % 500 == 0) report(Loc.Format("Dup.GroupingProgress", count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped(); }
        }
        var identities = new HashSet<string>();
        var hashes = new Dictionary<(long Size, string Hash), List<(LargeFile File, string Identity)>>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var candidates in sizes.Values.Where(g => g.Count > 1))
            foreach (var file in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!Unchanged(file)) { skipped(); continue; }
                    if (watch.ElapsedMilliseconds >= 200) { report(Loc.Format("Dup.Hashing", file.Path)); watch.Restart(); }
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string identity = Identity(stream);
                    if (!identities.Add(identity)) continue;
                    string hash = Hash(stream, token);
                    if (!Unchanged(file)) { skipped(); continue; }
                    if (!hashes.TryGetValue((file.Size, hash), out var group)) hashes[(file.Size, hash)] = group = [];
                    group.Add((file, identity));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped(); }
            }
        int number = 0;
        var result = new List<DuplicateFile>();
        foreach (var group in hashes.Where(p => p.Value.Count > 1).OrderByDescending(p => p.Key.Size * (p.Value.Count - 1)))
        {
            number++;
            result.AddRange(group.Value.Select(f => new DuplicateFile(f.File, group.Key.Hash, f.Identity, number)));
        }
        return result;
    }
}
