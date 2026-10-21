using System.IO;
using System.Text;

namespace Aeris;

/// <summary>
/// SHA-256 results remembered between searches in %LOCALAPPDATA%\Aeris\hashcache. An entry is only used while the
/// file keeps the same size, modification time and file ID, so a changed file is always hashed again.
/// </summary>
public sealed class HashCache
{
    private const string Header = "aeris-hashcache 1";
    private const int MaxEntries = 500_000;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private bool dirty;

    private readonly record struct Entry(long Size, long Modified, string Identity, string Hash);

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "hashcache");
    public static string Folder { get; set; } = DefaultFolder;
    public static string FilePath => Path.Combine(Folder, "sha256.tsv");
    public int Hits { get; private set; }

    public static HashCache Load()
    {
        var cache = new HashCache();
        try
        {
            if (!File.Exists(FilePath)) return cache;
            using var reader = new StreamReader(FilePath, Encoding.UTF8);
            if (reader.ReadLine() != Header) return cache;
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 5 || !long.TryParse(parts[0], out long size) || !long.TryParse(parts[1], out long modified) || parts[3].Length != 64) continue;
                cache.entries[parts[4]] = new(size, modified, parts[2], parts[3]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { cache.entries.Clear(); }
        return cache;
    }

    public string? Get(LargeFile file, string identity)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(file.Path, out var entry)) return null;
            if (entry.Size != file.Size || entry.Modified != file.Modified || entry.Identity != identity) { entries.Remove(file.Path); dirty = true; return null; }
            used.Add(file.Path);
            Hits++;
            return entry.Hash;
        }
    }

    public void Set(LargeFile file, string identity, string hash)
    {
        lock (gate)
        {
            entries[file.Path] = new(file.Size, file.Modified, identity, hash);
            used.Add(file.Path);
            dirty = true;
        }
    }

    /// <summary>Writes the cache atomically. Entries used in this run are kept first when the cache is full.</summary>
    public void Save()
    {
        lock (gate)
        {
            if (!dirty) return;
            Directory.CreateDirectory(Folder);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine(Header);
                    foreach (var (path, entry) in entries.OrderByDescending(p => used.Contains(p.Key)).Take(MaxEntries))
                        writer.WriteLine($"{entry.Size}\t{entry.Modified}\t{entry.Identity}\t{entry.Hash}\t{path}");
                }
                File.Move(temporary, FilePath, true);
                dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static long Size()
    {
        try { return File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
