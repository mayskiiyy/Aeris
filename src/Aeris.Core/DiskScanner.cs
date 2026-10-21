using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Aeris;

public enum FileKind : byte { Other, Video, Archive, Installer, Document, Image, Code, Audio }

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Map = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list) { foreach (string ext in list.Split(' ')) map["." + ext] = kind; }
        Add(FileKind.Video, "mp4 mkv avi mov webm m4v wmv flv mpg mpeg ts m2ts vob 3gp");
        Add(FileKind.Archive, "zip rar 7z tar gz tgz bz2 xz zst cab lz lzma");
        Add(FileKind.Installer, "exe msi msix msixbundle appx appxbundle iso img vhd vhdx vmdk wim esd dmg");
        Add(FileKind.Document, "pdf doc docx xls xlsx ppt pptx txt csv odt ods odp rtf epub md");
        Add(FileKind.Image, "jpg jpeg png gif webp heic heif tif tiff bmp raw cr2 cr3 nef arw dng psd svg ico");
        Add(FileKind.Code, "cs js ts tsx jsx py java c cpp h hpp go rs rb php json xml yml yaml html css scss sql sh ps1 dll pdb obj lib so");
        Add(FileKind.Audio, "mp3 flac wav ogg m4a aac wma opus");
        return map;
    }

    public static FileKind Of(string name) => Map.TryGetValue(Path.GetExtension(name), out var kind) ? kind : FileKind.Other;
}

[Flags]
public enum NodeFlags : byte { None = 0, Directory = 1, HardLinkCopy = 2, Unreadable = 4 }

/// <summary>
/// Every file and folder of a scan as parallel arrays indexed by node number, so a million files need no
/// million objects. A child always has a higher index than its parent.
/// </summary>
public sealed class NodeStore
{
    private string[] names = new string[1024];
    private int[] parents = new int[1024];
    private long[] sizes = new long[1024];
    private long[] allocated = new long[1024];
    private long[] modified = new long[1024];
    private int[] files = new int[1024];
    private NodeFlags[] flags = new NodeFlags[1024];
    private FileKind[] kinds = new FileKind[1024];
    private int[] firstChild = [];
    private int[] nextSibling = [];

    public int Count { get; private set; }

    public int Add(string name, int parent, long size, long allocatedSize, long modifiedTicks, NodeFlags nodeFlags)
    {
        if (Count == names.Length)
        {
            int capacity = names.Length * 2;
            Array.Resize(ref names, capacity); Array.Resize(ref parents, capacity); Array.Resize(ref sizes, capacity);
            Array.Resize(ref allocated, capacity); Array.Resize(ref modified, capacity); Array.Resize(ref files, capacity);
            Array.Resize(ref flags, capacity); Array.Resize(ref kinds, capacity);
        }
        int index = Count++;
        names[index] = name; parents[index] = parent; sizes[index] = size; allocated[index] = allocatedSize;
        modified[index] = modifiedTicks; flags[index] = nodeFlags; files[index] = (nodeFlags & NodeFlags.Directory) != 0 ? 0 : 1;
        kinds[index] = (nodeFlags & NodeFlags.Directory) != 0 ? FileKind.Other : FileKinds.Of(name);
        return index;
    }

    public string Name(int node) => names[node];
    public int Parent(int node) => parents[node];
    public long Size(int node, bool onDisk = false) => onDisk ? allocated[node] : sizes[node];
    public long Modified(int node) => modified[node];
    public int Files(int node) => files[node];
    public NodeFlags Flags(int node) => flags[node];
    public FileKind Kind(int node) => kinds[node];
    public bool IsDirectory(int node) => (flags[node] & NodeFlags.Directory) != 0;

    public string FullPath(int node)
    {
        var parts = new Stack<string>();
        for (int n = node; n >= 0; n = parents[n]) parts.Push(names[n]);
        return Path.Combine(parts.ToArray());
    }

