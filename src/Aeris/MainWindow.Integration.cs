using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Aeris;

public partial class MainWindow
{
    private static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Aeris.exe");

    /// <summary>Handles --scan, --duplicates, --largest and --tab, from this launch or from a second instance.</summary>
    public async void Launch(LaunchRequest request, bool activate)
    {
        if (activate)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
        }
        if (request.IsEmpty || busy) return;
        if (request.Folder != null)
        {
            if (!Directory.Exists(request.Folder) || !Storage.SafePath(request.Folder)) { Status.Text = Loc.Get("Scan.FolderUnavailable"); return; }
            SetFolder(request.Folder);
        }
        MainTabs.SelectedItem = request.Tab switch
        {
            "map" => MapTab,
            "files" => FilesTab,
            "duplicates" => DuplicatesTab,
            "caches" or "cache" => CacheTab,
            "empty" or "junk" => JunkTab,
            "history" => HistoryTab,
            "settings" => SettingsTab,
            _ => MainTabs.SelectedItem,
        };
        if (request.Action == "scan") await Scan(false);
        else if (request.Action == "duplicates") { DuplicateMode.SelectedIndex = 0; FindDuplicates(this, new RoutedEventArgs()); }
    }

    private void RestoreIntegrationSettings()
    {
        bool previous = restoring;
        restoring = true;
        try
        {
            if (ScheduleHour.Items.Count == 0)
                for (int hour = 0; hour < 24; hour++) ScheduleHour.Items.Add(new ComboBoxItem { Tag = hour.ToString(), Content = new DateTime(2000, 1, 1, hour, 0, 0).ToString("t", Loc.Culture) });
            else
                foreach (var item in ScheduleHour.Items.OfType<ComboBoxItem>()) item.Content = new DateTime(2000, 1, 1, int.Parse((string)item.Tag), 0, 0).ToString("t", Loc.Culture);
            foreach (var item in ScheduleAge.Items.OfType<ComboBoxItem>()) item.Content = Loc.Format("Schedule.OlderThan", Loc.Plural("Option.Days", int.Parse((string)item.Tag)));
            var schedule = data.Schedule;
            ScheduleEnabled.IsChecked = schedule.Enabled;
            ScheduleFrequency.SelectedItem = ScheduleFrequency.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, schedule.Frequency.ToString()));
            ScheduleHour.SelectedIndex = schedule.Hour;
            ScheduleAge.SelectedItem = Closest(ScheduleAge, schedule.OlderThanDays);
            ScheduleMode.SelectedIndex = schedule.Mode == CleanupMode.Quarantine ? 0 : 1;
            foreach (var combo in new[] { ScheduleFrequency, ScheduleHour, ScheduleAge, ScheduleMode }) RefreshSelectionBox(combo);
            SchedulePanel.IsEnabled = schedule.Enabled;
            ShellMenuEnabled.IsChecked = ShellMenu.IsEnabled();
        }
        finally { restoring = previous; }
    }

    /// <summary>Runs schtasks, so it is only called when Settings is shown or the schedule changes.</summary>
    private async void RefreshScheduleStatus()
    {
        string exe = Executable;
        var (exists, current) = await Task.Run(() => Schedule.Exists() ? (true, Schedule.PointsTo(exe)) : (false, false));
        ScheduleStatus.Text = Loc.Get(!exists ? (data.Schedule.Enabled ? "Schedule.Missing" : "Schedule.Off")
            : !data.Schedule.Enabled ? "Schedule.Orphan" : current ? "Schedule.Active" : "Schedule.Moved");
    }

    private void ScheduleToggled(object sender, RoutedEventArgs e)
    {
        SchedulePanel.IsEnabled = ScheduleEnabled.IsChecked == true;
        if (ScheduleEnabled.IsChecked != true) SaveSchedule(sender, e);
    }

    private async void SaveSchedule(object sender, RoutedEventArgs e)
    {
        if (restoring) return;
        var schedule = data.Schedule;
        schedule.Enabled = ScheduleEnabled.IsChecked == true;
        if (ScheduleFrequency.SelectedItem is ComboBoxItem { Tag: string frequency }) schedule.Frequency = Enum.Parse<ScheduleFrequency>(frequency);
        schedule.Hour = Math.Max(0, ScheduleHour.SelectedIndex);
        if (ScheduleAge.SelectedItem is ComboBoxItem { Tag: string days }) schedule.OlderThanDays = int.Parse(days);
        schedule.Mode = ScheduleMode.SelectedIndex == 1 ? CleanupMode.Permanent : CleanupMode.Quarantine;
        data.Profiles.RemoveAll(p => string.Equals(p.Name, Schedule.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (schedule.Enabled) data.Profiles.Add(Schedule.Profile(schedule));
        SaveSettings();
        string exe = Executable;
        var (code, output) = await Task.Run(() =>
        {
            try { return schedule.Enabled ? Schedule.Run(Schedule.CreateArguments(exe, schedule)) : Schedule.Exists() ? Schedule.Run(Schedule.DeleteArguments()) : (0, ""); }
            catch (Win32Exception ex) { return (-1, ex.Message); }
        });
        Status.Text = code == 0 ? Loc.Get(schedule.Enabled ? "Schedule.Saved" : "Schedule.Removed") : Loc.Format("Schedule.Failed", output);
        RefreshScheduleStatus();
    }

    private void ShellMenuToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShellMenuEnabled.IsChecked == true) ShellMenu.Enable(Executable);
            else ShellMenu.Disable();
            Status.Text = Loc.Get(ShellMenuEnabled.IsChecked == true ? "Shell.Added" : "Shell.Removed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Status.Text = Loc.Format("Shell.Failed", ex.Message);
            ShellMenuEnabled.IsChecked = ShellMenu.IsEnabled();
        }
    }
}
