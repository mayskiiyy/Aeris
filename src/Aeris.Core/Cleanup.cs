using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Aeris;

public enum CleanupMode { Permanent, Quarantine, Recycle }

public static class Outcome
{
    public const string Deleted = "deleted";
    public const string Quarantined = "quarantined";
    public const string Recycled = "recycled";
    public const string Changed = "changed";
    public const string InUse = "in-use";
    public const string Excluded = "excluded";
    public const string NotAllowed = "not-allowed";
    public const string OtherVolume = "other-volume";
    public const string Failed = "failed";
    public static bool Done(string result) => result is Deleted or Quarantined or Recycled;
}

/// <summary>One file a cleanup intends to touch, captured with the dates it had when the plan was made.</summary>
public class PlanItem(string path, long size, long modified, long created, string category, string root = "", bool allowPrograms = false) : INotifyPropertyChanged
{
    private bool included = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Path { get; } = path;
    public long Size { get; } = size;
    public long Modified { get; } = modified;
    public long Created { get; } = created;
    public string Category { get; } = category;
    public string Root { get; } = root;
    public bool AllowPrograms { get; } = allowPrograms;
    public string? Keeper { get; init; }
    public string? Hash { get; init; }
    public bool IsFolder { get; init; }
    public bool Included { get => included; set { if (included == value) return; included = value; PropertyChanged?.Invoke(this, new(nameof(Included))); } }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DisplaySize => IsFolder ? "—" : Storage.Format(Size);
    public string DisplayModified => Modified == 0 ? "" : new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("g", Loc.Culture);
    public static PlanItem From(LargeFile file, string category) => new(file.Path, file.Size, file.Modified, file.Created, category);
}

public class PlanCategory(string id, string name, List<PlanItem> items) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public List<PlanItem> Items { get; } = items;
    public string Warning { get; set; } = "";
    public bool HasWarning => Warning.Length > 0;
    public int IncludedCount => Items.Count(i => i.Included);
    public long IncludedBytes => Items.Where(i => i.Included).Sum(i => i.Size);
    public bool? Included
    {
        get => Items.All(i => i.Included) ? true : Items.Any(i => i.Included) ? null : false;
        set { foreach (var item in Items) item.Included = value != false; Refresh(); }
    }
    public string Summary => Loc.Format("Preview.CategorySummary", IncludedCount, Items.Count, Storage.Format(IncludedBytes));
    public void Refresh() => PropertyChanged?.Invoke(this, new(string.Empty));
}

/// <summary>What an operation will do. Building a plan never changes the disk.</summary>
public class CleanupPlan(string action, CleanupMode mode, List<PlanCategory> categories)
{
    public string Action { get; } = action;
    public CleanupMode Mode { get; set; } = mode;
    public List<PlanCategory> Categories { get; } = categories;
    public DateTime Cutoff { get; init; } = DateTime.MinValue;
    public string Details { get; init; } = "";
    public IEnumerable<PlanItem> Included => Categories.SelectMany(c => c.Items).Where(i => i.Included);
    public int Count => Included.Count();
    public long Bytes => Included.Sum(i => i.Size);
    public bool IsLarge(int files, long bytes) => Count > files || Bytes > bytes;
}

public class CleanupResult
{
    public string Id { get; init; } = "";
    public int Done { get; set; }
    public long Bytes { get; set; }
    public int Changed { get; set; }
    public int InUse { get; set; }
    public int Failed { get; set; }
    public int OtherVolume { get; set; }
    public List<string> Errors { get; } = [];
    public int Skipped => Changed + InUse + Failed + OtherVolume;
    public string Result => Skipped == 0 ? HistoryResult.Completed : HistoryResult.Partial;

    public void Count(PlanItem item, string outcome, string? error = null)
    {
        switch (outcome)
        {
            case Outcome.Deleted or Outcome.Quarantined or Outcome.Recycled: Done++; Bytes += item.Size; break;
            case Outcome.InUse: InUse++; break;
            case Outcome.Changed or Outcome.Excluded: Changed++; break;
            case Outcome.OtherVolume: OtherVolume++; break;
            default: Failed++; break;
        }
        if (error != null && Errors.Count < 200) Errors.Add($"{item.Path}: {error}");
    }

