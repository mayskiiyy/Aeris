using System.IO;
using System.Text;
using System.Text.Json;

namespace Aeris;

public record JournalHeader(int Schema, string Id, DateTime Time, string Action, string Mode, bool Scheduled, string Details);

public record JournalSummary(string Result, int Done, long Bytes, int Skipped);

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

    /// <summary>The last line of a finished operation. A log without it was interrupted.</summary>
    public void Finish(CleanupResult result) => Line(new { Summary = new JournalSummary(result.Result, result.Done, result.Bytes, result.Skipped) });

    private void Line<T>(T value)
    {
        lock (gate) { writer.WriteLine(JsonSerializer.Serialize(value, Options)); }
    }

    public void Flush() { lock (gate) writer.Flush(); }

    public void Dispose() { lock (gate) writer.Dispose(); }

    public static (JournalHeader? Header, List<JournalItem> Items) Read(string id)
    {
        var (header, items, _) = ReadAll(id);
        return (header, items);
    }

    public static (JournalHeader? Header, List<JournalItem> Items, JournalSummary? Summary) ReadAll(string id, bool headerOnly = false)
    {
        var items = new List<JournalItem>();
        JournalHeader? header = null;
        JournalSummary? summary = null;
        string file = PathOf(id);
        if (!System.IO.File.Exists(file)) return (null, items, null);
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        bool first = true;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (first) { header = JsonSerializer.Deserialize<JournalHeader>(line, Options); if (headerOnly) break; }
                else if (line.StartsWith("{\"summary\"", StringComparison.Ordinal)) summary = JsonSerializer.Deserialize<SummaryLine>(line, Options)?.Summary;
                else if (JsonSerializer.Deserialize<JournalItem>(line, Options) is { } item) items.Add(item);
            }
            catch (JsonException) { }
            first = false;
        }
        return (header, items, summary);
    }

    private record SummaryLine(JournalSummary? Summary);

    public static IEnumerable<string> Ids() =>
        Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.jsonl").Select(System.IO.Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal) : [];

    /// <summary>
    /// History entries for logged operations the settings file doesn't know yet, such as ones run by aeris-cli
    /// or the scheduler while the app was open.
    /// </summary>
    public static List<HistoryEntry> Missing(IEnumerable<HistoryEntry> known)
    {
        var ids = known.Select(h => h.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var entries = new List<HistoryEntry>();
        foreach (string id in Ids().Where(id => !ids.Contains(id)))
        {
            var (header, items, summary) = ReadAll(id);
            if (header == null || header.Action == HistoryAction.Restore) continue;
            var done = items.Where(i => Outcome.Done(i.Result)).ToList();
            entries.Add(new HistoryEntry
            {
                Id = id, Time = header.Time, Action = header.Action, Mode = header.Mode, Scheduled = header.Scheduled, Details = header.Details,
                Files = summary?.Done ?? done.Count, Bytes = summary?.Bytes ?? done.Sum(i => i.Size),
                Skipped = summary?.Skipped ?? items.Count - done.Count, Result = summary?.Result ?? HistoryResult.Interrupted,
            });
        }
        return entries;
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
