using System.IO;
using System.Text.Json;
using Aeris.Cli;

namespace Aeris.Tests;

public sealed class CliTests : IDisposable
{
    private readonly TempDir temp = new();

    public CliTests()
    {
        Journal.Folder = Path.Combine(temp.Path, "history");
        Quarantine.Folder = Path.Combine(temp.Path, "quarantine");
        HashCache.Folder = Path.Combine(temp.Path, "hashcache");
        Cleaners.UserFolder = Path.Combine(temp.Path, "cleaners");
    }

    public void Dispose()
    {
        Journal.Folder = Journal.DefaultFolder;
        Quarantine.Folder = Quarantine.DefaultFolder;
        HashCache.Folder = HashCache.DefaultFolder;
        Cleaners.UserFolder = Cleaners.DefaultUserFolder;
        temp.Dispose();
    }

    private static (int Code, string Out, string Error) Run(UserData data, string input, bool interactive, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Aeris.Cli.Cli.Run(args, new CliIO(output, error, new StringReader(input), interactive), data, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static (int Code, string Out, string Error) Run(params string[] args) => Run(new UserData(), "", false, args);

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("scan")]
    [InlineData("scan", "C:\\definitely\\missing\\folder")]
    [InlineData("clean", "--mode", "shred")]
    [InlineData("clean", "--older-than", "1d")]
    [InlineData("clean", "--older-than", "soon")]
    [InlineData("clean", "--categories", "no-such-category")]
    [InlineData("clean", "--categories", "user-temp", "--profile", "x")]
    [InlineData("clean", "--profile", "missing")]
    [InlineData("clean", "--yes=1")]
    [InlineData("clean", "--older-than")]
    [InlineData("duplicates")]
    [InlineData("history", "--last", "0")]
    [InlineData("restore")]
    [InlineData("restore", "..\\evil")]
    [InlineData("categories", "--frobnicate")]
    public void BadArgumentsExitWithTwo(params string[] args) => Assert.Equal(ExitCode.Usage, Run(args).Code);

    [Fact]
    public void HelpAndVersionSucceed()
    {
        Assert.Equal(0, Run("--help").Code);
        Assert.Contains("aeris-cli", Run().Out);
        Assert.StartsWith("aeris-cli ", Run("--version").Out);
    }

    [Theory]
    [InlineData("500", 500L)]
    [InlineData("1KB", 1024L)]
    [InlineData("100mb", 100L << 20)]
    [InlineData("1.5GB", 3L << 29)]
    public void SizesParse(string text, long bytes) => Assert.Equal(bytes, CommandLine.ParseSize(text));

    [Theory]
    [InlineData("48h", 48)]
    [InlineData("2d", 48)]
    [InlineData("1w", 168)]
    public void AgesParse(string text, int hours) => Assert.Equal(TimeSpan.FromHours(hours), CommandLine.ParseAge(text));

    [Fact]
    public void ScheduledCleanupRefusesRiskyCategories() =>
        Assert.Equal(ExitCode.Usage, Run("clean", "--categories", "gradle-caches", "--scheduled", "--dry-run").Code);

    [Fact]
    public void CleanWithoutYesIsRefusedWhenNotInteractive()
    {
        // The rule points at a temp folder holding one old file, so the plan is never empty.
        string cache = temp.Dir("rules-cache");
        temp.File(@"rules-cache\old.tmp", "x", DateTime.UtcNow.AddDays(-30));
        Directory.CreateDirectory(Cleaners.UserFolder);
        File.WriteAllText(Path.Combine(Cleaners.UserFolder, "test.json"), $$"""{ "id": "aeris-test-rule", "name": "Test", "targets": [ { "root": "{{cache.Replace(@"\", @"\\")}}" } ] }""");
        {
            var refused = Run("clean", "--categories", "aeris-test-rule");
            Assert.Equal(ExitCode.Usage, refused.Code);
            Assert.True(File.Exists(Path.Combine(cache, "old.tmp")));

            var declined = Run(new UserData(), "n\n", true, "clean", "--categories", "aeris-test-rule");
            Assert.Equal(ExitCode.Success, declined.Code);
            Assert.True(File.Exists(Path.Combine(cache, "old.tmp")));

            var dry = Run("clean", "--categories", "aeris-test-rule", "--dry-run", "--json");
            Assert.Equal(ExitCode.Success, dry.Code);
            using var json = JsonDocument.Parse(dry.Out);
            Assert.Equal(Aeris.Cli.Cli.JsonSchema, json.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("plan")[0].GetProperty("files").GetInt32());
            Assert.True(File.Exists(Path.Combine(cache, "old.tmp")));

            var done = Run("clean", "--categories", "aeris-test-rule", "--yes", "--mode", "quarantine", "--json");
            Assert.Equal(ExitCode.Success, done.Code);
            Assert.False(File.Exists(Path.Combine(cache, "old.tmp")));
            string id = JsonDocument.Parse(done.Out).RootElement.GetProperty("operation").GetString()!;

            var history = Run("history", "--json");
            Assert.Contains(id, history.Out);

            var restored = Run("restore", id, "--yes");
            Assert.Equal(ExitCode.Success, restored.Code);
            Assert.True(File.Exists(Path.Combine(cache, "old.tmp")));
        }
    }

    [Fact]
    public void DuplicatesReportsAndRecyclesOnlyWithYes()
    {
        using var folder = new TempDir();
        folder.File(@"a\x.bin", new string('d', 3000));
        folder.File(@"b\x.bin", new string('d', 3000));
        var report = Run("duplicates", folder.Path, "--json");
        Assert.Equal(ExitCode.Success, report.Code);
        Assert.Single(JsonDocument.Parse(report.Out).RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal(ExitCode.Usage, Run("duplicates", folder.Path, "--delete-to-recycle").Code);
        Assert.True(File.Exists(Path.Combine(folder.Path, @"a\x.bin")) && File.Exists(Path.Combine(folder.Path, @"b\x.bin")));
    }

    [Fact]
    public void ScanFindsTheLargestFiles()
    {
        using var folder = new TempDir();
        folder.File("small.txt", "x");
        folder.File(@"deep\big.mp4", new string('v', 10000));
        var result = Run("scan", folder.Path, "--top", "1", "--json");
        Assert.Equal(ExitCode.Success, result.Code);
        var largest = JsonDocument.Parse(result.Out).RootElement.GetProperty("largest");
        Assert.EndsWith("big.mp4", largest[0].GetProperty("path").GetString());
        Assert.Equal(1, largest.GetArrayLength());
        Assert.Equal(ExitCode.Success, Run("scan", folder.Path, "--type", "video", "--min-size", "5KB").Code);
    }
}
