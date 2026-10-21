using System.IO;

namespace Aeris.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly DateTime old = DateTime.UtcNow.AddDays(-10);
    private readonly DateTime cutoff = DateTime.UtcNow.AddDays(-3);

    public CleanupTests()
    {
        Journal.Folder = Path.Combine(temp.Path, "history");
        Quarantine.Folder = Path.Combine(temp.Path, "quarantine");
    }

    public void Dispose()
    {
        Journal.Folder = Journal.DefaultFolder;
        Quarantine.Folder = Quarantine.DefaultFolder;
        temp.Dispose();
    }

    private CacheGroup Group(string folder = "cache")
    {
        var rule = new CleanerRule { Id = "test", Name = "Test", Risk = Risk.Safe, Targets = [new() { Root = Path.Combine(temp.Path, folder) }] };
        var group = new CacheGroup(rule, Cleaners.Resolve(rule).ToArray()) { Selected = true };
        group.Scan(cutoff, [], CancellationToken.None);
        return group;
    }

    private CleanupResult Run(CleanupPlan plan, CacheGroup group)
    {
        using var journal = Journal.Begin(Cleanup.NewId(), plan.Action, plan.Mode, plan.Details);
        return Cleanup.ExecuteCache(plan, [], journal, CancellationToken.None, null, [group]);
    }

    [Fact]
    public void PlanDoesNotTouchTheDisk()
    {
        string a = temp.File(@"cache\a.bin", "aaaa", old);
        string b = temp.File(@"cache\sub\b.bin", "bb", old);
        var group = Group();
        var plan = Cleanup.CachePlan([group], cutoff, CleanupMode.Permanent);
        Assert.Equal(2, plan.Count);
        Assert.Equal(6, plan.Bytes);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.False(Directory.Exists(Journal.Folder));
    }

    [Fact]
    public void ExecutionDeletesOnlyIncludedAndUnchangedFiles()
    {
        string keep = temp.File(@"cache\keep.bin", "k", old);
        string changed = temp.File(@"cache\changed.bin", "c", old);
        string delete = temp.File(@"cache\delete.bin", "d", old);
        var group = Group();
        var plan = Cleanup.CachePlan([group], cutoff, CleanupMode.Permanent);
        plan.Categories[0].Items.Single(i => i.Path == keep).Included = false;
        File.WriteAllText(changed, "changed");
        File.SetCreationTimeUtc(changed, old); File.SetLastWriteTimeUtc(changed, old);
        var result = Run(plan, group);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(changed));
        Assert.False(File.Exists(delete));
        Assert.Equal(1, result.Done);
        Assert.Equal(1, result.Changed);
        Assert.Equal(HistoryResult.Partial, result.Result);
        var (header, items) = Journal.Read(result.Id);
        Assert.Equal(HistoryAction.Cleanup, header!.Action);
        Assert.Contains(items, i => i.Path == delete && i.Result == Outcome.Deleted);
        Assert.Contains(items, i => i.Path == changed && i.Result == Outcome.Changed);
    }

    [Fact]
    public void FileSwappedForALinkIsNotDeleted()
    {
        temp.File(@"cache\sub\x.bin", "x", old);
        string outside = temp.File(@"outside\x.bin", "x", old);
        var group = Group();
        var plan = Cleanup.CachePlan([group], cutoff, CleanupMode.Permanent);
        Directory.Delete(Path.Combine(temp.Path, @"cache\sub"), true);
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(temp.Path, @"cache\sub")}\" \"{Path.Combine(temp.Path, "outside")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        var result = Run(plan, group);
        Assert.True(File.Exists(outside));
        Assert.Equal(0, result.Done);
        Directory.Delete(Path.Combine(temp.Path, @"cache\sub"));
    }

    [Fact]
    public void RootOutsideCurrentRulesIsRefused()
    {
        temp.File(@"cache\a.bin", "a", old);
        string other = temp.File(@"other\b.bin", "b", old);
        var group = Group();
        var foreign = Group("other");
        var plan = Cleanup.CachePlan([foreign], cutoff, CleanupMode.Permanent);
        var result = Run(plan, group);
        Assert.True(File.Exists(other));
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void RetentionUnderTwoDaysIsRefused()
    {
        temp.File(@"cache\a.bin", "a", old);
        var group = Group();
        var plan = Cleanup.CachePlan([group], DateTime.UtcNow.AddHours(-47), CleanupMode.Permanent);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(plan, group));
    }

    [Fact]
    public void QuarantineMovesAndRestoresWithDates()
    {
        string a = temp.File(@"cache\deep\a.bin", "aaaa", old);
        var group = Group();
        var plan = Cleanup.CachePlan([group], cutoff, CleanupMode.Quarantine);
        var result = Run(plan, group);
        Assert.False(File.Exists(a));
        Assert.Equal(1, result.Done);
        var (_, items) = Journal.Read(result.Id);
        var stored = Assert.Single(items).Stored!;
        Assert.True(File.Exists(stored));
        Assert.StartsWith(Quarantine.FolderFor(result.Id), stored);

        var restore = Quarantine.Restore(result.Id, items, CancellationToken.None);
        Assert.Equal(1, restore.Restored);
        Assert.True(File.Exists(a));
        Assert.Equal(old, File.GetLastWriteTimeUtc(a));
        Assert.Equal(old, File.GetCreationTimeUtc(a));
        Assert.False(Directory.Exists(Quarantine.FolderFor(result.Id)));
    }

    [Fact]
    public void RestoreNeverOverwrites()
    {
        string a = temp.File(@"cache\a.bin", "original", old);
        var group = Group();
        var result = Run(Cleanup.CachePlan([group], cutoff, CleanupMode.Quarantine), group);
        File.WriteAllText(a, "new file in the same place");
        var (_, items) = Journal.Read(result.Id);
        var restore = Quarantine.Restore(result.Id, items, CancellationToken.None);
        Assert.Equal(0, restore.Restored);
        Assert.Equal(1, restore.Conflicts);
        Assert.Equal("new file in the same place", File.ReadAllText(a));
        Assert.True(File.Exists(items[0].Stored!));
    }

    [Fact]
    public void PurgeRemovesExpiredAndOversizedOperations()
    {
        Directory.CreateDirectory(Path.Combine(Quarantine.Folder, "20200101-000000-aaaa", "C"));
        File.WriteAllText(Path.Combine(Quarantine.Folder, "20200101-000000-aaaa", "C", "x"), "x");
        string fresh = Cleanup.NewId();
        Directory.CreateDirectory(Path.Combine(Quarantine.Folder, fresh, "C"));
        File.WriteAllText(Path.Combine(Quarantine.Folder, fresh, "C", "y"), "yy");
        var removed = Quarantine.Purge(7, long.MaxValue);
        Assert.Equal(["20200101-000000-aaaa"], removed);
        Assert.True(Directory.Exists(Quarantine.FolderFor(fresh)));
        Assert.Equal([fresh], Quarantine.Purge(7, 1));
    }

    [Fact]
    public void JournalRotationKeepsNewest()
    {
        Directory.CreateDirectory(Journal.Folder);
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(Journal.Folder, $"2020010{i}-000000-0000.jsonl"), "{}");
        Journal.Rotate(keep: 2, pinned: new HashSet<string> { "20200100-000000-0000" });
        var left = Directory.GetFiles(Journal.Folder).Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(new[] {"20200100-000000-0000", "20200103-000000-0000", "20200104-000000-0000" }, left);
    }
}
