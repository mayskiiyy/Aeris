using System.IO;

namespace Aeris.Tests;

public class CleanersTests
{
    [Fact]
    public void BuiltinRulesAreValid()
    {
        var rules = Cleaners.Builtin();
        Assert.True(rules.Count > 40);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        var english = Loc.Dictionary(Loc.Fallback);
        foreach (var rule in rules)
        {
            Assert.NotEmpty(rule.Targets);
            Assert.Contains(rule.Group, CleanerGroup.Order);
            Assert.True(rule.Name != null || (rule.NameKey != null && english.ContainsKey(rule.NameKey)), rule.Id);
            Assert.True(rule.DescriptionKey == null || english.ContainsKey(rule.DescriptionKey), rule.Id);
            Assert.True(rule.LocationKey == null || english.ContainsKey(rule.LocationKey), rule.Id);
            Assert.True(english.ContainsKey($"CacheGroup.{rule.Group}"), rule.Group);
            foreach (var target in rule.Targets)
            {
                Assert.DoesNotContain("node_modules", target.Root, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@".nuget\packages", target.Root, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void OnlySafeRulesAreOnByDefault()
    {
        foreach (var rule in Cleaners.Builtin()) Assert.Equal(rule.Risk == Risk.Safe, rule.Enabled);
    }

    [Fact]
    public void BuiltinRootsExpandToAllowedFolders()
    {
        foreach (var rule in Cleaners.Builtin())
            foreach (var target in rule.Targets)
                if (Cleaners.Expand(target.Root) is { } root)
                    Assert.True(PathGuard.RootProblem(root, rule.AllowProgramFiles) == null, $"{rule.Id}: {root}");
    }

    [Fact]
    public void ExpandHandlesVariablesAndRejectsUnknown()
    {
        Assert.Equal(PathGuard.Canonical(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "Temp")), Cleaners.Expand(@"%LOCALAPPDATA%\Temp"));
        Assert.Null(Cleaners.Expand(@"%AERIS_DOES_NOT_EXIST%\x"));
        Assert.Null(Cleaners.Expand(@"relative\path"));
        Assert.Null(Cleaners.Expand(@"{reg:HKCU\Software\Aeris\Nope:Missing}\x"));
    }

    [Fact]
    public void UserRulesAreTightenedAndBadOnesRejected()
    {
        using var temp = new TempDir();
        string cache = temp.Dir("app-cache");
        string folder = temp.Dir("rules");
        File.WriteAllText(Path.Combine(folder, "good.json"), $$"""
            { "id": "my-app", "name": "My app", "risk": "Safe", "defaultEnabled": true, "allowProgramFiles": true,
              "targets": [ { "root": "{{cache.Replace(@"\", @"\\")}}", "pattern": "*.tmp" } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "profile.json"), """
            { "id": "evil", "name": "Profile", "targets": [ { "root": "%USERPROFILE%" } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "documents.json"), """
            { "id": "docs", "name": "Docs", "targets": [ { "root": "%USERPROFILE%\\Documents\\x" } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "unknown.json"), """
            { "id": "typo", "name": "Typo", "tragets": [] }
            """);
        File.WriteAllText(Path.Combine(folder, "pattern.json"), """
            { "id": "pat", "name": "Pattern", "targets": [ { "root": "%TEMP%", "pattern": "..\\*" } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "clash.json"), """
            { "id": "user-temp", "name": "Clash", "targets": [ { "root": "%TEMP%" } ] }
            """);
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");
        var errors = new List<RuleError>();
        var rules = Cleaners.LoadUser(folder, errors, ["user-temp"]);
        var rule = Assert.Single(rules);
        Assert.Equal("my-app", rule.Id);
        Assert.Equal(Risk.Normal, rule.Risk);
        Assert.False(rule.Enabled);
        Assert.False(rule.AllowProgramFiles);
        Assert.Equal(CleanerGroup.Custom, rule.Group);
        Assert.Equal(6, errors.Count);
    }

    [Fact]
    public void PatternAndRecurseLimitTheScan()
    {
        using var temp = new TempDir();
        var old = DateTime.UtcNow.AddDays(-10);
        temp.File(@"c\a.tmp", "1", old);
        temp.File(@"c\b.log", "22", old);
        temp.File(@"c\sub\c.tmp", "333", old);
        var rule = new CleanerRule { Id = "t", Name = "T", Targets = [new() { Root = Path.Combine(temp.Path, "c"), Pattern = "*.tmp", Recurse = false }] };
        var group = new CacheGroup(rule, Cleaners.Resolve(rule).ToArray());
        group.Scan(DateTime.UtcNow.AddDays(-3), [], CancellationToken.None);
        Assert.Equal(["a.tmp"], group.Items.Select(i => i.Name));
    }

    [Fact]
    public void AgeFilterNeedsBothDatesOld()
    {
        using var temp = new TempDir();
        var old = DateTime.UtcNow.AddDays(-10);
        string fresh = temp.File(@"c\fresh.tmp");
        string touched = temp.File(@"c\touched.tmp", "x", old);
        File.SetLastWriteTimeUtc(touched, DateTime.UtcNow);
        temp.File(@"c\old.tmp", "x", old);
        var rule = new CleanerRule { Id = "t", Name = "T", Targets = [new() { Root = Path.Combine(temp.Path, "c") }] };
        var group = new CacheGroup(rule, Cleaners.Resolve(rule).ToArray());
        group.Scan(DateTime.UtcNow.AddDays(-2), [], CancellationToken.None);
        Assert.Equal(["old.tmp"], group.Items.Select(i => i.Name));
        Assert.Equal(3, group.Total);
    }

    [Fact]
    public void ChromiumProfilesExpandToCacheFolders()
    {
        using var temp = new TempDir();
        temp.Dir(@"User Data\Default\Cache");
        temp.Dir(@"User Data\Profile 2\Code Cache");
        temp.Dir(@"User Data\Other\Cache");
        temp.Dir(@"User Data\Default\Local Storage");
        var rule = new CleanerRule { Id = "b", Name = "B", Targets = [new() { Root = Path.Combine(temp.Path, "User Data"), Each = "chromium-profiles" }] };
        var roots = Cleaners.Resolve(rule).Select(t => Path.GetRelativePath(temp.Path, t.Root)).Order().ToArray();
        Assert.Equal(new[] {@"User Data\Default\Cache", @"User Data\Profile 2\Code Cache" }, roots);
    }
}
