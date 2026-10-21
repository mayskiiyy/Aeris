using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Aeris.Tests;

public class PathGuardTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint buffer);

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void FileInsideRootIsAllowed()
    {
        using var temp = new TempDir();
        string file = temp.File(@"cache\a\b.bin");
        Assert.True(PathGuard.IsAllowedTarget(file, Path.Combine(temp.Path, "cache")));
    }

    [Fact]
    public void DotDotCannotLeaveTheRoot()
    {
        using var temp = new TempDir();
        string outside = temp.File(@"outside.txt");
        temp.Dir("cache");
        Assert.False(PathGuard.IsAllowedTarget(Path.Combine(temp.Path, @"cache\..\outside.txt"), Path.Combine(temp.Path, "cache")));
        Assert.False(PathGuard.IsAllowedTarget(outside, Path.Combine(temp.Path, "cache")));
    }

    [Fact]
    public void SiblingWithSamePrefixIsOutside()
    {
        using var temp = new TempDir();
        string file = temp.File(@"cache2\x.bin");
        Assert.False(PathGuard.IsAllowedTarget(file, Path.Combine(temp.Path, "cache")));
    }

    [Fact]
    public void RootItselfIsNotATarget()
    {
        using var temp = new TempDir();
        string root = temp.Dir("cache");
        Assert.False(PathGuard.IsAllowedTarget(root, root));
    }

    [Fact]
    public void JunctionOnThePathIsRejected()
    {
        using var temp = new TempDir();
        string real = temp.Dir("real");
        temp.File(@"real\data.bin");
        string root = temp.Dir("cache");
        string link = Path.Combine(root, "link");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "junction was not created");
        Assert.False(PathGuard.IsAllowedTarget(Path.Combine(link, "data.bin"), root));
        Assert.Empty(Storage.Walk(root, CancellationToken.None, () => { }));
        Directory.Delete(link);
    }

    [Fact]
    public void ShortNamesAreExpanded()
    {
        using var temp = new TempDir();
        string longDir = temp.Dir("A very long folder name for 8.3");
        string file = temp.File(@"A very long folder name for 8.3\f.txt");
        var buffer = new StringBuilder(1024);
        GetShortPathName(longDir, buffer, 1024);
        string shortDir = buffer.ToString();
        Assert.Equal(PathGuard.Canonical(longDir), PathGuard.Canonical(shortDir), ignoreCase: true);
        Assert.True(PathGuard.IsAllowedTarget(Path.Combine(shortDir, "f.txt"), longDir));
        Assert.False(PathGuard.IsAllowedTarget(file, Path.Combine(temp.Path, "other")));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    public void DriveRootIsNeverAllowed(string root) => Assert.NotNull(PathGuard.RootProblem(root));

    [Fact]
    public void ProfileAndContainersAreNeverAllowed()
    {
        Assert.NotNull(PathGuard.RootProblem(Profile));
        Assert.NotNull(PathGuard.RootProblem(Path.GetDirectoryName(Profile)!));
        Assert.NotNull(PathGuard.RootProblem(Local));
        Assert.NotNull(PathGuard.RootProblem(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(Profile, "AppData")));
    }

    [Fact]
    public void PersonalFoldersAndTheirContentAreNeverAllowed()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.NotNull(PathGuard.RootProblem(documents));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(documents, "cache")));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(Profile, "Downloads")));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(Profile, "Downloads", "x")));
    }

    [Fact]
    public void WindowsIsBlockedExceptTemp()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(PathGuard.RootProblem(windows));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(windows, "System32")));
        Assert.Null(PathGuard.RootProblem(Path.Combine(windows, "Temp")));
        Assert.NotNull(PathGuard.RootProblem(Path.Combine(windows, @"Temp\..\System32")));
    }

    [Fact]
    public void ProgramFilesNeedsAnExplicitBuiltinException()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string steam = Path.Combine(programs, @"Steam\steamapps\shadercache");
        Assert.NotNull(PathGuard.RootProblem(programs, allowPrograms: true));
        Assert.NotNull(PathGuard.RootProblem(steam));
        Assert.Null(PathGuard.RootProblem(steam, allowPrograms: true));
    }

    [Fact]
    public void AppSubfolderIsAllowed() => Assert.Null(PathGuard.RootProblem(Path.Combine(Local, "Temp")));
}
