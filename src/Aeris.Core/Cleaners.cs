using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Aeris;

public enum Risk { Safe, Normal, Advanced }

public static class CleanerGroup
{
    public const string Windows = "Windows";
    public const string Browsers = "Browsers";
    public const string GamesAndGpu = "GamesAndGpu";
    public const string Developer = "Developer";
    public const string Apps = "Apps";
    public const string Custom = "Custom";
    public static readonly string[] Order = [Windows, Browsers, GamesAndGpu, Developer, Apps, Custom];
}

public class CleanerTarget
{
    public string Root { get; set; } = "";
    public string? Each { get; set; }
    public string[]? Folders { get; set; }
    public string Pattern { get; set; } = "*";
    public bool Recurse { get; set; } = true;
}

public class CleanerRule
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? NameKey { get; set; }
    public string? Description { get; set; }
    public string? DescriptionKey { get; set; }
    public string? LocationKey { get; set; }
    public string Group { get; set; } = CleanerGroup.Custom;
    public Risk Risk { get; set; } = Risk.Normal;
    public bool? DefaultEnabled { get; set; }
    public string[] CloseAppsHint { get; set; } = [];
    public CleanerTarget[] Targets { get; set; } = [];
    public bool AllowProgramFiles { get; set; }
    [JsonIgnore] public string Source { get; set; } = "";
    [JsonIgnore] public bool IsUser => Source.Length > 0;
    [JsonIgnore] public bool Enabled => DefaultEnabled ?? Risk == Risk.Safe;
}

public record ResolvedTarget(string Root, string Pattern, bool Recurse, bool AllowPrograms)
{
    public string Display => Pattern == "*" ? Root : Path.Combine(Root, Pattern);
    public bool Matches(string file, string directory) =>
        (Recurse || string.Equals(Path.TrimEndingDirectorySeparator(directory), Root, FileSystemPaths.Comparison))
        && (Pattern == "*" || FileSystemName.MatchesSimpleExpression(Pattern, Path.GetFileName(file), OperatingSystem.IsWindows()));
}

public record RuleError(string Source, string Message);

