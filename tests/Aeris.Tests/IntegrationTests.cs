namespace Aeris.Tests;

public class IntegrationTests
{
    [Fact]
    public void LaunchArgumentsAreParsed()
    {
        var scan = LaunchRequest.Parse(["--scan", @"C:\Users\Public\..\Public"]);
        Assert.Equal(@"C:\Users\Public", scan.Folder);
        Assert.Equal("map", scan.Tab);
        Assert.Equal("scan", scan.Action);

        var largest = LaunchRequest.Parse(["--largest", @"D:\"]);
        Assert.Equal("files", largest.Tab);
        Assert.Equal("scan", largest.Action);

        var duplicates = LaunchRequest.Parse(["--duplicates", @"C:\Photos"]);
        Assert.Equal("duplicates", duplicates.Action);

        Assert.Equal("history", LaunchRequest.Parse(["--tab", "History"]).Tab);
        Assert.True(LaunchRequest.Parse([]).IsEmpty);
        Assert.Null(LaunchRequest.Parse(["--scan"]).Folder);
    }

    [Fact]
    public void ScheduledTaskRunsTheSafeProfileWithoutAWindow()
    {
        var settings = new ScheduleSettings { Enabled = true, Frequency = ScheduleFrequency.Daily, Hour = 3, OlderThanDays = 1, Mode = CleanupMode.Recycle };
        var args = Schedule.CreateArguments(@"C:\Program Files\Aeris\Aeris.exe", settings);
        Assert.Equal("\"C:\\Program Files\\Aeris\\Aeris.exe\" --scheduled-clean scheduled", args[Array.IndexOf(args, "/TR") + 1]);
        Assert.Equal("DAILY", args[Array.IndexOf(args, "/SC") + 1]);
        Assert.Equal("03:00", args[Array.IndexOf(args, "/ST") + 1]);
        Assert.Equal("LIMITED", args[Array.IndexOf(args, "/RL") + 1]);
        var profile = Schedule.Profile(settings);
        Assert.Empty(profile.Categories);
        Assert.Equal(2, profile.OlderThanDays);
        Assert.Equal(CleanupMode.Permanent, profile.Mode);
    }
}
