using System.IO;
using System.Text;
using System.Text.Json;

namespace Aeris;

public record JournalHeader(int Schema, string Id, DateTime Time, string Action, string Mode, bool Scheduled, string Details);

public record JournalItem(string Path, long Size, long Created, long Modified, string Result, string? Stored = null, string? Error = null, string? Category = null);

/// <summary>
/// The per-operation log: a JSON Lines file in %LOCALAPPDATA%\Aeris\history with a header line and one line per file,
/// written as the operation runs so an interrupted operation still shows what was done.
/// </summary>
public sealed class Journal : IDisposable
{
    public const int Schema = 1;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly StreamWriter writer;
    private readonly object gate = new();

    public string Id { get; }
    public string File { get; }

    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "history");
    public static string Folder { get; set; } = DefaultFolder;

    private Journal(string id, string file, StreamWriter writer) { Id = id; File = file; this.writer = writer; }

    public static Journal Begin(string id, string action, CleanupMode mode, string details, bool scheduled = false)
    {
        Directory.CreateDirectory(Folder);
        string file = PathOf(id);
        var writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = false };
        var journal = new Journal(id, file, writer);
        journal.Line(new JournalHeader(Schema, id, DateTime.Now, action, mode.ToString(), scheduled, details));
        writer.Flush();
        return journal;
    }

    public static string PathOf(string id) => System.IO.Path.Combine(Folder, id + ".jsonl");

    public void Write(PlanItem item, string result, string? stored = null, string? error = null) =>
        Line(new JournalItem(item.Path, item.Size, item.Created, item.Modified, result, stored, error, item.Category));

    public void Write(JournalItem item) => Line(item);

    private void Line<T>(T value)
    {
        lock (gate) { writer.WriteLine(JsonSerializer.Serialize(value, Options)); }
    }

    public void Flush() { lock (gate) writer.Flush(); }

    public void Dispose() { lock (gate) writer.Dispose(); }

    public static (JournalHeader? Header, List<JournalItem> Items) Read(string id)
    {
        var items = new List<JournalItem>();
        JournalHeader? header = null;
        string file = PathOf(id);
        if (!System.IO.File.Exists(file)) return (null, items);
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        bool first = true;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (first) header = JsonSerializer.Deserialize<JournalHeader>(line, Options);
                else if (JsonSerializer.Deserialize<JournalItem>(line, Options) is { } item) items.Add(item);
            }
            catch (JsonException) { }
            first = false;
        }
        return (header, items);
    }

    /// <summary>Keeps the newest <paramref name="keep"/> logs and at most <paramref name="maxBytes"/> in total. Logs still referenced by quarantine are kept.</summary>
    public static void Rotate(int keep = 200, long maxBytes = 64L * 1024 * 1024, ISet<string>? pinned = null)
    {
        if (!Directory.Exists(Folder)) return;
        long total = 0;
        int index = 0;
        foreach (var file in new DirectoryInfo(Folder).GetFiles("*.jsonl").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            total += file.Length;
            string id = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            if ((++index > keep || total > maxBytes) && pinned?.Contains(id) != true)
                try { file.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
