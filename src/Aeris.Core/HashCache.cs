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
    private readonly Dictionary<string, Entry> entries = new(FileSystemPaths.Comparer);
    private readonly HashSet<string> used = new(FileSystemPaths.Comparer);
    private readonly object gate = new();
    private bool dirty;
    private readonly string file;

    private HashCache(string file) => this.file = file;

    private readonly record struct Entry(long Size, long Modified, string Identity, string Hash);

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "hashcache");
    public static string Folder { get; set; } = DefaultFolder;
    public static string PathFor(string name) => Path.Combine(Folder, name + ".tsv");
    public int Hits { get; private set; }

    /// <summary>Loads one kind of cached hash: "sha256" for duplicates, "dhash" for similar images.</summary>
    public static HashCache Load(string name = "sha256")
    {
        var cache = new HashCache(PathFor(name));
        try
        {
            if (!File.Exists(cache.file)) return cache;
            using var reader = new StreamReader(cache.file, Encoding.UTF8);
            if (reader.ReadLine() != Header) return cache;
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 5 || !long.TryParse(parts[0], out long size) || !long.TryParse(parts[1], out long modified) || parts[3].Length == 0) continue;
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
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine(Header);
                    foreach (var (path, entry) in entries.OrderByDescending(p => used.Contains(p.Key)).Take(MaxEntries))
                        writer.WriteLine($"{entry.Size}\t{entry.Modified}\t{entry.Identity}\t{entry.Hash}\t{path}");
                }
                File.Move(temporary, file, true);
                dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static long Size()
    {
        try { return Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles("*.tsv").Sum(f => f.Length) : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static void Clear()
    {
        if (!Directory.Exists(Folder)) return;
        foreach (string path in Directory.GetFiles(Folder, "*.tsv")) File.Delete(path);
    }
}
