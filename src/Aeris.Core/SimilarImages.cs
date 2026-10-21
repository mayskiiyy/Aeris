using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Aeris;

public class SimilarImage(string path, long size, long modified, int width, int height, ulong hash)
{
    public string Path { get; } = path;
    public long Size { get; } = size;
    public long Modified { get; } = modified;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public ulong Hash { get; } = hash;
    public int Group { get; set; }
    public int Distance { get; set; }
    public bool Best { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DisplaySize => Storage.Format(Size);
    public string Resolution => $"{Width} × {Height}";
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string DisplayModified => new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("g", Loc.Culture);
    public string DisplayDistance => Best ? Loc.Get("Similar.Best") : Loc.Format("Similar.Distance", Distance);
}

/// <summary>A BK-tree over 64-bit hashes with Hamming distance, so a lookup doesn't compare against every image.</summary>
public sealed class BkTree<T>(Func<T, ulong> key)
{
    private sealed class Node(T value)
    {
        public T Value { get; } = value;
        public Dictionary<int, Node> Children { get; } = [];
    }

    private Node? root;

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public void Add(T value)
    {
        if (root == null) { root = new(value); return; }
        var node = root;
        while (true)
        {
            int distance = Distance(key(node.Value), key(value));
            if (!node.Children.TryGetValue(distance, out var child)) { node.Children[distance] = new(value); return; }
            node = child;
        }
    }

    public List<T> Search(ulong hash, int threshold, CancellationToken token = default)
    {
        var found = new List<T>();
        if (root == null) return found;
        var pending = new Stack<Node>();
        pending.Push(root);
        int visited = 0;
        while (pending.Count > 0)
        {
            if ((visited++ & 1023) == 0) token.ThrowIfCancellationRequested();
            var node = pending.Pop();
            int distance = Distance(key(node.Value), hash);
            if (distance <= threshold) found.Add(node.Value);
            foreach (var (edge, child) in node.Children)
                if (edge >= distance - threshold && edge <= distance + threshold) pending.Push(child);
        }
        return found;
    }
}

/// <summary>
/// Finds visually similar images with a 64-bit difference hash. Images are decoded by Windows Imaging Component
/// to 9×8 pixels. Output size does not bound native decoder work, so input dimensions are limited.
/// </summary>
public static class SimilarImageSearch
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".jfif"];

    public static bool IsImage(string path) => Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static List<List<SimilarImage>> Find(IEnumerable<string> roots, IReadOnlyCollection<string> exclusions, int threshold, long minimumSize, CancellationToken token,
        Action<string> report, Action skipped, HashCache? cache = null)
    {
        var seen = new HashSet<string>(FileSystemPaths.Comparer);
        var images = new System.Collections.Concurrent.ConcurrentBag<SimilarImage>();
        var gate = new object();
        int discovered = 0, done = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Progress(string path)
        {
            lock (gate)
                if (watch.ElapsedMilliseconds >= 200)
                {
                    report(Loc.Format("Similar.StreamProgress", Volatile.Read(ref done), Volatile.Read(ref discovered), path));
                    watch.Restart();
                }
        }
        IEnumerable<LargeFile> Enumerate()
        {
            foreach (string root in roots)
                foreach (var info in Storage.Walk(root, token, () => { lock (gate) skipped(); }, exclusions))
                {
                    Progress(info.FullName);
                    if (!IsImage(info.Name) || !seen.Add(info.FullName)) continue;
                    LargeFile file;
                    try { file = Analysis.Snapshot(info); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lock (gate) skipped(); continue; }
                    if (file.Size < Math.Max(1, minimumSize)) continue;
                    if (file.Size > MaximumFileBytes) { lock (gate) skipped(); continue; }
                    Interlocked.Increment(ref discovered);
                    yield return file;
                }
        }
        report(Loc.Get("Similar.Enumerating"));
        try
        {
            Parallel.ForEach(Enumerate(), new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4) }, file =>
            {
                string identity = $"{file.Size}:{file.Modified}";
                SimilarImage? image = null;
                if (cache?.Get(file, identity) is { } cached && Parse(file, cached) is { } fromCache) image = fromCache;
                else
                {
                    try
                    {
                        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Loc.Get("Platform.SimilarUnavailable"));
                        var (hash, width, height) = Wic.DifferenceHash(file.Path);
                        image = new(file.Path, file.Size, file.Modified, width, height, hash);
                        cache?.Set(file, identity, $"{hash:x16}:{width}:{height}");
                    }
                    catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException) { lock (gate) skipped(); }
                }
                if (image != null) images.Add(image);
                Interlocked.Increment(ref done);
                Progress(file.Path);
            });
            token.ThrowIfCancellationRequested();
            report(Loc.Get("Similar.Grouping"));
            return Group(images.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList(), threshold, token);
        }
        finally { cache?.Save(); }
    }

    internal const long MaximumFileBytes = 256L * 1024 * 1024;
    internal static bool WithinDecodeLimits(uint width, uint height) =>
        width > 0 && height > 0 && width <= 32768 && height <= 32768 && (ulong)width * height <= 100_000_000;

    private static SimilarImage? Parse(LargeFile file, string cached)
    {
        string[] parts = cached.Split(':');
        if (parts.Length != 3 || !ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong hash)
            || !int.TryParse(parts[1], out int width) || !int.TryParse(parts[2], out int height)
            || width <= 0 || height <= 0 || !WithinDecodeLimits((uint)width, (uint)height)) return null;
        return new(file.Path, file.Size, file.Modified, width, height, hash);
    }

    /// <summary>Joins images within <paramref name="threshold"/> bits into groups and marks the best copy of each: largest resolution, then file size, then newest.</summary>
    public static List<List<SimilarImage>> Group(IReadOnlyList<SimilarImage> images, int threshold, CancellationToken token = default)
    {
        var tree = new BkTree<int>(i => images[i].Hash);
        int[] parent = Enumerable.Range(0, images.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var representatives = new Dictionary<ulong, int>();
        for (int i = 0; i < images.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (representatives.TryGetValue(images[i].Hash, out int same)) parent[i] = same;
            else { representatives.Add(images[i].Hash, i); tree.Add(i); }
        }
        if (threshold > 0)
            foreach (int i in representatives.Values)
            {
                token.ThrowIfCancellationRequested();
                foreach (int j in tree.Search(images[i].Hash, threshold, token))
                    if (j > i) parent[Find(i)] = Find(j);
            }
        token.ThrowIfCancellationRequested();
        var groups = Enumerable.Range(0, images.Count).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => images[i]).OrderByDescending(i => (long)i.Width * i.Height).ThenByDescending(i => i.Size).ThenByDescending(i => i.Modified).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderByDescending(g => g.Skip(1).Sum(i => i.Size)).ToList();
        int number = 0;
        foreach (var group in groups)
        {
            number++;
            var best = group[0];
            foreach (var image in group) { image.Group = number; image.Best = image == best; image.Distance = BkTree<int>.Distance(best.Hash, image.Hash); }
        }
        return groups;
    }

    /// <summary>Pairs within the threshold by checking every pair. Only for tests and tiny sets.</summary>
    public static List<(int, int)> BruteForce(IReadOnlyList<ulong> hashes, int threshold)
    {
        var pairs = new List<(int, int)>();
        for (int i = 0; i < hashes.Count; i++)
            for (int j = i + 1; j < hashes.Count; j++)
                if (BkTree<int>.Distance(hashes[i], hashes[j]) <= threshold) pairs.Add((i, j));
        return pairs;
    }
}

