using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aeris;

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

    public const int PrefixBytes = 64 * 1024;

    /// <summary>SHA-256 of the first and last 64 KB together with the size, or of the whole file when it is small enough.</summary>
    public static string PartialHash(FileStream stream, long size, CancellationToken token)
    {
        if (size <= 2L * PrefixBytes) return Hash(stream, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[PrefixBytes];
        stream.ReadExactly(buffer);
        hash.AppendData(buffer);
        stream.Seek(-PrefixBytes, SeekOrigin.End);
        stream.ReadExactly(buffer);
        hash.AppendData(buffer);
        stream.Seek(0, SeekOrigin.Begin);
        return "P" + Convert.ToHexString(hash.GetHashAndReset());
    }

    public static List<DuplicateFile> FindDuplicates(string root, IReadOnlyCollection<string> exclusions, CancellationToken token, Action<string> report, Action skipped, DuplicateOptions? options = null) =>
        FindDuplicates([root], exclusions, token, report, skipped, options);

    /// <summary>
    /// Groups files by size, then by a hash of their first and last 64 KB, then by full SHA-256. Hard links to the same
    /// file count once. Full hashes are reused from <see cref="DuplicateOptions.Cache"/> while a file stays unchanged.
    /// </summary>
    public static List<DuplicateFile> FindDuplicates(IEnumerable<string> roots, IReadOnlyCollection<string> exclusions, CancellationToken token, Action<string> report, Action skipped, DuplicateOptions? options = null)
    {
        options ??= new();
        var sizes = new Dictionary<long, List<LargeFile>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string root in roots)
            foreach (var info in Storage.Walk(root, token, skipped, exclusions))
            {
                try
                {
                    var file = Snapshot(info);
                    if (file.Size == 0 || file.Size < options.MinSize || options.Filter?.Invoke(file.Path) == false || !seen.Add(file.Path)) continue;
                    if (!sizes.TryGetValue(file.Size, out var group)) sizes[file.Size] = group = [];
                    group.Add(file);
                    if (++count % 500 == 0) report(Loc.Format("Dup.GroupingProgress", count));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped(); }
            }

        var skipGate = new object();
        void Skip() { lock (skipGate) skipped(); }
        var parallel = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = options.Sequential ? 1 : Math.Clamp(Environment.ProcessorCount, 2, 4) };
        var candidates = sizes.Values.Where(g => g.Count > 1).SelectMany(g => g).ToList();
        long totalBytes = candidates.Sum(f => Math.Min(f.Size, 2L * PrefixBytes)), doneBytes = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var identities = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var partial = new System.Collections.Concurrent.ConcurrentBag<(LargeFile File, string Identity, string Hash)>();
        void Progress(string key, string path, long bytes)
        {
            long done = Interlocked.Add(ref doneBytes, bytes);
            lock (watch)
            {
                if (watch.ElapsedMilliseconds < 200) return;
                report(Loc.Format(key, Storage.Format(done), Storage.Format(Math.Max(totalBytes, done)), path));
                watch.Restart();
            }
        }

        Parallel.ForEach(candidates, parallel, file =>
        {
            try
            {
                if (!Unchanged(file)) { Skip(); return; }
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                string identity = Identity(stream);
                if (!identities.TryAdd(identity, 0)) return;
                string hash = PartialHash(stream, file.Size, token);
                partial.Add((file, identity, hash));
                Progress("Dup.Prefiltering", file.Path, Math.Min(file.Size, 2L * PrefixBytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Skip(); }
        });

        var full = partial.GroupBy(p => (p.File.Size, p.Hash)).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        totalBytes = full.Where(p => p.Hash.StartsWith('P')).Sum(p => p.File.Size);
        doneBytes = 0;
        var hashes = new System.Collections.Concurrent.ConcurrentBag<(LargeFile File, string Identity, string Hash)>();
        Parallel.ForEach(full, parallel, item =>
        {
            var (file, identity, hash) = item;
            try
            {
                if (hash.StartsWith('P'))
                {
                    if (options.Cache?.Get(file, identity) is { } cached) hash = cached;
                    else
                    {
                        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                        if (Identity(stream) != identity) { Skip(); return; }
                        hash = Hash(stream, token);
                        if (!Unchanged(file)) { Skip(); return; }
                        options.Cache?.Set(file, identity, hash);
                        Progress("Dup.Hashing", file.Path, file.Size);
                    }
                }
                hashes.Add((file, identity, hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Skip(); }
        });
        token.ThrowIfCancellationRequested();
        options.Cache?.Save();

        int number = 0;
        var result = new List<DuplicateFile>();
        foreach (var group in hashes.GroupBy(h => (h.File.Size, h.Hash)).Where(g => g.Count() > 1).OrderByDescending(g => g.Key.Size * (g.Count() - 1)).ThenBy(g => g.Key.Hash, StringComparer.Ordinal))
        {
            number++;
            result.AddRange(group.OrderBy(f => f.File.Path, StringComparer.OrdinalIgnoreCase).Select(f => new DuplicateFile(f.File, group.Key.Hash, f.Identity, number)));
        }
        return result;
    }
}

public record DuplicateOptions(long MinSize = 1, bool Sequential = false, HashCache? Cache = null, Func<string, bool>? Filter = null);
