using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using KanamiReporter.Core;
using KanamiReporter.Windows;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinForms = System.Windows.Forms;

namespace KanamiReporter.App;

public partial class MainWindow : Window
{
    private readonly ReporterRuntime _runtime;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ResourceImporter _resourceImporter;
    private readonly BuiltInResourcePack _builtInResources;
    private readonly GitHubUpdateService _updateService;
    private readonly FileLogger _logger;
    private readonly bool _startHidden;
    private readonly WinForms.NotifyIcon _trayIcon = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly ObservableCollection<string> _debugMessages = new();
    private readonly ObservableCollection<TemplateScoreItem> _templateScores = new();

    private GlobalHotkey? _hotkey;
    private WriteableBitmap? _previewBitmap;
    private DispatcherQueueTimer? _captureRefreshTimer;
    private DispatcherQueueTimer? _scoreRefreshTimer;
    private CaptureTargetDescriptor? _lastSelectedTarget;
    private DetectionResult? _lastDetectionResult;
    private UpdateCheckResult? _availableUpdate;
    private bool _allowClose;
    private bool _updatingControls;
    private bool _isCaptureRunning;
    private bool _captureRefreshInProgress;
    private bool _loaded;
    private long _fpsWindowStarted;
    private int _framesInWindow;

    public MainWindow(
        ReporterRuntime runtime,
        AppSettings settings,
        SettingsStore settingsStore,
        ResourceImporter resourceImporter,
        BuiltInResourcePack builtInResources,
        GitHubUpdateService updateService,
        FileLogger logger,
        bool startHidden)
    {
        InitializeComponent();

        _runtime = runtime;
        _settings = settings;
        _settingsStore = settingsStore;
        _resourceImporter = resourceImporter;
        _builtInResources = builtInResources;
        _updateService = updateService;
        _logger = logger;
        _startHidden = startHidden;

        _runtime.FrameAvailable += Runtime_FrameAvailable;
        _runtime.StatusChanged += Runtime_StatusChanged;
        _runtime.DetectionUpdated += Runtime_DetectionUpdated;
        _runtime.AnnouncementTriggered += Runtime_AnnouncementTriggered;
        _runtime.AnnouncementPlaybackChanged += Runtime_AnnouncementPlaybackChanged;

        MainNav.SelectionChanged += MainNav_SelectionChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
        RootGrid.Loaded += RootGrid_Loaded;

        ConfigureWindow();
        InitializeStaticLists();
        InitializeSettingsControls();
        InitializeTrayIcon();
        ShowView(RunNavItem);
    }