/// <summary>Just enough of Windows Imaging Component to decode an image straight to a tiny grayscale bitmap.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Wic
{
    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid Gray8 = new("6fddc324-4e03-4bfe-b185-3d77768dc908");
    private static readonly Lazy<IWICImagingFactory> Factory = new(() => (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClass)!)!);

    public static (ulong Hash, int Width, int Height) DifferenceHash(string path)
    {
        // shortcut: cancellation waits for native decoding; use a timed worker process to interrupt a stuck codec.
        var factory = Factory.Value;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICBitmapScaler? scaler = null;
        IWICFormatConverter? converter = null;
        try
        {
            decoder = factory.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000, 0);
            frame = decoder.GetFrame(0);
            frame.GetSize(out uint width, out uint height);
            if (!SimilarImageSearch.WithinDecodeLimits(width, height)) throw new IOException("Image dimensions exceed decoding limits.");
            scaler = factory.CreateBitmapScaler();
            scaler.Initialize(frame, 9, 8, 3);
            converter = factory.CreateFormatConverter();
            var format = Gray8;
            converter.Initialize(scaler, ref format, 0, IntPtr.Zero, 0, 0);
            byte[] pixels = new byte[72];
            converter.CopyPixels(IntPtr.Zero, 9, (uint)pixels.Length, pixels);
            ulong hash = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    if (pixels[y * 9 + x] < pixels[y * 9 + x + 1]) hash |= 1UL << (y * 8 + x);
            return (hash, (int)width, (int)height);
        }
        finally
        {
            foreach (object? com in new object?[] { converter, scaler, frame, decoder })
                if (com != null) Marshal.ReleaseComObject(com);
        }
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        IWICBitmapDecoder CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, uint options);
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        IWICFormatConverter CreateFormatConverter();
        IWICBitmapScaler CreateBitmapScaler();
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        uint GetFrameCount();
        IWICBitmapFrameDecode GetFrame(uint index);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
    }

    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
    }

    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapScaler
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize(IWICBitmapFrameDecode source, uint width, uint height, uint mode);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double x, out double y);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint size, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize(IWICBitmapScaler source, ref Guid format, uint dither, IntPtr palette, double alpha, uint paletteType);
    }
}
