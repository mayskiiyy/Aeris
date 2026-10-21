namespace Aeris;

internal static class PortableScanner
{
    internal static ScanResult Scan(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full) || !Storage.SafePath(full)) throw new DirectoryNotFoundException(full);
        var store = new NodeStore();
        store.Add(full, -1, 0, 0, 0, NodeFlags.Directory);
        var pending = new Stack<(string Path, int Node)>();
        pending.Push((full, 0));
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0;
        long files = 0, bytes = 0;
        var report = System.Diagnostics.Stopwatch.StartNew();
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (!Storage.SafePath(directory.Path) || Storage.IsExcluded(directory.Path, options.Exclusions)) { skipped++; continue; }
            try
            {
                foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || Storage.IsExcluded(entry.FullName, options.Exclusions)) { skipped++; continue; }
                        if (entry is DirectoryInfo)
                        {
                            int node = store.Add(entry.Name, directory.Node, 0, 0, 0, NodeFlags.Directory);
                            pending.Push((entry.FullName, node));
                        }
                        else if (entry is FileInfo file)
                        {
                            var info = LinuxFiles.Read(file.FullName);
                            if (!info.IsRegular) { skipped++; continue; }
                            bool copy = !identities.Add(info.Identity);
                            store.Add(file.Name, directory.Node, copy ? 0 : file.Length, copy ? 0 : checked((long)info.Blocks * 512), file.LastWriteTimeUtc.Ticks, copy ? NodeFlags.HardLinkCopy : NodeFlags.None);
                            files++; if (!copy) bytes += file.Length;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                    if (report.ElapsedMilliseconds >= 200) { progress?.Report(new(files, bytes, directory.Path)); report.Restart(); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        store.Finish();
        return new(store, skipped, watch.Elapsed);
    }
}
