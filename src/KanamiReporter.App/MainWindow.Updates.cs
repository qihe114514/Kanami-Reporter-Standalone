using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace KanamiReporter.App;

public partial class MainWindow
{
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        if (_settings.LastUpdateCheckUtc is DateTimeOffset last &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
        {
            return;
        }

        try
        {
            await CheckForUpdatesAsync(showNoUpdateMessage: false);
        }
        catch (Exception exception)
        {
            _logger.Warning($"后台检查更新失败：{exception.Message}");
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync(showNoUpdateMessage: true);
        }
        catch (Exception exception)
        {
            _logger.Warning($"检查更新失败：{exception.Message}");
            UpdateStatusText.Text = "检查更新失败，请确认网络连接。";
        }
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdateMessage)
    {
        UpdateStatusText.Text = "正在检查更新…";
        var currentVersion = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        _availableUpdate = await _updateService.CheckAsync(currentVersion);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);

        if (_availableUpdate.IsUpdateAvailable)
        {
            UpdateStatusText.Text =
                $"发现新版本 {_availableUpdate.LatestVersion}。{Environment.NewLine}{_availableUpdate.ReleaseNotes}";
            DownloadUpdateButton.IsEnabled = true;
        }
        else
        {
            UpdateStatusText.Text = showNoUpdateMessage ? "当前已经是最新版本。" : string.Empty;
            DownloadUpdateButton.IsEnabled = false;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || !_availableUpdate.IsUpdateAvailable)
        {
            return;
        }

        try
        {
            DownloadUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在下载更新…";
            var installer = await _updateService.DownloadAsync(_availableUpdate);
            UpdateStatusText.Text = "下载完成，正在启动安装器…";
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });
            ExitApplication();
        }
        catch (Exception exception)
        {
            _logger.Error("下载更新失败。", exception);
            UpdateStatusText.Text = "更新下载或校验失败。";
            DownloadUpdateButton.IsEnabled = true;
        }
    }
}
