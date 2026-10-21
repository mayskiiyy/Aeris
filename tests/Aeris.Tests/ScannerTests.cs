using System.Diagnostics;
using System.IO;

namespace Aeris.Tests;

public class ScannerTests
{
    private static void Tree(TempDir temp)
    {
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
            temp.File($@"d{i % 7}\s{i % 3}\f{i}.{(i % 4 == 0 ? "mp4" : "txt")}", new string('x', random.Next(0, 5000)));
        temp.File("root.bin", new string('r', 12345));
        temp.Dir(@"empty\deeper");
    }

    [Fact]
    public void LogicalSizesMatchTheOldWalker()
    {
        using var temp = new TempDir();
        Tree(temp);
        var result = StandardScanner.Scan(temp.Path, new([]), null, CancellationToken.None);
        long oldTotal = 0, oldCount = 0;
        var oldFolders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Storage.Walk(temp.Path, CancellationToken.None, () => { }))
        {
            oldTotal += file.Length; oldCount++;
            string key = Analysis.FolderKey(temp.Path, file.FullName);
            oldFolders[key] = oldFolders.GetValueOrDefault(key) + file.Length;
        }
        Assert.Equal(oldTotal, result.Nodes.Size(0));
        Assert.Equal(oldCount, result.Nodes.Files(0));
        foreach (int child in result.Nodes.Children(0).Where(c => result.Nodes.IsDirectory(c) && result.Nodes.Size(c) > 0))
            Assert.Equal(oldFolders[result.Nodes.FullPath(child)], result.Nodes.Size(child));
        Assert.Equal(0, result.Skipped);
        Assert.True(result.Nodes.Size(0, onDisk: true) >= oldTotal);
        Assert.Equal(75, result.FileNodes().Count(n => result.Nodes.Kind(n) == FileKind.Video));
    }

    [Fact]
    public void HardLinksCountOnceAndJunctionsAreNotFollowed()
    {
        using var temp = new TempDir();
        string a = temp.File(@"a\big.bin", new string('b', 10000));
        Assert.True(HardLinkNative.CreateHardLink(Path.Combine(temp.Path, "link.bin"), a, IntPtr.Zero));
        string outside = new TempDir().Path;
        File.WriteAllText(Path.Combine(outside, "far.bin"), new string('f', 50000));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(temp.Path, "j")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        var result = StandardScanner.Scan(temp.Path, new([]), null, CancellationToken.None);
        Assert.Equal(10000, result.Nodes.Size(0));
        Assert.Equal(2, result.Nodes.Files(0));
        Assert.Equal(1, result.Skipped);
        Directory.Delete(Path.Combine(temp.Path, "j"));
        Directory.Delete(outside, true);
    }

    [Fact]
    public void ExclusionsAndFindWork()
    {
        using var temp = new TempDir();
        Tree(temp);
        string excluded = Path.Combine(temp.Path, "d1");
        var result = StandardScanner.Scan(temp.Path, new([excluded]), null, CancellationToken.None);
        Assert.Equal(-1, result.Find(excluded));
        int node = result.Find(Path.Combine(temp.Path, @"d2\s1"));
        Assert.True(node > 0);
        Assert.Equal(Path.Combine(temp.Path, @"d2\s1"), result.Nodes.FullPath(node));
        Assert.Equal(-1, result.Find(@"C:\Windows"));
    }

    [Fact]
    public void CancellationStopsTheScan()
    {
        using var temp = new TempDir();
        Tree(temp);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StandardScanner.Scan(temp.Path, new([]), null, cancel.Token));
    }

    [Fact]
    public void SquarifyFillsTheBoundsWithoutOverlap()
    {
        double[] sizes = [600, 300, 200, 150, 100, 60, 40, 30, 10, 5, 3, 1];
        var bounds = new TreemapRect(10, 20, 640, 360);
        var rects = Treemap.Squarify(sizes, bounds);
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3);
        double scale = bounds.Area / sizes.Sum();
        for (int i = 0; i < rects.Length; i++)
        {
            Assert.Equal(sizes[i] * scale, rects[i].Area, 3);
            Assert.True(rects[i].X >= bounds.X - 1e-6 && rects[i].Y >= bounds.Y - 1e-6);
            Assert.True(rects[i].X + rects[i].Width <= bounds.X + bounds.Width + 1e-6 && rects[i].Y + rects[i].Height <= bounds.Y + bounds.Height + 1e-6);
            for (int j = i + 1; j < rects.Length; j++)
            {
                double overlapX = Math.Min(rects[i].X + rects[i].Width, rects[j].X + rects[j].Width) - Math.Max(rects[i].X, rects[j].X);
                double overlapY = Math.Min(rects[i].Y + rects[i].Height, rects[j].Y + rects[j].Height) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(overlapX > 1e-6 && overlapY > 1e-6, $"{i} overlaps {j}");
            }
        }
        double worst = rects.Take(6).Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
        Assert.True(worst < 3, $"aspect ratio {worst}");
    }

    [Fact]
    public void LayoutGathersTinyFilesIntoOneCell()
    {
        using var temp = new TempDir();
        temp.File("big.bin", new string('b', 100000));
        for (int i = 0; i < 100; i++) temp.File($"tiny{i}.txt", new string('t', 30));
        var result = StandardScanner.Scan(temp.Path, new([]), null, CancellationToken.None);
        var cells = Treemap.Layout(result.Nodes, 0, new(0, 0, 200, 100), false);
        var small = Assert.Single(cells, c => c.Node < 0);
        Assert.Equal(100, small.SmallCount);
        Assert.Contains(cells, c => c.Node >= 0 && result.Nodes.Name(c.Node) == "big.bin");
    }
}

file static class HardLinkNative
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateHardLink(string link, string target, IntPtr security);
}
