using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Aeris;

public partial class MainWindow
{
    private UpdateInfo? update;
    private CancellationTokenSource? updating;
    private bool startupUpdateChecked;
    private bool windowClosed;

    private async void CheckStartupUpdate(object? sender, EventArgs e)
    {
        if (startupUpdateChecked) return;
        startupUpdateChecked = true;
        await CheckUpdatesAsync(true);
    }

    private void RefreshUpdateCard()
    {
        UpdateVersion.Text = Loc.Format("Update.Current", Updater.Current.ToString(3));
        if (update == null && updating == null) UpdateStatus.Text = Loc.Get("Update.NotChecked");
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(false);

    private async Task CheckUpdatesAsync(bool startup)
    {
        if (updating != null) return;
        updating = new();
        UpdateCheckButton.IsEnabled = false;
        UpdateStatus.Text = Loc.Get("Update.Checking");
        try
        {
            using var client = Updater.CreateClient();
            update = await new Updater(client).CheckAsync(Updater.Current, updating.Token);
            if (update == null)
            {
                UpdateStatus.Text = Loc.Format("Update.UpToDate", Updater.Current.ToString(3));
                UpdateNotes.Visibility = UpdateInstallButton.Visibility = UpdatePageButton.Visibility = Visibility.Collapsed;
                return;
            }
            bool installed = Updater.IsInstalled(Executable);
            UpdateStatus.Text = Loc.Format(installed ? "Update.AvailableInstalled" : "Update.AvailablePortable", update.Version.ToString(3));
            UpdateNotes.Text = update.Notes;
            UpdateNotes.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateInstallButton.Content = Loc.Get(installed ? "Update.Install" : "Update.DownloadZip");
            UpdateInstallButton.Visibility = (installed ? update.Setup : update.Portable) != null && update.Checksums != null ? Visibility.Visible : Visibility.Collapsed;
            UpdatePageButton.Visibility = Visibility.Visible;
            if (startup && !windowClosed && MessageBox.Show(this,
                Loc.Format("Update.Prompt", update.Version.ToString(3)), Loc.Get("Settings.Updates"),
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes)
                MainTabs.SelectedItem = SettingsTab;
        }
        catch (UpdateException ex) { UpdateStatus.Text = ex.Message; }
        catch (OperationCanceledException) { UpdateStatus.Text = Loc.Get("Update.NotChecked"); }
        finally { updating?.Dispose(); updating = null; UpdateCheckButton.IsEnabled = true; }
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (update == null || updating != null || busy) return;
        bool installed = Updater.IsInstalled(Executable);
        var asset = installed ? update.Setup : update.Portable;
        if (asset == null) return;
        string question = Loc.Format(installed ? "Update.ConfirmInstall" : "Update.ConfirmZip", update.Version.ToString(3), Storage.Format(asset.Size));
        if (MessageBox.Show(this, question, Loc.Get("Settings.Updates"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        updating = new();
        UpdateCheckButton.IsEnabled = UpdateInstallButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            UpdateProgress.Value = 100.0 * p.Done / Math.Max(1, p.Total);
            UpdateStatus.Text = Loc.Format("Update.Downloading", Storage.Format(p.Done), Storage.Format(p.Total));
        });
        try
        {
            using var client = Updater.CreateClient();
            string file = await new Updater(client).DownloadAsync(update, asset, Updater.DownloadFolder, progress, updating.Token);
            if (installed)
            {
                if (busy) { UpdateStatus.Text = Loc.Get("Update.Busy"); return; }
                UpdateStatus.Text = Loc.Get("Update.Starting");
                SaveSettings();
                Updater.RunInstaller(file);
                Application.Current.Shutdown();
                return;
            }
            UpdateStatus.Text = Loc.Format("Update.ZipReady", file);
            Reveal(file, false);
        }
        catch (UpdateException ex) { UpdateStatus.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { UpdateStatus.Text = Loc.Format("Update.Error.Network", ex.Message); }
        catch (OperationCanceledException) { UpdateStatus.Text = Loc.Get("Update.NotChecked"); }
        finally
        {
            updating?.Dispose(); updating = null;
            UpdateCheckButton.IsEnabled = UpdateInstallButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenReleasePage(object sender, RoutedEventArgs e)
    {
        string url = update?.PageUrl is { } page && page.StartsWith($"https://github.com/{Updater.Repository}/", StringComparison.Ordinal) ? page : $"https://github.com/{Updater.Repository}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { UpdateStatus.Text = ex.Message; }
    }
}
