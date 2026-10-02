using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    /// <summary>分数抖动小于该步长时不改变列表位置，避免分数接近的行反复互换。</summary>
    private const double ReorderBucketSize = 0.01;

    /// <summary>除“第一名换人”和“命中行数量变化”外，列表重排的最小间隔。</summary>
    private const long ReorderIntervalMilliseconds = 1000;

    private const int DebugLogCapacity = 160;
    private const long FrameInfoIntervalMilliseconds = 1000;

    private readonly ReporterRuntime _runtime;
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly GitHubUpdateService _updateService;
    private readonly FileLogger _logger;
    private readonly bool _startHidden;
    private readonly WinForms.NotifyIcon _trayIcon = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly ObservableCollection<string> _debugMessages = new();
    private readonly ObservableCollection<TemplateScoreItem> _templateScores = new();
    private readonly ObservableCollection<ResourceStateItem> _resourceStates = new();

    /// <summary>按状态索引的同一批列表项：分数必须按状态写入，不能按列表位置写入（列表会重排）。</summary>
    private readonly TemplateScoreItem[] _scoreItemsByState = new TemplateScoreItem[ReporterStates.Count];

    private GlobalHotkey? _hotkey;
    private WriteableBitmap? _previewBitmap;
    private DispatcherQueueTimer? _captureRefreshTimer;
    private DispatcherQueueTimer? _scoreRefreshTimer;
    private nint _previousWindowProc;
    private WindowProcDelegate? _windowProc;
    private CaptureTargetDescriptor? _lastSelectedTarget;
    private DetectionResult? _lastDetectionResult;
    private UpdateCheckResult? _availableUpdate;
    private bool _allowClose;
    private bool _updatingControls;
    private bool _isCaptureRunning;
    private bool _captureRefreshInProgress;
    private bool _diagnosticsExpanded;
    private bool _loaded;
    private long _fpsWindowStarted;
    private int _framesInWindow;
    private long _lastReorderTicks;
    private long _lastFrameInfoTicks;
    private int _lastHitCount;

    public MainWindow(
        ReporterRuntime runtime,
        AppSettings settings,
        SettingsStore settingsStore,
        GitHubUpdateService updateService,
        FileLogger logger,
        bool startHidden)
    {
        InitializeComponent();

        _runtime = runtime;
        _settings = settings;
        _settingsStore = settingsStore;
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
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarColors();

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
            ApplyTitleBarColors();
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

    /// <summary>标题栏并入窗口材质后，标题栏按钮颜色需要跟着主题走。</summary>
    private void ApplyTitleBarColors()
    {
        try
        {
            var isDark = RootGrid.ActualTheme != ElementTheme.Light;
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Colors.White : ColorHelper.FromArgb(255, 26, 26, 26);
            titleBar.ButtonInactiveForegroundColor = isDark
                ? ColorHelper.FromArgb(140, 255, 255, 255)
                : ColorHelper.FromArgb(140, 0, 0, 0);
            titleBar.ButtonHoverBackgroundColor = isDark
                ? ColorHelper.FromArgb(36, 255, 255, 255)
                : ColorHelper.FromArgb(20, 0, 0, 0);
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedBackgroundColor = isDark
                ? ColorHelper.FromArgb(56, 255, 255, 255)
                : ColorHelper.FromArgb(32, 0, 0, 0);
            titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        }
        catch (Exception exception)
        {
            _logger.Warning($"设置标题栏颜色失败：{exception.Message}");
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        ApplyTitleBarColors();
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
        await RefreshResourceStatusAsync();
        ShowStage(running: false);
        StartCaptureAutoRefresh();
        UpdateCaptureControls();

        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            InstallShutdownHook(windowHandle);
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
        DebugLogRepeater.ItemsSource = _debugMessages;
        TemplateScoreRepeater.ItemsSource = _templateScores;
        ResourceStateRepeater.ItemsSource = _resourceStates;

        for (var i = 0; i < ReporterStates.Count; i++)
        {
            var stateId = (StateId)i;
            var item = new TemplateScoreItem(stateId);
            item.SetTemplateAvailability(_runtime.GetTemplate(stateId) is not null);
            _scoreItemsByState[i] = item;
            _templateScores.Add(item);
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
        UpdateMatchSummary();
        _updatingControls = false;
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("开始 / 停止识别", null, async (_, _) => await ToggleRecognitionAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon.Text = "香奈美x黑潮爆破";
        // 托盘图标用应用自身的图标；Assets 缺失时退回从 exe 提取，再不行才是系统占位图标。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            _trayIcon.Icon = new System.Drawing.Icon(iconPath);
        }
        else
        {
            try
            {
                _trayIcon.Icon = string.IsNullOrWhiteSpace(Environment.ProcessPath)
                    ? System.Drawing.SystemIcons.Application
                    : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch (Exception exception)
            {
                _logger.Warning($"提取托盘图标失败：{exception.Message}");
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }

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
        // 识别进行中不允许切换捕获源，也没有必要每隔两秒重新枚举一次窗口。
        if (_captureRefreshInProgress || _isCaptureRunning)
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

            // SelectedItem 必须指向当前 ItemsSource 里的实例，否则下拉框会丢掉当前选择。
            var items = CaptureTargets.ItemsSource as IReadOnlyList<CaptureTargetDescriptor> ?? targets;
            var selected = items.FirstOrDefault(target => target.Id == previousTarget?.Id)
                ?? items.FirstOrDefault(target => target.Id == _settings.LastCaptureTargetId)
                ?? items.FirstOrDefault(target =>
                    !string.IsNullOrWhiteSpace(_settings.LastCaptureProcessName) &&
                    string.Equals(target.ProcessName, _settings.LastCaptureProcessName, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault();

            if (!ReferenceEquals(CaptureTargets.SelectedItem, selected))
            {
                CaptureTargets.SelectedItem = selected;
            }

            _lastSelectedTarget = selected;
            ToolTipService.SetToolTip(
                CaptureTargets,
                targets.Count == 0
                    ? "没有找到可捕获的窗口或显示器。"
                    : $"已发现 {targets.Count} 个捕获源；列表每 2 秒自动刷新。");

            if (targets.Count == 0)
            {
                SetCaptureHint("没有找到可捕获的窗口或显示器。请先启动游戏，再点“立即刷新”。", isProblem: true);
            }
            else
            {
                SetCaptureHint(string.Empty, isProblem: false);
            }

            if (!_isCaptureRunning && _lastDetectionResult is null)
            {
                SetText(TitleSourceText, selected?.DisplayName ?? "未选择捕获源");
            }
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
            await ShowMessageAsync("请先选择窗口或显示器。", "香奈美x黑潮爆破");
            return;
        }

        try
        {
            _settings.LastCaptureTargetId = target.Id;
            _settings.LastCaptureProcessName = target.ProcessName;

            // 先切到运行态给出即时反馈，捕获真正开始后由状态事件更新文案。
            _isCaptureRunning = true;
            _lastDetectionResult = null;
            UpdateCaptureControls();
            ShowStage(running: true);
            SetText(PreviewStatusText, "正在连接捕获源…");
            SetText(DebugSourceText, $"捕获源：{target.DisplayName}");

            await _runtime.StartAsync(target);
            await _settingsStore.SaveAsync(_settings);

            SetText(TitleSourceText, target.DisplayName);
            SetCaptureHint(string.Empty, isProblem: false);
            AppendDebug($"开始识别：{target.DisplayName}");
        }
        catch (Exception exception)
        {
            _logger.Error("开始识别失败。", exception);
            _isCaptureRunning = false;
            UpdateCaptureControls();
            ShowStage(running: false);

            // 失败常常是因为选中的窗口已经关闭或重建：立刻重扫一次捕获源，
            // 把失效项从下拉框里换掉，否则用户再点一次还是一模一样的报错。
            await ReloadTargetsAsync(showErrors: false);
            await ShowMessageAsync(exception.Message, "无法开始采集");
        }
    }

    private async Task StopRecognitionAsync()
    {
        try
        {
            await _runtime.StopAsync();
            _isCaptureRunning = false;
            _lastDetectionResult = null;
            UpdateCaptureControls();
            ShowStage(running: false);
            ResetTemplateScores();
            ResetObservation();

            SetText(TitleSourceText, _lastSelectedTarget?.DisplayName ?? "未选择捕获源");
            SetText(DebugSourceText, "捕获源：未开始");
            SetText(DebugFrameInfoText, "当前帧：等待首帧");
            SetText(PreviewFooterMetaText, string.Empty);
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
        RefreshTargetsButton.IsEnabled = !_isCaptureRunning;
        StartRecognitionButton.IsEnabled = !_isCaptureRunning;
        EmptyStateStartButton.IsEnabled = !_isCaptureRunning;
        StopRecognitionButton.IsEnabled = _isCaptureRunning;

        SetText(TitleStatusText, _isCaptureRunning ? "识别中" : "待机");
        SetVisible(TitleStatusDotRunning, _isCaptureRunning);
        SetVisible(TitleStatusDotIdle, !_isCaptureRunning);
    }

    /// <summary>空闲与运行两种舞台状态：空闲使用跟随主题的底色，运行使用固定的深色视频底。</summary>
    private void ShowStage(bool running)
    {
        SetVisible(StageIdleSurface, !running);
        SetVisible(StageRunningSurface, running);
        SetVisible(PreviewEmptyState, !running);
        SetVisible(FpsBadge, running);
        SetVisible(PreviewStatusBadge, running);
        SetVisible(RunPreviewImage, running);

        if (!running)
        {
            _previewBitmap = null;
            RunPreviewImage.Source = null;
        }
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

        var now = Environment.TickCount64;
        if (now - _lastFrameInfoTicks >= FrameInfoIntervalMilliseconds)
        {
            _lastFrameInfoTicks = now;
            SetText(PreviewFooterMetaText, $"{frame.Width}×{frame.Height}");
            SetText(
                DebugFrameInfoText,
                $"当前帧：{frame.Width}×{frame.Height}  时间 {frame.Timestamp.TotalSeconds:0.0} 秒");
        }
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
        DispatcherQueue.TryEnqueue(() => SetText(FpsText, $"{fps:0.0} 帧/秒"));
    }

    private void Runtime_StatusChanged(object? sender, CaptureStatus status)
    {
        // 采集层的告警（画面中断、已回退到显示器）只有写进日志和界面提示才有人看得见。
        if (status.Error is not null || status.IsWarning)
        {
            _logger.Warning(status.Message);
        }
        else
        {
            _logger.Info(status.Message);
        }

        if (!status.IsRunning)
        {
            _isCaptureRunning = false;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCaptureControls();

            // 回退和告警都带着用户需要知道的信息（例如"已回退到显示器"），
            // 不能像普通运行状态那样只写日志、把提示行清空。
            if (status.Error is not null || status.UsedFallback || status.IsWarning)
            {
                SetCaptureHint(status.Message, isProblem: true);
            }
            else
            {
                SetCaptureHint(string.Empty, isProblem: false);
            }

            if (status.IsRunning)
            {
                SetText(PreviewStatusText, "实时预览");
            }
            else if (!_isCaptureRunning && status.Error is not null)
            {
                ShowStage(running: false);
            }

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
        SetText(
            CurrentStateText,
            result.StateId is { } stateId ? ReporterStates.GetDisplayName(stateId) : "未知");
        SetVisible(StateActiveDot, result.StateId is not null);

        SetText(
            DebugStateDurationText,
            $"阶段计时：{result.StateDuration.TotalSeconds:0.0} 秒");
        SetText(
            GameCountdownText,
            result.EstimatedPhaseRemaining is { } remaining ? FormatCountdown(remaining) : "--:--");
        SetText(
            GamePhaseText,
            string.IsNullOrWhiteSpace(result.PhaseTimingLabel) ? "实时识别" : result.PhaseTimingLabel);
        SetText(GameRoundText, result.RoundNumber > 0 ? $"第 {result.RoundNumber} 回合" : "回合 --");
        SetText(
            GameSideText,
            result.Side switch
            {
                1 => "阵营：进攻方",
                2 => "阵营：防守方",
                _ => "阵营：未知"
            });
        SetText(GameEventText, BuildNextEventText(result));
    }

    private void ResetObservation()
    {
        SetText(CurrentStateText, "未知");
        SetVisible(StateActiveDot, false);
        SetText(GamePhaseText, "等待识别");
        SetText(GameCountdownText, "--:--");
        SetText(GameRoundText, "回合 --");
        SetText(GameSideText, "阵营：未知");
        SetText(DebugStateDurationText, "阶段计时：0 秒");
        SetText(GameEventText, "等待识别数据");
        SetText(MatchSummaryText, $"当前 — · 阈值 {_runtime.Threshold:0.000}");
    }

    private static string FormatCountdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        // 识别每 100 毫秒刷新一次，保留一位小数才能跟着刷新走，不会一秒一跳。
        return $"{value.TotalSeconds:0.0} 秒";
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

        var threshold = _runtime.Threshold;
        for (var i = 0; i < ReporterStates.Count; i++)
        {
            var score = result.Scores[i];
            _scoreItemsByState[i].SetScore(score, score >= 0 && score >= threshold);
        }

        ApplyStableOrder();

        for (var i = 0; i < _templateScores.Count; i++)
        {
            _templateScores[i].SetRank(i + 1);
        }

        UpdateMatchSummary();
    }

    /// <summary>
    /// 命中的行始终排在最前面，其余按分数分桶排序：分数差不足一档的两行保持现有先后次序，
    /// 且重排最多每秒一次（命中数量变化或第一名换人例外）。分数逐帧抖动时列表因此基本静止。
    /// </summary>
    private void ApplyStableOrder()
    {
        var desired = _templateScores
            .Select((item, index) => (item, index))
            .OrderByDescending(pair => pair.item.IsHit)
            .ThenByDescending(pair => ScoreBucket(pair.item))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToArray();

        var changed = false;
        for (var i = 0; i < desired.Length; i++)
        {
            if (!ReferenceEquals(desired[i], _templateScores[i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            _lastHitCount = _templateScores.Count(item => item.IsHit);
            return;
        }

        var topChanged = !ReferenceEquals(desired[0], _templateScores[0]);
        var hitCountChanged = _templateScores.Count(item => item.IsHit) != _lastHitCount;
        if (!topChanged && !hitCountChanged &&
            Environment.TickCount64 - _lastReorderTicks < ReorderIntervalMilliseconds)
        {
            return;
        }

        for (var target = 0; target < desired.Length; target++)
        {
            var current = _templateScores.IndexOf(desired[target]);
            if (current != target)
            {
                _templateScores.Move(current, target);
            }
        }

        _lastHitCount = _templateScores.Count(item => item.IsHit);
        _lastReorderTicks = Environment.TickCount64;
    }

    private static int ScoreBucket(TemplateScoreItem item) =>
        item.Score < 0 ? int.MinValue : (int)Math.Round(item.Score / ReorderBucketSize);

    private void ResetTemplateScores()
    {
        foreach (var item in _templateScores)
        {
            item.Reset();
        }

        var desired = _templateScores.OrderBy(item => (int)item.StateId).ToArray();
        for (var target = 0; target < desired.Length; target++)
        {
            var current = _templateScores.IndexOf(desired[target]);
            if (current != target)
            {
                _templateScores.Move(current, target);
            }
        }

        for (var i = 0; i < _templateScores.Count; i++)
        {
            _templateScores[i].SetRank(i + 1);
        }

        _lastReorderTicks = 0;
        _lastHitCount = 0;
        SetText(MatchSummaryText, $"当前 — · 阈值 {_runtime.Threshold:0.000}");
    }

    private void UpdateMatchSummary()
    {
        var score = _lastDetectionResult?.BestScore ?? -1;
        SetText(
            MatchSummaryText,
            $"当前 {(score < 0 ? "—" : score.ToString("0.000"))} · 阈值 {_runtime.Threshold:0.000}");
    }

    private void Runtime_AnnouncementTriggered(object? sender, string eventId)
    {
        var displayName = ReporterStates.GetEventDisplayName(eventId);
        _logger.Info($"触发事件：{displayName} ({eventId})");
        DispatcherQueue.TryEnqueue(() =>
        {
            SetText(VoicePlaybackText, $"语音：准备播放 {displayName}");
            SetText(LastAnnouncementText, $"最近播报：{displayName} · {DateTime.Now:HH:mm:ss}");
            AppendDebug($"触发事件：{displayName}");
        });
    }

    private void Runtime_AnnouncementPlaybackChanged(object? sender, AnnouncementPlaybackStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var text = status.IsPlaying
                ? $"语音：正在播放 {status.FileName}"
                : status.IsCompleted
                    ? $"语音：播放完成 {status.FileName}"
                    : $"语音：播放失败 {status.FileName}";
            SetText(VoicePlaybackText, text);
            AppendDebug(status.Error is null ? text : $"语音失败：{status.Error.Message}");
        });
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
        while (_debugMessages.Count > DebugLogCapacity)
        {
            _debugMessages.RemoveAt(0);
        }

        if (_diagnosticsExpanded)
        {
            ScrollLogToEnd();
        }
    }

    private void ScrollLogToEnd() =>
        DispatcherQueue.TryEnqueue(() => DebugLogScroll.ChangeView(null, DebugLogScroll.ScrollableHeight, null));

    private void SettingsThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingControls || SettingsThresholdText is null)
        {
            return;
        }

        SettingsThresholdText.Text = e.NewValue.ToString("0.000");
        _runtime.Threshold = e.NewValue;
        _settings.MatchThreshold = e.NewValue;
        UpdateMatchSummary();
    }

    private void LoadAudioDevices()
    {
        try
        {
            var devices = _runtime.GetAudioDevices();
            var choices = new List<AudioDeviceInfo>(devices.Count + 1) { AudioDeviceInfo.FollowSystem };
            choices.AddRange(devices);
            AudioDeviceList.ItemsSource = choices;

            // 设备被拔出或配置为空时回到“跟随系统输出”，而不是固定到当时碰巧在用的设备。
            var selectedDevice =
                choices.FirstOrDefault(device => device.PlaybackDeviceId == _settings.AudioDeviceId) ??
                AudioDeviceInfo.FollowSystem;
            AudioDeviceList.SelectedItem = selectedDevice;
            _runtime.AudioDeviceId = selectedDevice.PlaybackDeviceId;
            AudioVolumeText.Text = $"{(int)Math.Round(AudioVolumeSlider.Value * 100)}%";
            AppendDebug(DescribeAudioOutput(selectedDevice, devices));
        }
        catch (Exception exception)
        {
            _logger.Error("枚举音频输出设备失败。", exception);
        }
    }

    private static string DescribeAudioOutput(AudioDeviceInfo selected, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (!selected.IsFollowSystem)
        {
            return $"音频输出：{selected.Name}";
        }

        var current = devices.FirstOrDefault(device => device.IsDefault)?.Name;
        return current is null
            ? "音频输出：跟随系统输出（当前未检测到可用设备）。"
            : $"音频输出：跟随系统输出（当前：{current}）";
    }

    private void AudioDeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _runtime.AudioDeviceId = (AudioDeviceList.SelectedItem as AudioDeviceInfo)?.PlaybackDeviceId;
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
            _settings.AudioDeviceId = (AudioDeviceList.SelectedItem as AudioDeviceInfo)?.PlaybackDeviceId;
            _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
            _settings.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;

            _runtime.Threshold = _settings.MatchThreshold;
            _runtime.AudioDeviceId = _settings.AudioDeviceId;
            _runtime.AudioVolume = _settings.AudioVolume;
            StartupRegistration.SetEnabled(_settings.StartWithWindows, Environment.ProcessPath ?? string.Empty);
            await _settingsStore.SaveAsync(_settings);
            await ShowMessageAsync("设置已保存。", "香奈美x黑潮爆破");
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

    /// <summary>写入文本只在内容真正变化时进行：观测面板每秒会刷新十次。</summary>
    private static void SetText(TextBlock target, string text)
    {
        if (!string.Equals(target.Text, text, StringComparison.Ordinal))
        {
            target.Text = text;
        }
    }

    private static void SetVisible(UIElement element, bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != value)
        {
            element.Visibility = value;
        }
    }

    /// <summary>常规情况不占用界面空间，只有需要用户注意时才显示这一行。</summary>
    private void SetCaptureHint(string text, bool isProblem)
    {
        SetText(CaptureHintText, text);
        SetVisible(CaptureHintText, isProblem && !string.IsNullOrWhiteSpace(text));
    }

    /// <summary>
    /// 接管窗口过程，专门处理"系统要求关闭"。
    /// 默认设置是关闭窗口时最小化到托盘，如果连系统或安装程序的关闭请求也被当成"最小化到托盘"，
    /// 进程就永远不会退出——安装程序会一直停在"正在关闭应用程序"（实测就是这样卡住的）。
    /// </summary>
    private void InstallShutdownHook(nint windowHandle)
    {
        _windowProc = WindowProc;
        _previousWindowProc = SetWindowLongPtr(
            windowHandle,
            WindowProcIndex,
            Marshal.GetFunctionPointerForDelegate(_windowProc));

        if (_previousWindowProc == nint.Zero)
        {
            _windowProc = null;
            _logger.Warning("接管窗口过程失败：系统关闭请求可能无法正常退出。");
        }
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message is WindowMessageQueryEndSession or WindowMessageEndSession)
        {
            // 关掉"最小化到托盘"，让随后到来的关闭请求真的把程序关掉。
            _allowClose = true;
            _logger.Info($"收到系统关闭请求（0x{message:X}），准备退出。");

            if (message == WindowMessageQueryEndSession)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Close();
                    }
                    catch (Exception exception)
                    {
                        _logger.Warning($"响应系统关闭请求失败：{exception.Message}");
                    }
                });
            }

            return 1;
        }

        return CallWindowProc(_previousWindowProc, window, message, wParam, lParam);
    }

    private static nint SetWindowLongPtr(nint window, int index, nint value) =>
        nint.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : SetWindowLong32(window, index, (int)value);

    private delegate nint WindowProcDelegate(nint window, uint message, nint wParam, nint lParam);

    private const int WindowProcIndex = -4;
    private const uint WindowMessageQueryEndSession = 0x0011;
    private const uint WindowMessageEndSession = 0x0016;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previous, nint window, uint message, nint wParam, nint lParam);
}
