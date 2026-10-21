using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aeris.Cli;

public static class ExitCode
{
    public const int Success = 0;
    public const int Partial = 1;
    public const int Usage = 2;
    public const int Cancelled = 3;
}

/// <summary>Where the CLI reads and writes. Tests swap in string readers and writers.</summary>
public record CliIO(TextWriter Out, TextWriter Error, TextReader In, bool Interactive, int ProgressWidth = 0);

public class Cli(CliIO io, UserData data, CancellationToken token)
{
    public const int JsonSchema = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string[] Types = ["", "video", "archive", "installer", "document", "image"];
    private bool quiet;
    private DateTime lastProgress;
    private bool progressShown;

    public static int Run(string[] args, CliIO io, UserData data, CancellationToken token)
    {
        try
        {
            var line = CommandLine.Parse(args);
            return new Cli(io, data, token).Execute(line);
        }
        catch (UsageException e)
        {
            io.Error.WriteLine(e.Message);
            io.Error.WriteLine(Loc.Get("Cli.SeeHelp"));
            return ExitCode.Usage;
        }
        catch (OperationCanceledException)
        {
            io.Error.WriteLine(Loc.Get("Cli.Cancelled"));
            return ExitCode.Cancelled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            io.Error.WriteLine(e.Message);
            return ExitCode.Partial;
        }
    }

    private int Execute(CommandLine line)
    {
        quiet = line.Has("--quiet") || line.Has("--json") || io.ProgressWidth <= 0;
        try { return Dispatch(line); }
        finally { ClearProgress(); }
    }

    private int Dispatch(CommandLine line)
    {
        if (!OperatingSystem.IsWindows() && line.Has("--similar-images"))
            throw new UsageException(Loc.Get("Platform.SimilarUnavailable"));
        return line.Command switch
        {
            "help" => Help(),
            "version" => Version(),
            "scan" => Scan(line),
            "clean" => Clean(line),
            "duplicates" => FindDuplicates(line),
            "empty-folders" => EmptyFolderSearch(line),
            "history" => History(line),
            "restore" => Restore(line),
            "categories" => Categories(line),
            _ => throw new UsageException(Loc.Format("Cli.UnknownCommand", line.Command)),
        };
    }

    private int Help()
    {
        io.Out.WriteLine(Loc.Get("Cli.Help"));
        return ExitCode.Success;
    }

    private int Version()
    {
        io.Out.WriteLine($"aeris-cli {typeof(Cli).Assembly.GetName().Version?.ToString(3)}");
        return ExitCode.Success;
    }

    private void WriteJson(string command, object payload)
    {
        var document = new Dictionary<string, object?> { ["schema"] = JsonSchema, ["command"] = command };
        foreach (var property in payload.GetType().GetProperties()) document[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(payload);
        io.Out.WriteLine(JsonSerializer.Serialize(document, Json));
    }

    /// <summary>One status line that rewrites itself on an interactive console. Nothing when output is redirected or --quiet.</summary>
    private void Progress(string text)
    {
        if (quiet || DateTime.UtcNow - lastProgress < TimeSpan.FromMilliseconds(250)) return;
        lastProgress = DateTime.UtcNow;
        int width = io.ProgressWidth - 1;
        if (text.Length > width) text = text[..(width / 2 - 1)] + "…" + text[^(width - width / 2)..];
        io.Error.Write("\r" + text.PadRight(width));
        progressShown = true;
    }

    private void ClearProgress()
    {
        if (!progressShown) return;
        io.Error.Write("\r" + new string(' ', io.ProgressWidth - 1) + "\r");
        progressShown = false;
    }

    private static string Folder(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new UsageException(Loc.Format("Cli.BadFolder", path)); }
        if (!Directory.Exists(full) || !Storage.SafePath(full)) throw new UsageException(Loc.Format("Cli.BadFolder", path));
        return full;
    }

    private bool Confirm(CommandLine line, string question)
    {
        if (line.Has("--yes")) return true;
        if (!io.Interactive) { io.Error.WriteLine(Loc.Get("Cli.NeedYes")); return false; }
        io.Error.Write(question + " " + Loc.Get("Cli.YesNo") + " ");
        string answer = (io.In.ReadLine() ?? "").Trim();
        return Loc.Get("Cli.YesAnswers").Split('|').Contains(answer, StringComparer.OrdinalIgnoreCase);
    }