    private void ConfigureWindow()
    {
        AppWindow.Resize(new SizeInt32(1500, 940));

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"设置应用图标失败：{exception.Message}");
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1120;
            presenter.PreferredMinimumHeight = 700;
        }

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(150, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(36, 255, 255, 255);
        }
        catch (Exception exception)
        {
            _logger.Warning($"设置自绘标题栏失败：{exception.Message}");
        }

        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                RootGrid.Background = new SolidColorBrush(Colors.Transparent);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"设置窗口背景失败：{exception.Message}");
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        VersionText.Text = $"版本 {GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
        LogPathText.Text = _logger.LogFile;

        if (!_settings.FirstRunCompleted)
        {
            WelcomeBar.IsOpen = !_startHidden;
            _settings.FirstRunCompleted = true;
            await _settingsStore.SaveAsync(_settings);
        }

        await ReloadTargetsAsync(showErrors: true);
        LoadAudioDevices();
        RefreshResourceSummary();
        StartCaptureAutoRefresh();
        UpdateCaptureControls();

        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _hotkey = new GlobalHotkey(
                windowHandle,
                GlobalHotkey.ModControl | GlobalHotkey.ModAlt,
                0x52,
                () => _ = ToggleRecognitionAsync());
        }
        catch (Exception exception)
        {
            _logger.Warning($"注册全局热键失败：{exception.Message}");
        }

        if (_startHidden)
        {
            HideToTray();
        }

        if (_settings.CheckForUpdates)
        {
            _ = CheckForUpdatesInBackgroundAsync();
        }
    }

    private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            ShowView(item);
        }
    }

    private void ShowView(NavigationViewItem item)
    {
        var tag = item.Tag as string;
        RunView.Visibility = tag == "run" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(MainNav.SelectedItem, item))
        {
            MainNav.SelectedItem = item;
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && _settings.MinimizeToTray)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _captureRefreshTimer?.Stop();
        _scoreRefreshTimer?.Stop();
        _hotkey?.Dispose();
        _hotkey = null;
        _trayIcon.Dispose();
        (Application.Current as App)?.Shutdown();
    }

    private void InitializeStaticLists()
    {
        DebugLogList.ItemsSource = _debugMessages;
        TemplateScoreList.ItemsSource = _templateScores;

        for (var i = 0; i < ReporterStates.Count; i++)
        {
            _templateScores.Add(new TemplateScoreItem((StateId)i));
        }
    }

    private void InitializeSettingsControls()
    {
        _updatingControls = true;
        SettingsThresholdSlider.Value = _settings.MatchThreshold;
        SettingsThresholdText.Text = _settings.MatchThreshold.ToString("0.000");
        AudioVolumeSlider.Value = _settings.AudioVolume;
        AudioVolumeText.Text = $"{(int)Math.Round(_settings.AudioVolume * 100)}%";
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        CheckUpdatesCheck.IsChecked = _settings.CheckForUpdates;
        _settings.MinimizeWhenRecognitionStarts = false;
        _runtime.Threshold = _settings.MatchThreshold;
        _runtime.AudioVolume = _settings.AudioVolume;
        DebugThresholdText.Text = $"匹配阈值：{_settings.MatchThreshold:0.000}";
        _updatingControls = false;
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("开始 / 停止识别", null, async (_, _) => await ToggleRecognitionAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon.Text = "香奈美播报员";
        _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void StartCaptureAutoRefresh()
    {
        _captureRefreshTimer = DispatcherQueue.CreateTimer();
        _captureRefreshTimer.Interval = TimeSpan.FromSeconds(2);
        _captureRefreshTimer.IsRepeating = true;
        _captureRefreshTimer.Tick += CaptureRefreshTimer_Tick;
        _captureRefreshTimer.Start();
    }

    private async void CaptureRefreshTimer_Tick(DispatcherQueueTimer sender, object args) =>
        await ReloadTargetsAsync(showErrors: false);

    private async Task ReloadTargetsAsync(bool showErrors)
    {
        if (_captureRefreshInProgress)
        {
            return;
        }

        _captureRefreshInProgress = true;
        try
        {
            var previousTarget = (CaptureTargets.SelectedItem as CaptureTargetDescriptor) ?? _lastSelectedTarget;
            var targets = await _runtime.DiscoverTargetsAsync();

            if (!TargetListsEqual(CaptureTargets.ItemsSource as IEnumerable<CaptureTargetDescriptor>, targets))
            {
                CaptureTargets.ItemsSource = targets;
            }

            var selected = targets.FirstOrDefault(target => target.Id == previousTarget?.Id)
                ?? targets.FirstOrDefault(target => target.Id == _settings.LastCaptureTargetId)
                ?? targets.FirstOrDefault(target =>
                    !string.IsNullOrWhiteSpace(_settings.LastCaptureProcessName) &&
                    string.Equals(target.ProcessName, _settings.LastCaptureProcessName, StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault();

            CaptureTargets.SelectedItem = selected;
            _lastSelectedTarget = selected;
            CaptureHintText.Text = targets.Count == 0
                ? "没有找到可捕获的窗口或显示器。"
                : $"已发现 {targets.Count} 个捕获源，列表每 2 秒自动刷新。";
        }
        catch (Exception exception)
        {
            _logger.Warning($"自动刷新捕获源失败：{exception.Message}");
            if (showErrors)
            {
                await ShowMessageAsync(exception.Message, "捕获源错误");
            }
        }
        finally
        {
            _captureRefreshInProgress = false;
        }
    }

    private static bool TargetListsEqual(
        IEnumerable<CaptureTargetDescriptor>? previous,
        IReadOnlyList<CaptureTargetDescriptor> current)
    {
        if (previous is null)
        {
            return false;
        }

        var previousList = previous.ToArray();
        if (previousList.Length != current.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var left = previousList[i];
            var right = current[i];
            if (left.Id != right.Id ||
                left.DisplayName != right.DisplayName ||
                left.Bounds != right.Bounds ||
                left.ProcessName != right.ProcessName ||
                left.Kind != right.Kind)
            {
                return false;
            }
        }

        return true;
    }

    private async void RefreshTargets_Click(object sender, RoutedEventArgs e) =>
        await ReloadTargetsAsync(showErrors: true);

    private async void StartRecognition_Click(object sender, RoutedEventArgs e) => await StartRecognitionAsync();

    private async void StopRecognition_Click(object sender, RoutedEventArgs e) => await StopRecognitionAsync();

    private async Task ToggleRecognitionAsync()
    {
        if (_isCaptureRunning)
        {
            await StopRecognitionAsync();
        }
        else
        {
            await StartRecognitionAsync();
        }
    }

    private async Task StartRecognitionAsync()
    {
        if (CaptureTargets.SelectedItem is not CaptureTargetDescriptor target)
        {
            await ShowMessageAsync("请先选择窗口或显示器。", "香奈美播报员");
            return;
        }

        try
        {
            _settings.LastCaptureTargetId = target.Id;
            _settings.LastCaptureProcessName = target.ProcessName;
            await _runtime.StartAsync(target);
            await _settingsStore.SaveAsync(_settings);
            _isCaptureRunning = true;
            UpdateCaptureControls();

            DebugSourceText.Text = $"捕获源：{target.DisplayName}";
            PreviewStatusText.Text = "实时预览";
            TitleStatusText.Text = "识别中";
            TitleSubtitleText.Text = target.DisplayName;
            AppendDebug($"开始识别：{target.DisplayName}");
        }
        catch (Exception exception)
        {
            _logger.Error("开始识别失败。", exception);
            await ShowMessageAsync(exception.Message, "无法开始采集");
        }
    }

    private async Task StopRecognitionAsync()
    {
        try
        {
            await _runtime.StopAsync();
            _isCaptureRunning = false;
            UpdateCaptureControls();
            AppendDebug("识别已停止。");
        }
        catch (Exception exception)
        {
            _logger.Error("停止识别失败。", exception);
        }
    }

    private void UpdateCaptureControls()
    {
        CaptureTargets.IsEnabled = !_isCaptureRunning;
        StartRecognitionButton.IsEnabled = !_isCaptureRunning;
        StopRecognitionButton.IsEnabled = _isCaptureRunning;
        TitleStatusText.Text = _isCaptureRunning ? "识别中" : "待机";
        TitleSubtitleText.Text = _isCaptureRunning ? "正在分析捕获画面" : "识别已停止";
    }

    private void Runtime_FrameAvailable(object? sender, CapturedFrame frame)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () => UpdatePreview(frame));
        UpdateFrameRate();
    }

    private void UpdatePreview(CapturedFrame frame)
    {
        if (_previewBitmap is null ||
            _previewBitmap.PixelWidth != frame.Width ||
            _previewBitmap.PixelHeight != frame.Height)
        {
            _previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            RunPreviewImage.Source = _previewBitmap;
        }

        using (var stream = _previewBitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Bgra, 0, frame.Bgra.Length);
        }

        _previewBitmap.Invalidate();
        CaptureHintBorder.Visibility = Visibility.Collapsed;
        PreviewStatusBadge.Visibility = Visibility.Visible;
        PreviewStatusText.Text = "实时预览";
        DebugFrameInfoText.Text = $"当前帧：{frame.Width}×{frame.Height}  时间 {frame.Timestamp.TotalSeconds:0.000} 秒";
    }

    private void UpdateFrameRate()
    {
        var now = Environment.TickCount64;
        if (_fpsWindowStarted == 0)
        {
            _fpsWindowStarted = now;
        }

        _framesInWindow++;
        if (now - _fpsWindowStarted < 1000)
        {
            return;
        }

        var fps = _framesInWindow * 1000.0 / (now - _fpsWindowStarted);
        _framesInWindow = 0;
        _fpsWindowStarted = now;
        DispatcherQueue.TryEnqueue(() => FpsText.Text = $"{fps:0.0} 帧/秒");
    }

    private void Runtime_StatusChanged(object? sender, CaptureStatus status)
    {
        _logger.Info(status.Message);
        if (!status.IsRunning)
        {
            _isCaptureRunning = false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            CaptureHintText.Text = status.Message;
            PreviewStatusText.Text = status.IsRunning ? "实时预览" : "已停止";
            UpdateCaptureControls();
            AppendDebug($"采集状态：{status.Message}");
        });
    }

    private void Runtime_DetectionUpdated(object? sender, DetectionResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastDetectionResult = result;
            UpdateObservation(result);
            QueueTemplateScoreRefresh();
        });
    }

    private void UpdateObservation(DetectionResult result)
    {
        CurrentStateText.Text = result.StateId is { } stateId
            ? ReporterStates.GetDisplayName(stateId)
            : "未知";
        ScoreText.Text = result.BestScore < 0 ? "-" : result.BestScore.ToString("0.000");
        DebugThresholdText.Text = $"匹配阈值：{_runtime.Threshold:0.000}";
        DebugStateDurationText.Text = $"阶段计时：{result.StateDuration.TotalSeconds:0.0} 秒";
        GameCountdownText.Text = result.EstimatedPhaseRemaining is { } remaining
            ? FormatCountdown(remaining)
            : "--:--";
        GamePhaseText.Text = string.IsNullOrWhiteSpace(result.PhaseTimingLabel)
            ? "实时识别"
            : result.PhaseTimingLabel;
        GameRoundText.Text = result.RoundNumber > 0 ? $"第 {result.RoundNumber} 回合" : "回合 --";
        GameSideText.Text = result.Side switch
        {
            1 => "阵营：进攻方",
            2 => "阵营：防守方",
            _ => "阵营：未知"
        };
        GameEventText.Text = BuildNextEventText(result);
        TitleSubtitleText.Text = $"{CurrentStateText.Text} · {GameCountdownText.Text}";
    }

    private static string FormatCountdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalMinutes >= 1
            ? $"{(int)value.TotalMinutes}:{value.Seconds:00}"
            : $"{value.TotalSeconds:0.0} 秒";
    }

    private static string BuildNextEventText(DetectionResult result)
    {
        if (result.StateId == StateId.RoundStart)
        {
            var threshold = ReporterStates.GetRoundStartCountdownSeconds(result.RoundNumber);
            var seconds = threshold - result.StateDuration.TotalSeconds;
            return seconds > 0
                ? $"倒计时语音：{seconds:0.0} 秒后触发"
                : "倒计时语音：已触发";
        }

        if (result.StateId == StateId.RoundIngame)
        {
            var seconds = result.StateDuration.TotalSeconds;
            if (seconds < 75)
            {
                return $"剩余40秒语音：{75 - seconds:0.0} 秒后触发";
            }

            if (seconds < 95)
            {
                return $"剩余20秒语音：{95 - seconds:0.0} 秒后触发";
            }

            return "剩余20秒语音：已触发";
        }

        if (result.StateId == StateId.RoundIngameBombPlanted)
        {
            return "炸弹安装状态：模板已命中";
        }

        return "当前阶段没有计时语音";
    }

    private void QueueTemplateScoreRefresh()
    {
        if (_scoreRefreshTimer is null)
        {
            _scoreRefreshTimer = DispatcherQueue.CreateTimer();
            _scoreRefreshTimer.Interval = TimeSpan.FromMilliseconds(350);
            _scoreRefreshTimer.IsRepeating = false;
            _scoreRefreshTimer.Tick += TemplateScoreRefreshTimer_Tick;
        }

        if (!_scoreRefreshTimer.IsRunning)
        {
            _scoreRefreshTimer.Start();
        }
    }

    private void TemplateScoreRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_lastDetectionResult is { } result)
        {
            UpdateTemplateScores(result);
        }
    }

    private void UpdateTemplateScores(DetectionResult result)
    {
        if (result.Scores.Count != ReporterStates.Count)
        {
            return;
        }

        for (var i = 0; i < ReporterStates.Count; i++)
        {
            var score = result.Scores[i];
            _templateScores[i].SetScore(score, score >= 0 && score >= _runtime.Threshold);
        }

        var ordered = _templateScores
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.StateName, StringComparer.CurrentCulture)
            .ToArray();

        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = _templateScores.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _templateScores.Move(currentIndex, targetIndex);
            }
        }

        for (var i = 0; i < ordered.Length; i++)
        {
            ordered[i].SetRank(i + 1);
        }
    }

    private void Runtime_AnnouncementTriggered(object? sender, string eventId)
    {
        var displayName = ReporterStates.GetEventDisplayName(eventId);
        _logger.Info($"触发事件：{displayName} ({eventId})");
        DispatcherQueue.TryEnqueue(() =>
        {
            VoicePlaybackText.Text = $"语音：准备播放 {displayName}";
            AppendDebug($"触发事件：{displayName}");
        });
    }

    private void Runtime_AnnouncementPlaybackChanged(object? sender, AnnouncementPlaybackStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            VoicePlaybackText.Text = status.IsPlaying
                ? $"语音：正在播放 {status.FileName}"
                : status.IsCompleted
                    ? $"语音：播放完成 {status.FileName}"
                    : $"语音：播放失败 {status.FileName}";

            AppendDebug(status.Error is null ? VoicePlaybackText.Text : $"语音失败：{status.Error.Message}");
        });
    }

    private void RefreshResourceSummary()
    {
        var templateLoaded = _runtime.LoadedTemplateCount;
        var voicesDirectory = Path.Combine(Path.GetDirectoryName(_runtime.TemplateDirectory)!, "voices");
        var voiceFiles = Directory.Exists(voicesDirectory)
            ? Directory.GetFiles(voicesDirectory, "*.mp3")
            : [];
        var referencedNames = ReporterStates.VoiceTable
            .SelectMany(item => item.FileNames)
            .ToHashSet(StringComparer.Ordinal);
        var mappedFiles = voiceFiles
            .Where(path => referencedNames.Contains(Path.GetFileName(path)))
            .ToArray();
        var unboundFiles = voiceFiles
            .Where(path => !referencedNames.Contains(Path.GetFileName(path)))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name)
            .ToArray();
        var missingTemplates = Enumerable.Range(0, ReporterStates.Count)
            .Select(index => (StateId)index)
            .Where(stateId => _runtime.GetTemplate(stateId) is null)
            .Select(ReporterStates.GetDisplayName)
            .ToArray();
        var expectedTemplateFiles = ReporterStates.Names
            .Select(name => name + ".krt")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedTemplateFiles = Directory.Exists(_runtime.TemplateDirectory)
            ? Directory.GetFiles(_runtime.TemplateDirectory, "*.krt")
                .Where(path => !expectedTemplateFiles.Contains(Path.GetFileName(path)))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .OrderBy(name => name)
                .ToArray()
            : [];
        var activeEventCount = ReporterStates.VoiceTable.Count(item =>
            !ReporterStates.UnsupportedDynamicEvents.Contains(item.EventId) &&
            item.FileNames.Any(name => File.Exists(Path.Combine(voicesDirectory, name))));
        var pausedDynamicEvents = ReporterStates.VoiceTable
            .Where(item => ReporterStates.UnsupportedDynamicEvents.Contains(item.EventId))
            .Select(item => ReporterStates.GetEventDisplayName(item.EventId))
            .ToArray();
        var emptyVoiceEvents = ReporterStates.VoiceTable
            .Where(item => item.FileNames.Count == 0)
            .Select(item => ReporterStates.GetEventDisplayName(item.EventId))
            .ToArray();

        ResourceCountText.Text = $"模板 {templateLoaded}/{ReporterStates.Count} · 语音 {mappedFiles.Length}/{voiceFiles.Length}";
        ResourceSummaryText.Text =
            $"模板负责识别 {ReporterStates.Count} 个画面阶段；语音按事件播放，同一个模板可关联多条语音，所以数量不会一一对应。" +
            $"当前自动接入 {activeEventCount} 条语音事件。";

        var detail = new List<string>
        {
            $"已加载模板：{templateLoaded}/{ReporterStates.Count}",
            $"语音文件：{voiceFiles.Length} 个，已映射 {mappedFiles.Length} 个，未映射 {unboundFiles.Length} 个",
            $"未加载模板：{(missingTemplates.Length == 0 ? "无" : string.Join("、", missingTemplates))}",
            $"旧版或未匹配的模板文件：{(unusedTemplateFiles.Length == 0 ? "无" : string.Join("、", unusedTemplateFiles))}",
            $"未自动触发的动态事件：{(pausedDynamicEvents.Length == 0 ? "无" : string.Join("、", pausedDynamicEvents))}",
            $"无音频占位事件：{(emptyVoiceEvents.Length == 0 ? "无" : string.Join("、", emptyVoiceEvents))}",
            "状态 → 语音事件："
        };

        for (var i = 0; i < ReporterStates.Count; i++)
        {
            var stateId = (StateId)i;
            var eventIds = ReporterStates.GetVoiceEventIds(stateId);
            var activeCount = eventIds.Count(eventId =>
            {
                var definition = ReporterStates.VoiceTable.FirstOrDefault(item => item.EventId == eventId);
                return definition is not null &&
                       !ReporterStates.UnsupportedDynamicEvents.Contains(eventId) &&
                       definition.FileNames.Any(name => File.Exists(Path.Combine(voicesDirectory, name)));
            });
            detail.Add($"  {ReporterStates.GetDisplayName(stateId)} → {activeCount}/{eventIds.Count} 条可用");
        }

        if (unboundFiles.Length > 0)
        {
            detail.Add("未映射语音文件：");
            detail.AddRange(unboundFiles.Select(name => $"  {name}"));
        }

        ResourceDetailText.Text = string.Join(Environment.NewLine, detail);
    }

    private string? FindVoiceFile(VoiceDefinition definition)
    {
        var voicesDirectory = Path.Combine(Path.GetDirectoryName(_runtime.TemplateDirectory)!, "voices");
        return definition.FileNames
            .Select(name => Path.Combine(voicesDirectory, name))
            .FirstOrDefault(File.Exists);
    }

    private void AppendDebug(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        if (DispatcherQueue.HasThreadAccess)
        {
            AppendDebugCore(line);
            return;
        }

        DispatcherQueue.TryEnqueue(() => AppendDebugCore(line));
    }

    private void AppendDebugCore(string line)
    {
        _debugMessages.Add(line);
        while (_debugMessages.Count > 160)
        {
            _debugMessages.RemoveAt(0);
        }

        DebugLogList.ScrollIntoView(_debugMessages[^1]);
    }

    private void SettingsThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingControls || SettingsThresholdText is null)
        {
            return;
        }

        SettingsThresholdText.Text = e.NewValue.ToString("0.000");
        _runtime.Threshold = e.NewValue;
        _settings.MatchThreshold = e.NewValue;
    }

    private void LoadAudioDevices()
    {
        try
        {
            var devices = _runtime.GetAudioDevices();
            AudioDeviceList.ItemsSource = devices;
            var selectedDevice =
                devices.FirstOrDefault(device => device.Id == _settings.AudioDeviceId) ??
                devices.FirstOrDefault(device => device.IsDefault) ??
                devices.FirstOrDefault();
            AudioDeviceList.SelectedItem = selectedDevice;
            _runtime.AudioDeviceId = selectedDevice?.Id;
            AudioVolumeText.Text = $"{(int)Math.Round(AudioVolumeSlider.Value * 100)}%";
            AppendDebug(selectedDevice is null
                ? "未找到可用的音频输出设备。"
                : $"音频输出：{selectedDevice.Name}");
        }
        catch (Exception exception)
        {
            _logger.Error("枚举音频输出设备失败。", exception);
        }
    }

    private void AudioDeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _runtime.AudioDeviceId = (AudioDeviceList.SelectedItem as AudioDeviceInfo)?.Id;
    }

    private void AudioVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }

        _runtime.AudioVolume = (float)e.NewValue;
        if (AudioVolumeText is not null)
        {
            AudioVolumeText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.MatchThreshold = SettingsThresholdSlider.Value;
            _settings.AudioVolume = (float)AudioVolumeSlider.Value;
            _settings.AudioDeviceId = (AudioDeviceList.SelectedItem as AudioDeviceInfo)?.Id;
            _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
            _settings.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;

            _runtime.Threshold = _settings.MatchThreshold;
            _runtime.AudioDeviceId = _settings.AudioDeviceId;
            _runtime.AudioVolume = _settings.AudioVolume;
            StartupRegistration.SetEnabled(_settings.StartWithWindows, Environment.ProcessPath ?? string.Empty);
            await _settingsStore.SaveAsync(_settings);
            await ShowMessageAsync("设置已保存。", "香奈美播报员");
        }
        catch (Exception exception)
        {
            _logger.Error("保存设置失败。", exception);
            await ShowMessageAsync(exception.Message, "保存失败");
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Directory.GetParent(_runtime.TemplateDirectory)?.FullName;
        if (directory is not null)
        {
            OpenPath(directory);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        OpenPath(Path.GetDirectoryName(_logger.LogFile)!);

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void HideToTray()
    {
        _trayIcon.Visible = true;
        AppWindow.Hide();
    }

    private void ShowFromTray()
    {
        _trayIcon.Visible = true;
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private async Task ShowMessageAsync(string message, string title)
    {
        if (RootGrid.XamlRoot is null)
        {
            _logger.Warning($"{title}：{message}");
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "确定",
                XamlRoot = RootGrid.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            _logger.Warning($"显示提示框失败：{exception.Message}");
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}

public sealed class TemplateScoreItem : INotifyPropertyChanged
{
    private double _score = -1;
    private bool _isHit;
    private int _rank;
    private string _rankText = "--";
    private string _scoreText = "  -  ";
    private string _status = "无模板";
    private Brush _statusBrush = new SolidColorBrush(Colors.Gray);

    public TemplateScoreItem(StateId stateId)
    {
        StateId = stateId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StateId StateId { get; }
    public string StateName => ReporterStates.GetDisplayName(StateId);
    public double Score => _score;

    public string RankText
    {
        get => _rankText;
        private set => SetField(ref _rankText, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => SetField(ref _scoreText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    public void SetScore(double score, bool isHit)
    {
        _score = score;
        _isHit = isHit;
        ScoreText = score < 0 ? "  -  " : score.ToString("0.000");
        UpdateStatus();
    }

    public void SetRank(int rank)
    {
        _rank = rank;
        RankText = rank.ToString("00");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_score < 0)
        {
            Status = "无模板";
            StatusBrush = new SolidColorBrush(Colors.Gray);
            return;
        }

        if (_isHit)
        {
            Status = "命中";
            StatusBrush = new SolidColorBrush(Colors.SeaGreen);
            return;
        }

        Status = _rank == 1 ? "最高" : string.Empty;
        StatusBrush = new SolidColorBrush(Colors.Orange);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}