    public IEnumerable<int> Children(int node)
    {
        for (int child = node < firstChild.Length ? firstChild[node] : -1; child >= 0; child = nextSibling[child]) yield return child;
    }

    /// <summary>Adds every node's size into its folders and links children. Runs once after the scan.</summary>
    internal void Finish()
    {
        firstChild = new int[Count];
        nextSibling = new int[Count];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        for (int node = Count - 1; node > 0; node--)
        {
            int parent = parents[node];
            if (parent < 0) continue;
            sizes[parent] += sizes[node]; allocated[parent] += allocated[node]; files[parent] += files[node];
            if (modified[node] > modified[parent] && IsDirectory(parent)) modified[parent] = modified[node];
            nextSibling[node] = firstChild[parent];
            firstChild[parent] = node;
        }
        Array.Resize(ref names, Count); Array.Resize(ref parents, Count); Array.Resize(ref sizes, Count); Array.Resize(ref allocated, Count);
        Array.Resize(ref modified, Count); Array.Resize(ref files, Count); Array.Resize(ref flags, Count); Array.Resize(ref kinds, Count);
    }
}

public record ScanProgress(long Files, long Bytes, string Folder);

public sealed class ScanResult(NodeStore nodes, int skipped, TimeSpan elapsed)
{
    public NodeStore Nodes { get; } = nodes;
    public int Root => 0;
    public int Skipped { get; } = skipped;
    public TimeSpan Elapsed { get; } = elapsed;
    public string RootPath => Nodes.Name(0);

    public IEnumerable<int> FileNodes()
    {
        for (int node = 1; node < Nodes.Count; node++)
            if (!Nodes.IsDirectory(node)) yield return node;
    }

    public LargeFile ToLargeFile(int node) => new(Nodes.FullPath(node), Nodes.Size(node), Nodes.Modified(node));

    /// <summary>Total size per file kind under a node, for the legend.</summary>
    public long[] KindTotals(int under, bool onDisk)
    {
        var totals = new long[Enum.GetValues<FileKind>().Length];
        var pending = new Stack<int>();
        pending.Push(under);
        while (pending.Count > 0)
        {
            int node = pending.Pop();
            if (!Nodes.IsDirectory(node)) { totals[(int)Nodes.Kind(node)] += Nodes.Size(node, onDisk); continue; }
            foreach (int child in Nodes.Children(node)) pending.Push(child);
        }
        return totals;
    }

    /// <summary>The node for a path inside this scan, or -1.</summary>
    public int Find(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(RootPath);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return -1;
        int node = 0;
        foreach (string part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            node = Nodes.Children(node).FirstOrDefault(c => string.Equals(Nodes.Name(c), part, StringComparison.OrdinalIgnoreCase), -1);
            if (node < 0) return -1;
        }
        return node;
    }
}

public interface IDiskScanner
{
    Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token);
}

public record ScanOptions(IReadOnlyCollection<string> Exclusions, int Workers = 0);