    public string Summary => Loc.Format("Result.Summary", Done, Storage.Format(Bytes), InUse, Changed, Failed + OtherVolume);
}

public static class Cleanup
{
    public static string NewId() => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";

    /// <summary>Running processes from the categories' close-apps hints, by display name.</summary>
    public static List<string> RunningApps(IEnumerable<CacheGroup> groups)
    {
        var running = new List<string>();
        foreach (var group in groups)
            foreach (string name in group.Rule.CloseAppsHint)
            {
                Process[] processes = [];
                try { processes = Process.GetProcessesByName(name); }
                catch (InvalidOperationException) { }
                bool found = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (found) { running.Add(group.Name); break; }
            }
        return running;
    }

    public static CleanupPlan CachePlan(IEnumerable<CacheGroup> groups, DateTime cutoff, CleanupMode mode)
    {
        var selected = groups.Where(g => g.Selected && g.Items.Count > 0).ToList();
        var running = RunningApps(selected).ToHashSet();
        var categories = selected.Select(g => new PlanCategory(g.Rule.Id, g.Name, g.Items.Select(i => { i.Included = true; return i; }).ToList())
        { Warning = running.Contains(g.Name) ? Loc.Format("Preview.CloseApp", g.Name) : "" }).ToList();
        return new(HistoryAction.Cleanup, mode, categories) { Cutoff = cutoff, Details = string.Join("\n", selected.Select(g => g.Name)) };
    }

    /// <summary>
    /// Deletes or quarantines the plan's included cache files. Every file is checked again first: it must still lie
    /// inside a folder of a current rule, have no links on its path, be unchanged since the scan and be older than the cutoff.
    /// </summary>
    public static CleanupResult ExecuteCache(CleanupPlan plan, IReadOnlyCollection<string> exclusions, Journal journal, CancellationToken token, IProgress<int>? progress = null, IEnumerable<CacheGroup>? currentGroups = null)
    {
        if (plan.Cutoff.Kind != DateTimeKind.Utc || plan.Cutoff > DateTime.UtcNow.AddHours(-48))
            throw new ArgumentOutOfRangeException(nameof(plan), Loc.Get("Error.MinRetention"));
        if (plan.Mode == CleanupMode.Recycle) throw new ArgumentException(nameof(plan.Mode));
        var allowed = (currentGroups ?? Cleaners.Groups()).SelectMany(g => g.Targets).Select(t => t.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new CleanupResult { Id = journal.Id };
        string quarantine = Quarantine.FolderFor(journal.Id);
        int index = 0;
        foreach (var item in plan.Included)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(++index);
            var (outcome, stored, error) = CleanOne(item, plan, exclusions, allowed, quarantine);
            result.Count(item, outcome, error);
            journal.Write(item, outcome, stored, error);
        }
        return result;
    }

    private static (string Outcome, string? Stored, string? Error) CleanOne(PlanItem item, CleanupPlan plan, IReadOnlyCollection<string> exclusions, HashSet<string> allowed, string quarantine)
    {
        try
        {
            if (Storage.IsExcluded(item.Path, exclusions)) return (Outcome.Excluded, null, null);
            if (!allowed.Contains(item.Root) || !PathGuard.IsAllowedTarget(item.Path, item.Root, item.AllowPrograms)) return (Outcome.NotAllowed, null, Loc.Get("Error.NotAllowed"));
            var file = new FileInfo(item.Path);
            if (!file.Exists || file.Length != item.Size || file.LastWriteTimeUtc.Ticks != item.Modified || file.CreationTimeUtc.Ticks != item.Created || !Storage.Eligible(file, plan.Cutoff))
                return (Outcome.Changed, null, null);
            if (plan.Mode == CleanupMode.Quarantine)
            {
                if (!Quarantine.SameVolume(item.Path)) return (Outcome.OtherVolume, null, Loc.Format("Error.QuarantineVolume", Path.GetPathRoot(item.Path) ?? ""));
                string target = Quarantine.PathFor(quarantine, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(item.Path, target);
                return (Outcome.Quarantined, target, null);
            }
            file.Delete();
            return (Outcome.Deleted, null, null);
        }
        catch (IOException e) when (IsLocked(e)) { return (Outcome.InUse, null, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (Outcome.Failed, null, e.Message); }
    }

    public static bool IsLocked(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;
}