    private int Scan(CommandLine line)
    {
        if (line.Positional.Count != 1) throw new UsageException(Loc.Get("Cli.ScanNeedsFolder"));
        string root = Folder(line.Positional[0]);
        int top = line.Int("--top", 200, 1, 100000);
        long minimum = line.Value("--min-size") is { } size ? CommandLine.ParseSize(size) : 0;
        int type = 0;
        if (line.Value("--type") is { } name && (type = Array.IndexOf(Types, name.ToLowerInvariant())) <= 0) throw new UsageException(Loc.Format("Cli.BadType", name, string.Join(", ", Types.Skip(1))));
        long total = 0, count = 0;
        int skipped = 0;
        var largest = new PriorityQueue<LargeFile, long>();
        foreach (var file in Storage.Walk(root, token, () => skipped++, data.Exclusions))
        {
            long length;
            try { length = file.Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; continue; }
            total += length; count++;
            if (length >= minimum && Analysis.Matches(file.FullName, type)) { largest.Enqueue(Analysis.Snapshot(file), length); if (largest.Count > top) largest.Dequeue(); }
            Progress(Loc.Format("Status.FilesChecked", count, Storage.Format(total), file.DirectoryName ?? ""));
        }
        ClearProgress();
        var files = largest.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Size).ToList();
        if (line.Value("--csv") is { } csv) Storage.ExportCsv(files, csv);
        if (line.Has("--json"))
            WriteJson("scan", new { Root = root, Files = count, Bytes = total, Skipped = skipped, Largest = files.Select(f => new { f.Path, f.Size, Modified = new DateTime(f.Modified, DateTimeKind.Utc) }) });
        else
        {
            foreach (var file in files) io.Out.WriteLine($"{file.DisplaySize,12}  {file.Path}");
            io.Out.WriteLine(Loc.Format("Status.ScanDone", count, Storage.Format(total), skipped));
        }
        return skipped > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    private int Clean(CommandLine line)
    {
        if (line.Positional.Count > 0) throw new UsageException(Loc.Format("Cli.UnexpectedArgument", line.Positional[0]));
        if (line.Has("--categories") && line.Has("--profile")) throw new UsageException(Loc.Get("Cli.CategoriesOrProfile"));
        var errors = new List<RuleError>();
        var groups = Cleaners.Groups(errors);
        foreach (var error in errors) io.Error.WriteLine(Loc.Format("Cli.RuleSkipped", error.Source, error.Message));
        CleanupProfile? profile = null;
        if (line.Value("--profile") is { } profileName)
            profile = data.Profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException(Loc.Format("Cli.UnknownProfile", profileName));
        List<CacheGroup> selected;
        // A profile without categories means the default set: every Safe category that is on by default.
        var ids = line.Value("--categories")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? (profile is { Categories.Count: > 0 } ? profile.Categories.ToArray() : null);
        if (ids != null)
        {
            var rules = Cleaners.Builtin().Concat(groups.Select(g => g.Rule)).ToList();
            var known = rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ids.FirstOrDefault(id => !known.Contains(id)) is { } unknown) throw new UsageException(Loc.Format("Cli.UnknownCategory", unknown));
            // Validate requested rules even when their cache folders don't exist on this machine.
            if (line.Has("--scheduled") && rules.FirstOrDefault(r => ids.Contains(r.Id, StringComparer.OrdinalIgnoreCase) && r.Risk != Risk.Safe) is { } riskyRule)
                throw new UsageException(Loc.Format("Cli.ScheduledSafeOnly", riskyRule.Id));
            selected = groups.Where(g => ids.Contains(g.Rule.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        else selected = groups.Where(g => g.Rule.Enabled).ToList();
        if (line.Has("--scheduled") && selected.FirstOrDefault(g => g.Rule.Risk != Risk.Safe) is { } risky)
            throw new UsageException(Loc.Format("Cli.ScheduledSafeOnly", risky.Rule.Id));

        var age = line.Value("--older-than") is { } older ? CommandLine.ParseAge(older) : TimeSpan.FromDays(profile?.OlderThanDays ?? data.RetentionDays);
        if (age < TimeSpan.FromHours(48)) throw new UsageException(Loc.Get("Error.MinRetention"));
        var mode = line.Value("--mode") switch
        {
            null => profile?.Mode ?? data.CleanupMode,
            "permanent" => CleanupMode.Permanent,
            "quarantine" => CleanupMode.Quarantine,
            var other => throw new UsageException(Loc.Format("Cli.BadMode", other)),
        };
        var cutoff = DateTime.UtcNow - age;
        foreach (var group in selected)
        {
            Progress(Loc.Format("Cli.Checking", group.Name));
            group.Scan(cutoff, data.Exclusions, token, (root, _) => Progress(Loc.Format("Cli.Checking", $"{group.Name} · {root}")));
            group.Selected = true;
        }
        ClearProgress();
        var plan = Cleanup.CachePlan(selected, cutoff, mode);
        var warnings = plan.Categories.Where(c => c.HasWarning).Select(c => c.Warning).ToList();
        if (line.Has("--json") && line.Has("--dry-run"))
        {
            WriteJson("clean", new { DryRun = true, Mode = mode, Cutoff = cutoff, Warnings = warnings, Plan = PlanJson(plan) });
            return ExitCode.Success;
        }
        if (!quiet)
        {
            foreach (var category in plan.Categories) io.Out.WriteLine($"{Storage.Format(category.IncludedBytes),12}  {category.Name} ({category.IncludedCount:N0})");
            foreach (string warning in warnings) io.Error.WriteLine(warning);
            io.Out.WriteLine(Loc.Format("Cli.PlanTotal", plan.Count, Storage.Format(plan.Bytes), Loc.Get($"Mode.{mode}")));
        }
        if (line.Has("--dry-run") || plan.Count == 0) return ExitCode.Success;
        if (!Confirm(line, Loc.Get("Cli.ConfirmClean"))) return io.Interactive && !line.Has("--yes") ? ExitCode.Success : ExitCode.Usage;

        string id = Cleanup.NewId();
        string details = string.Join("\n", plan.Categories.Select(c => c.Name)) + "\n" + Loc.Format("History.Before", cutoff.ToLocalTime().ToString("g", Loc.Culture));
        CleanupResult result;
        using (var journal = Journal.Begin(id, HistoryAction.Cleanup, mode, details, line.Has("--scheduled")))
        {
            result = Cleanup.ExecuteCache(plan, data.Exclusions, journal, token, null, groups);
            journal.Finish(result);
        }
        if (mode == CleanupMode.Quarantine) Quarantine.Purge(data.QuarantineDays, data.QuarantineLimitGb * 1024L * 1024 * 1024);
        if (line.Has("--json")) WriteJson("clean", new { DryRun = false, Operation = id, Mode = mode, result.Done, result.Bytes, result.InUse, result.Changed, result.Failed, result.OtherVolume, result.Errors });
        else if (!line.Has("--quiet")) { io.Out.WriteLine(result.Summary); io.Out.WriteLine(Loc.Format("Cli.Operation", id)); }
        foreach (string error in result.Errors.Take(20)) io.Error.WriteLine(error);
        return result.Skipped > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    private static object PlanJson(CleanupPlan plan) => plan.Categories.Select(c => new
    {
        c.Id, c.Name, Files = c.IncludedCount, Bytes = c.IncludedBytes,
        Items = c.Items.Where(i => i.Included).Select(i => new { i.Path, i.Size, Modified = new DateTime(i.Modified, DateTimeKind.Utc) }),
    });

    private List<string> Roots(CommandLine line)
    {
        if (line.Positional.Count == 0) throw new UsageException(Loc.Get("Cli.NeedsFolder"));
        return line.Positional.Select(Folder).Distinct(FileSystemPaths.Comparer).ToList();
    }

    private int FindDuplicates(CommandLine line)
    {
        var roots = Roots(line);
        if (line.Has("--similar-images")) return SimilarImages(line, roots);
        if (line.Has("--threshold")) throw new UsageException(Loc.Get("Cli.ThresholdNeedsSimilar"));
        long minimum = line.Value("--min-size") is { } size ? Math.Max(1, CommandLine.ParseSize(size)) : 1;
        int skipped = 0;
        var found = Analysis.FindDuplicates(roots, data.Exclusions, token, Progress, () => skipped++, new DuplicateOptions(minimum, data.SequentialHashing, HashCache.Load()));
        ClearProgress();
        var groups = found.GroupBy(f => f.Group).ToList();
        long extra = groups.Sum(g => g.First().File.Size * (g.Count() - 1));
        CleanupResult? result = null;
        if (line.Has("--delete-to-recycle"))
        {
            if (!line.Has("--yes")) throw new UsageException(Loc.Get("Cli.DeleteNeedsYes"));
            var copies = DuplicateRules.Extra(found, data.KeepRule, data.PriorityFolders, data.ProtectedFolders);
            if (copies.Count > 0) result = RecycleNow(Duplicates.Plan(copies, found));
        }
        if (line.Has("--json"))
            WriteJson("duplicates", new
            {
                Roots = roots, Groups = groups.Select(g => new { Group = g.Key, g.First().Hash, Size = g.First().File.Size, Files = g.Select(f => f.Path) }),
                ExtraBytes = extra, Skipped = skipped, Recycled = result?.Done, RecycleErrors = result?.Errors,
            });
        else
        {
            foreach (var group in groups)
            {
                io.Out.WriteLine(Loc.Format("Cli.DuplicateGroup", group.Key, Storage.Format(group.First().File.Size), group.Count()));
                foreach (var file in group) io.Out.WriteLine("    " + file.Path);
            }
            io.Out.WriteLine(Loc.Format("Dup.Result", groups.Count, Storage.Format(extra), skipped));
            if (result != null) io.Out.WriteLine(Loc.Format("Recycle.Done", result.Done, Storage.Format(result.Bytes), result.Skipped));
        }
        return skipped > 0 || result?.Skipped > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    private int SimilarImages(CommandLine line, List<string> roots)
    {
        if (line.Has("--delete-to-recycle")) throw new UsageException(Loc.Get("Cli.SimilarReportOnly"));
        int threshold = line.Int("--threshold", 5, 0, 12);
        long minimum = line.Value("--min-size") is { } size ? CommandLine.ParseSize(size) : 0;
        int skipped = 0;
        var groups = SimilarImageSearch.Find(roots, data.Exclusions, threshold, minimum, token, Progress, () => skipped++, HashCache.Load("dhash"));
        ClearProgress();
        if (line.Has("--json"))
            WriteJson("duplicates", new { Roots = roots, Similar = true, Threshold = threshold, Groups = groups.Select((g, i) => new { Group = i + 1, Files = g.Select(f => new { f.Path, f.Width, f.Height, f.Size, f.Distance, f.Best }) }), Skipped = skipped });
        else
        {
            int number = 0;
            foreach (var group in groups)
            {
                io.Out.WriteLine(Loc.Format("Cli.SimilarGroup", ++number, group.Count));
                foreach (var image in group) io.Out.WriteLine($"  {(image.Best ? "*" : " ")} {image.Width}×{image.Height} {Storage.Format(image.Size),10}  {image.Path}");
            }
            io.Out.WriteLine(Loc.Format("Similar.Result", groups.Count, skipped));
        }
        return skipped > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    private int EmptyFolderSearch(CommandLine line)
    {
        var roots = Roots(line);
        bool ignoreJunk = !line.Has("--keep-system-files");
        var folders = new List<string>();
        var zero = new List<LargeFile>();
        int skipped = 0;
        foreach (string root in roots)
        {
            var found = EmptyFolders.Find(root, data.Exclusions, ignoreJunk, token, path => Progress(Loc.Format("Cli.Checking", path)));
            folders.AddRange(found.Folders); zero.AddRange(found.ZeroFiles); skipped += found.Skipped;
        }
        ClearProgress();
        CleanupResult? result = null;
        if (line.Has("--delete-to-recycle"))
        {
            if (!line.Has("--yes")) throw new UsageException(Loc.Get("Cli.DeleteNeedsYes"));
            var plan = EmptyFolders.Plan(folders, zero);
            if (plan.Count > 0) result = RecycleNow(plan, ignoreJunk);
        }
        if (line.Has("--json")) WriteJson("empty-folders", new { Roots = roots, Folders = folders, ZeroByteFiles = zero.Select(f => f.Path), Skipped = skipped, Recycled = result?.Done, RecycleErrors = result?.Errors });
        else
        {
            foreach (string folder in folders) io.Out.WriteLine($"{Loc.Get("Junk.KindFolder"),-18} {folder}");
            foreach (var file in zero) io.Out.WriteLine($"{Loc.Get("Junk.KindZero"),-18} {file.Path}");
            io.Out.WriteLine(Loc.Format("Junk.Result", folders.Count, zero.Count, skipped));
            if (result != null) io.Out.WriteLine(Loc.Format("Recycle.Done", result.Done, Storage.Format(result.Bytes), result.Skipped));
        }
        return skipped > 0 || result?.Skipped > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    /// <summary>Recycle Bin operations go through the Shell, which needs an STA thread.</summary>
    private CleanupResult RecycleNow(CleanupPlan plan, bool ignoreJunk = true)
    {
        CleanupResult? result = null;
        Exception? failure = null;
        string details = string.Join("\n", plan.Included.Select(i => i.Path));
        var thread = new Thread(() =>
        {
            try
            {
                using var journal = Journal.Begin(Cleanup.NewId(), HistoryAction.Recycle, CleanupMode.Recycle, details);
                result = Recycle.Execute(plan, data.Exclusions, IntPtr.Zero, journal, token, null, ignoreJunk);
                journal.Finish(result);
            }
            catch (Exception e) { failure = e; }
        });
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is OperationCanceledException) throw failure;
        if (failure != null) throw new IOException(failure.Message, failure);
        return result!;
    }

    private List<HistoryEntry> AllHistory() =>
        data.History.Concat(Journal.Missing(data.History)).OrderByDescending(h => h.Time).ToList();

    private int History(CommandLine line)
    {
        int last = line.Int("--last", 20, 1, 100000);
        var entries = AllHistory().Take(last).ToList();
        if (line.Has("--json"))
            WriteJson("history", new { Operations = entries.Select(h => new { h.Id, h.Time, h.Action, h.Mode, h.Scheduled, h.Files, h.Bytes, h.Skipped, h.Result, Restorable = h.CanRestore, h.Restored }) });
        else
            foreach (var h in entries)
                io.Out.WriteLine($"{h.DisplayTime,-18} {h.Id,-22} {h.DisplayAction,-26} {h.Files,8:N0} {h.DisplaySize,10}  {h.DisplayResult}{(h.CanRestore ? " · " + h.Undo : "")}");
        return ExitCode.Success;
    }

    private int Restore(CommandLine line)
    {
        if (line.Positional.Count != 1) throw new UsageException(Loc.Get("Cli.RestoreNeedsId"));
        string id = line.Positional[0];
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) throw new UsageException(Loc.Format("Cli.UnknownOperation", id));
        var (header, items) = Journal.Read(id);
        if (header == null) throw new UsageException(Loc.Format("Cli.UnknownOperation", id));
        var stored = items.Where(i => i.Result == Outcome.Quarantined).ToList();
        if (stored.Count == 0 || !Directory.Exists(Quarantine.FolderFor(id))) { io.Error.WriteLine(Loc.Get("History.NothingToRestore")); return ExitCode.Partial; }
        string question = Loc.Format("History.RestoreConfirm", stored.Count, Storage.Format(stored.Sum(i => i.Size)), header.Time.ToString("g", Loc.Culture));
        if (!Confirm(line, question.Replace("\n\n", " "))) return io.Interactive && !line.Has("--yes") ? ExitCode.Success : ExitCode.Usage;
        var result = Quarantine.Restore(id, stored, token);
        if (line.Has("--json")) WriteJson("restore", new { Operation = id, result.Restored, result.Conflicts, result.Missing, result.Failed, result.Errors });
        else io.Out.WriteLine(result.Summary);
        return result.Conflicts + result.Missing + result.Failed > 0 ? ExitCode.Partial : ExitCode.Success;
    }

    private int Categories(CommandLine line)
    {
        var errors = new List<RuleError>();
        var groups = Cleaners.Groups(errors);
        if (line.Has("--json"))
            WriteJson("categories", new
            {
                Categories = groups.Select(g => new { g.Rule.Id, g.Name, g.Rule.Group, g.Rule.Risk, EnabledByDefault = g.Rule.Enabled, User = g.Rule.IsUser, Folders = g.Roots }),
                Errors = errors.Select(e => new { e.Source, e.Message }),
            });
        else
        {
            foreach (var group in groups.GroupBy(g => g.GroupName))
            {
                io.Out.WriteLine(group.Key);
                foreach (var g in group) io.Out.WriteLine($"  {g.Rule.Id,-24} {(g.Rule.Enabled ? "*" : " ")} {g.RiskText,-18} {g.Name}");
            }
            foreach (var error in errors) io.Error.WriteLine(Loc.Format("Cli.RuleSkipped", error.Source, error.Message));
        }
        return errors.Count > 0 ? ExitCode.Partial : ExitCode.Success;
    }
}
