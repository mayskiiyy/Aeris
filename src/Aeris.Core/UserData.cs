using System.IO;
using System.Text.Json;

namespace Aeris;

public static class HistoryAction
{
    public const string Cleanup = "Cleanup";
    public const string Recycle = "Recycle";
    public const string Restore = "Restore";
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
    public string Id { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public string Mode { get; set; } = "";
    public bool Scheduled { get; set; }
    public bool Restored { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int Skipped { get; set; }
    public string Result { get; set; } = HistoryResult.Running;
    public string DisplaySize => Storage.Format(Bytes);
    public string DisplayTime => Time.ToString("g", Loc.Culture);
    public string DisplayAction => Scheduled ? Loc.Format("History.Scheduled", Loc.Get($"History.Action.{Action}")) : Loc.Get($"History.Action.{Action}");
    public string DisplayResult => Loc.Get($"History.Result.{Result}");
    public string DisplayMode => Mode.Length == 0 ? "" : Loc.Get($"Mode.{Mode}");
    public bool CanRestore => Mode == nameof(CleanupMode.Quarantine) && !Restored && Id.Length > 0 && Directory.Exists(Quarantine.FolderFor(Id));
    public bool InRecycleBin => Mode == nameof(CleanupMode.Recycle) || (Mode.Length == 0 && Action == HistoryAction.Recycle);
    public string Undo => Restored ? Loc.Get("History.Undo.Restored")
        : CanRestore ? Loc.Get("History.Undo.Quarantine")
        : InRecycleBin ? Loc.Get("History.Undo.RecycleBin")
        : Mode == nameof(CleanupMode.Quarantine) ? Loc.Get("History.Undo.Expired")
        : Action == HistoryAction.Cleanup ? Loc.Get("History.Undo.Permanent") : "";

    public void Normalize()
    {
        if (Result == HistoryResult.Running) Result = HistoryResult.Interrupted;
    }
}

public class CleanupProfile
{
    public string Name { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public int OlderThanDays { get; set; } = 2;
    public CleanupMode Mode { get; set; } = CleanupMode.Permanent;
}

public class UserData
{
    public const int CurrentSchema = 2;
    private string sourcePath = FilePath;
    public int SchemaVersion { get; set; }
    public string Folder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public int RetentionDays { get; set; } = 2;
    public int MinimumMb { get; set; } = 100;
    public int FileType { get; set; }
    public List<string> Exclusions { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public CleanupMode CleanupMode { get; set; } = CleanupMode.Permanent;
    public int QuarantineDays { get; set; } = 7;
    public int QuarantineLimitGb { get; set; } = 5;
    public int LargeOperationFiles { get; set; } = 5000;
    public int LargeOperationGb { get; set; } = 5;
    public string KeepRule { get; set; } = "Oldest";
    public List<string> PriorityFolders { get; set; } = [];
    public List<string> ProtectedFolders { get; set; } = [];
    public bool SequentialHashing { get; set; }
    public List<CleanupProfile> Profiles { get; set; } = [];
    public ScheduleSettings Schedule { get; set; } = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "settings.json");

    public static UserData Load(string? path = null)
    {
        path ??= FilePath;
        UserData? data = null;
        foreach (string source in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(source)) data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(source), Options); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (data != null) break;
        }
        data ??= new();
        data.sourcePath = path;
        if (!Directory.Exists(data.Folder)) data.Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        data.RetentionDays = Math.Clamp(data.RetentionDays, 2, 36500);
        data.MinimumMb = Math.Clamp(data.MinimumMb, 1, 1000000000);
        data.FileType = Math.Clamp(data.FileType, 0, 5);
        data.Exclusions = Folders(data.Exclusions);
        data.PriorityFolders = Folders(data.PriorityFolders);
        data.ProtectedFolders = Folders(data.ProtectedFolders);
        if (!Enum.IsDefined(data.CleanupMode) || data.CleanupMode == CleanupMode.Recycle) data.CleanupMode = CleanupMode.Permanent;
        data.QuarantineDays = Math.Clamp(data.QuarantineDays, 1, 30);
        data.QuarantineLimitGb = Math.Clamp(data.QuarantineLimitGb, 1, 1000);
        data.LargeOperationFiles = Math.Clamp(data.LargeOperationFiles, 10, 10000000);
        data.LargeOperationGb = Math.Clamp(data.LargeOperationGb, 1, 100000);
        if (!DuplicateRules.Names.Contains(data.KeepRule)) data.KeepRule = DuplicateRules.Names[0];
        data.Profiles = (data.Profiles ?? []).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        data.Schedule ??= new();
        data.Schedule.Hour = Math.Clamp(data.Schedule.Hour, 0, 23);
        data.Schedule.OlderThanDays = Math.Clamp(data.Schedule.OlderThanDays, 2, 365);
        if (!Enum.IsDefined(data.Schedule.Frequency)) data.Schedule.Frequency = ScheduleFrequency.Weekly;
        if (data.Schedule.Mode is not (CleanupMode.Permanent or CleanupMode.Quarantine)) data.Schedule.Mode = CleanupMode.Quarantine;
        foreach (var profile in data.Profiles)
        {
            profile.Categories = (profile.Categories ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.OlderThanDays = Math.Clamp(profile.OlderThanDays, 2, 36500);
            if (profile.Mode is not (CleanupMode.Permanent or CleanupMode.Quarantine)) profile.Mode = CleanupMode.Permanent;
        }
        data.History = (data.History ?? []).Where(h => h != null).ToList();
        foreach (var entry in data.History) entry.Normalize();
        if (data.History.Count > MaxHistory) data.History = data.History.OrderByDescending(h => h.Time).Take(MaxHistory).OrderBy(h => h.Time).ToList();
        data.Migrate();
        return data;
    }

    public const int MaxHistory = 500;

    /// <summary>Brings an older settings file up to <see cref="CurrentSchema"/> without dropping anything it had.</summary>
    private void Migrate()
    {
        if (SchemaVersion < 2)
        {
            // 1.0 had no schema field, no operation ids and no journals. Old entries keep their details and stay read-only.
            foreach (var entry in History) entry.Mode = entry.Action == HistoryAction.Recycle ? nameof(CleanupMode.Recycle) : nameof(CleanupMode.Permanent);
        }
        SchemaVersion = CurrentSchema;
    }

    private static List<string> Folders(IEnumerable<string?>? candidates)
    {
        var folders = new List<string>();
        foreach (string? candidate in candidates ?? [])
        {
            try { if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate)) folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate))); }
            catch (ArgumentException) { }
        }
        return folders.Distinct(FileSystemPaths.Comparer).ToList();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public void Save(string? path = null)
    {
        path ??= sourcePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
