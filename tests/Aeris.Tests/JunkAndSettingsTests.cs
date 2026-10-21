using System.IO;

namespace Aeris.Tests;

public class JunkAndSettingsTests
{
    [Fact]
    public void EmptyFoldersReportsTopmostOnly()
    {
        using var temp = new TempDir(outsideAppData: true);
        temp.Dir(@"empty\a\b");
        temp.Dir(@"empty\c");
        temp.File(@"junk\desktop.ini");
        temp.File(@"full\file.txt");
        temp.Dir(@"full\empty-child");
        temp.File(@"zero\nothing.txt", "");
        var result = EmptyFolders.Find(temp.Path, [], ignoreJunk: true, CancellationToken.None);
        var names = result.Folders.Select(f => Path.GetRelativePath(temp.Path, f)).Order().ToArray();
        Assert.Equal(new[] { "empty", @"full\empty-child", "junk" }, names);
        Assert.Equal(["nothing.txt"], result.ZeroFiles.Select(f => f.Name));
    }

    [Fact]
    public void JunkCountsAsContentWhenNotIgnored()
    {
        using var temp = new TempDir(outsideAppData: true);
        temp.File(@"junk\Thumbs.db");
        var result = EmptyFolders.Find(temp.Path, [], ignoreJunk: false, CancellationToken.None);
        Assert.Empty(result.Folders);
        Assert.False(EmptyFolders.IsEmpty(Path.Combine(temp.Path, "junk"), false));
        Assert.True(EmptyFolders.IsEmpty(Path.Combine(temp.Path, "junk"), true));
    }

    [Fact]
    public void ProtectedFoldersAreNeverOffered()
    {
        Assert.True(EmptyFolders.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FileAttributes.Directory));
        Assert.True(EmptyFolders.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomeApp"), FileAttributes.Directory));
        Assert.True(EmptyFolders.IsProtected(@"C:\Something", FileAttributes.Directory | FileAttributes.System));
    }

    [Fact]
    public void SettingsFromVersionOneAreMigratedWithoutLoss()
    {
        using var temp = new TempDir();
        string file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, $$"""
            {
              "Folder": "{{temp.Path.Replace(@"\", @"\\")}}",
              "RetentionDays": 7,
              "MinimumMb": 500,
              "FileType": 2,
              "Exclusions": ["C:\\Games\\"],
              "History": [
                { "Time": "2026-10-01T10:00:00", "Action": "Cleanup", "Details": "User temporary files", "Files": 10, "Bytes": 1000, "Skipped": 1, "Result": "Completed" },
                { "Time": "2026-10-02T10:00:00", "Action": "Recycle", "Details": "C:\\a.bin", "Files": 1, "Bytes": 5, "Skipped": 0, "Result": "Running" }
              ]
            }
            """);
        var data = UserData.Load(file);
        Assert.Equal(UserData.CurrentSchema, data.SchemaVersion);
        Assert.Equal(7, data.RetentionDays);
        Assert.Equal(500, data.MinimumMb);
        Assert.Equal(2, data.FileType);
        Assert.Equal([@"C:\Games"], data.Exclusions);
        Assert.Equal(2, data.History.Count);
        Assert.Equal("Permanent", data.History[0].Mode);
        Assert.Equal("Recycle", data.History[1].Mode);
        Assert.Equal(HistoryResult.Interrupted, data.History[1].Result);
        Assert.Equal(CleanupMode.Permanent, data.CleanupMode);
        Assert.Equal(7, data.QuarantineDays);

        data.CleanupMode = CleanupMode.Quarantine;
        data.Save();
        Assert.True(File.Exists(file + ".bak"));
        var again = UserData.Load(file);
        Assert.Equal(CleanupMode.Quarantine, again.CleanupMode);
        Assert.Equal(2, again.History.Count);
    }

    [Fact]
    public void BrokenSettingsFallBackToBackup()
    {
        using var temp = new TempDir();
        string file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{ broken");
        File.WriteAllText(file + ".bak", """{ "RetentionDays": 30 }""");
        Assert.Equal(30, UserData.Load(file).RetentionDays);
    }
}