/// <summary>
/// Reads folders in parallel with one large GetFileInformationByHandleEx call per buffer, which returns names,
/// sizes, sizes on disk, dates and file IDs together. Links and junctions are not followed, hard links count once
/// and folders that can't be read are counted as skipped.
/// </summary>
public sealed class StandardScanner : IDiskScanner
{
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int BufferSize = 64 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);

    public Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token) =>
        Task.Run(() => Scan(root, options, progress, token), token);

    public static ScanResult Scan(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string full = Path.GetFullPath(root);
        if (!Storage.SafePath(full) || !Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var store = new NodeStore();
        var gate = new object();
        long fileCount = 0, byteCount = 0;
        int skipped = 0, pending = 1;
        var seenIds = new HashSet<(uint, long)>();
        uint volume = VolumeSerial(full);
        store.Add(full, -1, 0, 0, Directory.GetLastWriteTimeUtc(full).Ticks, NodeFlags.Directory);
        var queue = Channel.CreateUnbounded<(int Node, string Path)>();
        queue.Writer.TryWrite((0, full));
        var lastReport = DateTime.UtcNow;

        void Work()
        {
            byte[] buffer = new byte[BufferSize];
            var batch = new List<Entry>();
            while (queue.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                if (!queue.Reader.TryRead(out var item)) continue;
                try
                {
                    batch.Clear();
                    if (Storage.IsExcluded(item.Path, options.Exclusions) || !Read(item.Path, buffer, batch, token)) { Interlocked.Increment(ref skipped); continue; }
                    long added = 0;
                    var subfolders = new List<(int, string)>();
                    lock (gate)
                    {
                        foreach (var entry in batch)
                        {
                            string path = Path.Combine(item.Path, entry.Name);
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || Storage.IsExcluded(path, options.Exclusions)) { Interlocked.Increment(ref skipped); continue; }
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                int node = store.Add(entry.Name, item.Node, 0, 0, entry.Modified, NodeFlags.Directory);
                                subfolders.Add((node, path));
                                continue;
                            }
                            bool copy = entry.Id != 0 && !seenIds.Add((volume, entry.Id));
                            store.Add(entry.Name, item.Node, copy ? 0 : entry.Size, copy ? 0 : entry.Allocated, entry.Modified, copy ? NodeFlags.HardLinkCopy : NodeFlags.None);
                            fileCount++;
                            if (!copy) added += entry.Size;
                        }
                        byteCount += added;
                        Interlocked.Add(ref pending, subfolders.Count);
                        if (progress != null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(100))
                        {
                            lastReport = DateTime.UtcNow;
                            progress.Report(new(fileCount, byteCount, item.Path));
                        }
                    }
                    foreach (var folder in subfolders) queue.Writer.TryWrite(folder);
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0) queue.Writer.TryComplete();
                }
            }
        }

        int workers = options.Workers > 0 ? options.Workers : Math.Clamp(Environment.ProcessorCount, 2, 8);
        var threads = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(Work, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Task.WaitAll(threads); }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException)) { throw new OperationCanceledException(token); }
        token.ThrowIfCancellationRequested();
        store.Finish();
        progress?.Report(new(fileCount, byteCount, full));
        return new(store, skipped, watch.Elapsed);
    }

    private readonly record struct Entry(string Name, FileAttributes Attributes, long Size, long Allocated, long Modified, long Id);

    private static string Long(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// <summary>All entries of one folder. False if the folder can't be opened.</summary>
    private static bool Read(string folder, byte[] buffer, List<Entry> entries, CancellationToken token)
    {
        // FILE_LIST_DIRECTORY, share everything, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT
        using var handle = CreateFile(Long(folder), 0x0001, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(handle, first ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo, buffer, buffer.Length))
                return Marshal.GetLastWin32Error() is 18 or 2; // ERROR_NO_MORE_FILES, or an empty root
            first = false;
            int offset = 0;
            while (true)
            {
                var span = buffer.AsSpan(offset);
                int next = BinaryPrimitives.ReadInt32LittleEndian(span);
                int nameLength = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);
                string name = Encoding.Unicode.GetString(buffer, offset + 104, nameLength);
                if (name is not ("." or ".."))
                    entries.Add(new(name, (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(span[56..]), BinaryPrimitives.ReadInt64LittleEndian(span[40..]),
                        BinaryPrimitives.ReadInt64LittleEndian(span[48..]), DateTime.FromFileTimeUtc(Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(span[24..]))).Ticks,
                        BinaryPrimitives.ReadInt64LittleEndian(span[96..])));
                if (next == 0) break;
                offset += next;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder? fileSystem, int fileSystemSize);

    private static uint VolumeSerial(string path)
    {
        try { return GetVolumeInformation(Path.GetPathRoot(path)!, null, 0, out uint serial, out _, out _, null, 0) ? serial : 0; }
        catch (Exception e) when (e is Win32Exception or ArgumentException) { return 0; }
    }
}
