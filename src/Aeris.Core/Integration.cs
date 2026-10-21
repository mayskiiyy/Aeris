using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Aeris;

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public class ScheduleSettings
{
    public bool Enabled { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    public int Hour { get; set; } = 12;
    public int OlderThanDays { get; set; } = 7;
    public CleanupMode Mode { get; set; } = CleanupMode.Quarantine;
}

/// <summary>
/// A per-user Task Scheduler task that runs Safe categories through the same code as aeris-cli. No service, no
/// administrator rights and nothing resident: Windows starts Aeris without a window and it exits when done.
/// </summary>
public static class Schedule
{
    public const string TaskName = "Aeris automatic cleanup";
    public const string ProfileName = "scheduled";

    public static string[] CreateArguments(string exe, ScheduleSettings settings)
    {
        string schedule = settings.Frequency switch { ScheduleFrequency.Daily => "DAILY", ScheduleFrequency.Monthly => "MONTHLY", _ => "WEEKLY" };
        return ["/Create", "/TN", TaskName, "/TR", $"\"{exe}\" --scheduled-clean {ProfileName}", "/SC", schedule, "/ST", $"{Math.Clamp(settings.Hour, 0, 23):00}:00", "/RL", "LIMITED", "/F"];
    }

    public static string[] DeleteArguments() => ["/Delete", "/TN", TaskName, "/F"];

    /// <summary>The profile the task runs: every Safe, on-by-default category, the chosen age and mode.</summary>
    public static CleanupProfile Profile(ScheduleSettings settings) =>
        new() { Name = ProfileName, Categories = [], OlderThanDays = Math.Clamp(settings.OlderThanDays, 2, 365), Mode = settings.Mode == CleanupMode.Quarantine ? CleanupMode.Quarantine : CleanupMode.Permanent };

    public static (int Code, string Output) Run(IEnumerable<string> arguments)
    {
        // schtasks writes in the console (OEM) code page, which .NET only knows after the provider is registered.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = oem, StandardErrorEncoding = oem,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }

    public static bool Exists() => Run(["/Query", "/TN", TaskName]).Code == 0;
}

/// <summary>Explorer context menu entries under HKCU, so no administrator rights are needed.</summary>
public static class ShellMenu
{
    public static readonly (string Verb, string Argument, string LabelKey)[] Verbs =
    [
        ("Aeris.Scan", "--scan", "Shell.Scan"),
        ("Aeris.Duplicates", "--duplicates", "Shell.Duplicates"),
        ("Aeris.Largest", "--largest", "Shell.Largest"),
    ];

    public static readonly string[] Targets = [@"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell"];

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Targets[0]}\{Verbs[0].Verb}");
        return key != null;
    }

    public static void Enable(string exe)
    {
        foreach (string target in Targets)
            foreach (var (verb, argument, label) in Verbs)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{target}\{verb}");
                key.SetValue("MUIVerb", Loc.Get(label));
                key.SetValue("Icon", $"\"{exe}\",0");
                using var command = key.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" {argument} \"%1\"");
            }
    }

    public static void Disable()
    {
        foreach (string target in Targets)
            foreach (var (verb, _, _) in Verbs)
                Registry.CurrentUser.DeleteSubKeyTree($@"{target}\{verb}", throwOnMissingSubKey: false);
    }
}

/// <summary>What Aeris.exe was asked to do on its command line or by a second instance.</summary>
public record LaunchRequest(string? Folder, string? Tab, string? Action)
{
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        string? folder = null, tab = null, action = null;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (arg.ToLowerInvariant())
            {
                case "--scan": action = "scan"; tab ??= "map"; folder = Next(); break;
                case "--duplicates": action = "duplicates"; tab = "duplicates"; folder = Next(); break;
                case "--largest": action = "scan"; tab = "files"; folder = Next(); break;
                case "--tab": tab = Next()?.ToLowerInvariant(); break;
            }
        }
        if (folder != null)
        {
            try { folder = Path.GetFullPath(folder.Trim('"')); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { folder = null; action = null; }
        }
        return new(folder, tab, action);
    }

    public bool IsEmpty => Folder == null && Tab == null;
}
