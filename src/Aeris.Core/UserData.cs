using System.IO;
using System.Text.Json;

namespace Aeris;

public static class HistoryAction
{
    public const string Cleanup = "Cleanup";
    public const string Recycle = "Recycle";
}

public static class HistoryResult
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Partial = "Partial";
    public const string Interrupted = "Interrupted";
}

public class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int Skipped { get; set; }
    public string Result { get; set; } = HistoryResult.Running;
    public string DisplaySize => Storage.Format(Bytes);
    public string DisplayTime => Time.ToString("g", Loc.Culture);
    public string DisplayAction => Loc.Get($"History.Action.{Action}");
    public string DisplayResult => Loc.Get($"History.Result.{Result}");

    public void Normalize()
    {
        if (Result == HistoryResult.Running) Result = HistoryResult.Interrupted;
    }
}

public class UserData
{
    private string sourcePath = FilePath;
    public string Folder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public int RetentionDays { get; set; } = 2;
    public int MinimumMb { get; set; } = 100;
    public int FileType { get; set; }
    public List<string> Exclusions { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "settings.json");

    public static UserData Load(string? path = null)
    {
        path ??= FilePath;
        UserData? data = null;
        foreach (string source in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(source)) data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(source)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (data != null) break;
        }
        data ??= new();
        data.sourcePath = path;
        if (!Directory.Exists(data.Folder)) data.Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        data.RetentionDays = Math.Clamp(data.RetentionDays, 2, 36500);
        data.MinimumMb = Math.Clamp(data.MinimumMb, 1, 1000000000);
        data.FileType = Math.Clamp(data.FileType, 0, 5);
        var exclusions = new List<string>();
        foreach (string? candidate in data.Exclusions ?? [])
        {
            try { if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate)) exclusions.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate))); }
            catch (ArgumentException) { }
        }
        data.Exclusions = exclusions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.History = (data.History ?? []).Where(h => h != null).ToList();
        foreach (var entry in data.History) entry.Normalize();
        return data;
    }

    public void Save(string? path = null)
    {
        path ??= sourcePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