public static class Cleaners
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex IdFormat = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"%([A-Za-z0-9_()]+)%|\{reg:(HKCU|HKLM)\\([^:{}]+):([^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly string[] ChromiumFolders = ["Cache", "Code Cache", "GPUCache"];

    public static string DefaultUserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "cleaners");
    public static string UserFolder { get; set; } = DefaultUserFolder;

    public static List<CleanerRule> Builtin()
    {
        using var stream = typeof(Cleaners).Assembly.GetManifestResourceStream(OperatingSystem.IsLinux() ? "Aeris.Cleaners.linux.json" : "Aeris.Cleaners.builtin.json")!;
        return JsonSerializer.Deserialize<List<CleanerRule>>(stream, Json)!;
    }

    /// <summary>Rules from <see cref="UserFolder"/>. A broken file or rule is reported and left out.</summary>
    public static List<CleanerRule> LoadUser(string folder, ICollection<RuleError> errors, IEnumerable<string> takenIds)
    {
        var rules = new List<CleanerRule>();
        var ids = new HashSet<string>(takenIds, StringComparer.OrdinalIgnoreCase);
        string[] files;
        try { files = Directory.Exists(folder) && Storage.SafePath(folder) ? Directory.GetFiles(folder, "*.json") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(new(folder, e.Message)); return rules; }
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            List<CleanerRule> found;
            try
            {
                string text = File.ReadAllText(file);
                found = text.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<List<CleanerRule>>(text, Json) ?? [] : [JsonSerializer.Deserialize<CleanerRule>(text, Json)!];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { errors.Add(new(Path.GetFileName(file), e.Message)); continue; }
            foreach (var rule in found)
            {
                if (rule == null) continue;
                rule.Source = file;
                if (Validate(rule) is { } problem) { errors.Add(new($"{Path.GetFileName(file)} · {rule.Id}", problem)); continue; }
                if (!ids.Add(rule.Id)) { errors.Add(new($"{Path.GetFileName(file)} · {rule.Id}", Loc.Get("Rule.Error.DuplicateId"))); continue; }
                rules.Add(rule);
            }
        }
        return rules;
    }

    /// <summary>Checks a user rule and tightens it: user rules are never Safe, never on by default and can't reach Program Files.</summary>
    public static string? Validate(CleanerRule rule)
    {
        if (!IdFormat.IsMatch(rule.Id ?? "")) return Loc.Get("Rule.Error.Id");
        if (string.IsNullOrWhiteSpace(rule.Name)) return Loc.Get("Rule.Error.Name");
        if (rule.Targets == null || rule.Targets.Length == 0) return Loc.Get("Rule.Error.NoTargets");
        rule.NameKey = null; rule.DescriptionKey = null; rule.LocationKey = null;
        rule.Group = CleanerGroup.Custom;
        if (rule.Risk == Risk.Safe) rule.Risk = Risk.Normal;
        rule.DefaultEnabled = false;
        rule.AllowProgramFiles = false;
        rule.CloseAppsHint ??= [];
        foreach (var target in rule.Targets)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Root)) return Loc.Get("Rule.Error.NoTargets");
            if (target.Each is not (null or "subfolders" or "chromium-profiles")) return Loc.Format("Rule.Error.Each", target.Each);
            if (string.IsNullOrWhiteSpace(target.Pattern) || target.Pattern.IndexOfAny(['\\', '/', ':']) >= 0) return Loc.Format("Rule.Error.Pattern", target.Pattern ?? "");
            if ((target.Folders ?? []).Any(f => f == null || Path.IsPathRooted(f) || f.Split('\\', '/').Contains("..")))
                return Loc.Get("Rule.Error.Folders");
            if (Expand(target.Root) is not { } root) return Loc.Format("Rule.Error.Variable", target.Root);
            if (PathGuard.RootProblem(root) is { } problem) return Loc.Format("Rule.Error.Root", root, Loc.Get(problem));
        }
        return null;
    }

    /// <summary>Expands %VARIABLES% and {reg:HKCU\Key:Value}. Returns null if something is undefined or the result is relative.</summary>
    public static string? Expand(string template)
    {
        bool missing = false;
        string result = Token.Replace(template, match =>
        {
            string? value = match.Groups[1].Success
                ? (OperatingSystem.IsLinux() && match.Groups[1].Value == "XDG_CACHE_HOME"
                    ? Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                    : Environment.GetEnvironmentVariable(match.Groups[1].Value))
                : ReadRegistry(match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
            if (string.IsNullOrEmpty(value)) { missing = true; return ""; }
            return OperatingSystem.IsWindows() ? value.Replace('/', '\\') : value;
        });
        if (missing || result.Contains('%') || !Path.IsPathFullyQualified(result)) return null;
        try { return PathGuard.Canonical(result); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? ReadRegistry(string hive, string key, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(value) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Existing, allowed folders a rule points to. Nothing is followed through links.</summary>
    public static List<ResolvedTarget> Resolve(CleanerRule rule)
    {
        var result = new List<ResolvedTarget>();
        foreach (var target in rule.Targets)
        {
            if (Expand(target.Root) is not { } root || !Directory.Exists(root) || !Storage.SafePath(root)) continue;
            IEnumerable<string> bases = [root];
            try
            {
                if (target.Each == "subfolders") bases = Directory.GetDirectories(root);
                else if (target.Each == "chromium-profiles")
                    bases = Directory.GetDirectories(root).Where(p => Path.GetFileName(p) is var leaf && (leaf == "Default" || leaf.StartsWith("Profile ", StringComparison.Ordinal))).Prepend(root).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            string[] folders = target.Folders is { Length: > 0 } list ? list : target.Each == "chromium-profiles" ? ChromiumFolders : [""];
            foreach (string folder in bases.SelectMany(b => folders.Select(f => f.Length == 0 ? b : Path.Combine(b, f))))
            {
                string path;
                try { path = PathGuard.Canonical(folder); }
                catch (Exception e) when (e is ArgumentException or PathTooLongException) { continue; }
                if (!Directory.Exists(path) || !Storage.SafePath(path) || PathGuard.RootProblem(path, rule.AllowProgramFiles) != null) continue;
                result.Add(new(path, target.Pattern, target.Recurse, rule.AllowProgramFiles));
            }
        }
        return result;
    }

    /// <summary>Every rule with at least one existing folder, built-in first. A folder claimed by an earlier rule is not repeated.</summary>
    public static List<CacheGroup> Groups(ICollection<RuleError>? errors = null, string? userFolder = null)
    {
        var builtin = Builtin();
        var rules = builtin.Concat(LoadUser(userFolder ?? UserFolder, errors ?? new List<RuleError>(), builtin.Select(r => r.Id)));
        var known = new HashSet<string>(FileSystemPaths.Comparer);
        var groups = new List<CacheGroup>();
        foreach (var rule in rules)
        {
            var targets = Resolve(rule).Where(t => known.Add(t.Display)).ToArray();
            if (targets.Length > 0) groups.Add(new(rule, targets));
        }
        return groups.OrderBy(g => Array.IndexOf(CleanerGroup.Order, g.Rule.Group)).ToList();
    }
}
