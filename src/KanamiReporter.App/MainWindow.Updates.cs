using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace KanamiReporter.App;

public partial class MainWindow
{
    /// <summary>
    /// 主界面打开后自动检查一次更新：发现新版本时跳到「关于与日志」页展示更新信息，
    /// 没有新版本则完全静默（连检查进度都不显示，避免打扰）。
    /// </summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
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
        // 后台自动检查不显示进度，避免每次启动都在界面上闪一条"正在检查更新…"。
        if (showNoUpdateMessage)
        {
            UpdateStatusText.Text = "正在检查更新…";
        }

        var currentVersion = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        _availableUpdate = await _updateService.CheckAsync(currentVersion);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(_settings);

        // 直连不通时更新服务会自动改走加速镜像，这里把实际用的路由告诉用户。
        var routeHint = _updateService.LastRequestUsedMirror
            ? $"（已通过 {_updateService.LastRoute} 加速）"
            : string.Empty;

        if (_availableUpdate.IsUpdateAvailable)
        {
            UpdateStatusText.Text =
                $"发现新版本 {_availableUpdate.LatestVersion}{routeHint}。{Environment.NewLine}{_availableUpdate.ReleaseNotes}";
            DownloadUpdateButton.IsEnabled = true;

            // 自动检查发现新版本：把用户带到更新所在的「关于与日志」页。
            if (!showNoUpdateMessage)
            {
                ShowView(AboutNavItem);
            }
        }
        else
        {
            UpdateStatusText.Text = showNoUpdateMessage ? $"当前已经是最新版本。{routeHint}" : string.Empty;
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
            var routeHint = _updateService.LastRequestUsedMirror
                ? $"（经 {_updateService.LastRoute} 加速）"
                : string.Empty;
            UpdateStatusText.Text = $"下载完成{routeHint}，正在启动安装器…";
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
