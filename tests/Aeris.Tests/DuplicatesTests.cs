using System.IO;

namespace Aeris.Tests;

public class DuplicatesTests
{
    private static DuplicateFile Copy(string path, int group, long modified) =>
        new(new LargeFile(path, 10, modified, modified), "HASH", path, group);

    [Fact]
    public void PlanRefusesToRemoveEveryCopy()
    {
        var a = Copy(@"C:\x\a.bin", 1, 1);
        var b = Copy(@"C:\y\a.bin", 1, 2);
        Assert.Throws<InvalidOperationException>(() => Duplicates.Plan([a, b], [a, b]));
        var plan = Duplicates.Plan([b], [a, b]);
        Assert.Equal(a.Path, Assert.Single(plan.Included).Keeper);
    }

    [Fact]
    public void KeepsOneDetectsKeeperIncludedLater()
    {
        var a = Copy(@"C:\x\a.bin", 1, 1);
        var b = Copy(@"C:\y\a.bin", 1, 2);
        var plan = Duplicates.Plan([b], [a, b]);
        Assert.True(Duplicates.KeepsOne(plan));
        plan.Categories[0].Items.Add(new PlanItem(a.Path, 10, 1, 1, "dup-1") { Keeper = b.Path, Hash = "HASH" });
        Assert.False(Duplicates.KeepsOne(plan));
    }

    [Theory]
    [InlineData("Oldest", @"C:\b\old.bin")]
    [InlineData("Newest", @"C:\a\very\deep\new.bin")]
    [InlineData("ShortestPath", @"C:\c\m.bin")]
    [InlineData("ShortestName", @"C:\c\m.bin")]
    public void KeepRules(string rule, string expected)
    {
        DuplicateFile[] group = [Copy(@"C:\b\old.bin", 1, 1), Copy(@"C:\a\very\deep\new.bin", 1, 9), Copy(@"C:\c\m.bin", 1, 5)];
        Assert.Equal(expected, DuplicateRules.Keeper(group, rule, []).Path);
    }

    [Fact]
    public void PriorityFolderWinsAndProtectedFoldersAreNeverPicked()
    {
        DuplicateFile[] group = [Copy(@"C:\photos\a.jpg", 1, 9), Copy(@"C:\backup\a.jpg", 1, 1), Copy(@"C:\desk\a.jpg", 1, 5)];
        Assert.Equal(@"C:\photos\a.jpg", DuplicateRules.Keeper(group, "Oldest", [@"C:\photos"]).Path);
        var extra = DuplicateRules.Extra(group, "Oldest", [@"C:\photos"], [@"C:\desk"]);
        Assert.Equal([@"C:\backup\a.jpg"], extra.Select(e => e.Path));
    }

    [Fact]
    public void FindsDuplicatesAndIgnoresHardLinks()
    {
        using var temp = new TempDir();
        string a = temp.File(@"a\one.bin", new string('x', 5000));
        temp.File(@"b\two.bin", new string('x', 5000));
        temp.File(@"c\other.bin", new string('y', 5000));
        Assert.True(HardLink.CreateHardLink(Path.Combine(temp.Path, "link.bin"), a, IntPtr.Zero));
        var found = Analysis.FindDuplicates(temp.Path, [], CancellationToken.None, _ => { }, () => { });
        Assert.Equal(2, found.Count);
        Assert.Single(found.Select(f => f.Group).Distinct());
    }
}

file static class HardLink
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateHardLink(string link, string target, IntPtr security);
}
