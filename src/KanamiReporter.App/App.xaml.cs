using System.Threading;
using KanamiReporter.Core;
using KanamiReporter.Windows;
using Microsoft.UI.Xaml;

namespace KanamiReporter.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "KanamiReporter.Desktop.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private bool _shutdownStarted;
    private ReporterRuntime? _runtime;
    private FileLogger? _logger;
    private MainWindow? _window;

    public AppSettings Settings { get; private set; } = new();
    public SettingsStore SettingsStore { get; private set; } = null!;
    public ResourceImporter ResourceImporter { get; private set; } = null!;
    public BuiltInResourcePack BuiltInResources { get; private set; } = null!;
    public GitHubUpdateService UpdateService { get; private set; } = null!;
    public AppPaths Paths { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = InitializeAsync();
    }

    public void Shutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;

        if (_runtime is not null)
        {
            try
            {
                Task.Run(async () => await _runtime.DisposeAsync()).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                _logger?.Error("释放运行时失败。", exception);
            }

            _runtime = null;
        }

        _logger?.Dispose();
        _logger = null;
        UpdateService?.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥锁已被系统回收。
            }
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        try
        {
            Exit();
        }
        catch
        {
            // 应用可能已经在关闭过程中。
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var mutexName = Environment.GetEnvironmentVariable("KANAMI_SINGLE_INSTANCE_NAME") ?? SingleInstanceMutexName;
            _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out _ownsMutex);
            if (!_ownsMutex)
            {
                Exit();
                return;
            }

            Paths = new AppPaths();
            _logger = new FileLogger(Paths.Logs);
            SettingsStore = new SettingsStore(Paths);
            ResourceImporter = new ResourceImporter(Paths);
            BuiltInResources = new BuiltInResourcePack(Paths, _logger);

            try
            {
                var installed = BuiltInResources.EnsureInstalled();
                if (installed.HasChanges)
                {
                    _logger.Info(
                        $"已准备内置资源包：模板 {installed.TemplateCount} 个，语音 {installed.VoiceCount} 个，" +
                        $"保留已有文件 {installed.SkippedCount} 个。");
                }
            }
            catch (Exception exception)
            {
                _logger.Error("准备内置资源包失败。", exception);
            }

            Settings = await SettingsStore.LoadAsync();
            UpdateService = new GitHubUpdateService(
                "https://github.com/qihe114514/Kanami-Reporter-Standalone/releases/latest/download/update.json");

            var frameSource = new WindowsGraphicsCaptureSource();
            var engine = new RecognitionEngine(Paths.Templates);
            var player = new NAudioAnnouncementPlayer();
            _runtime = new ReporterRuntime(Paths, _logger, frameSource, engine, player)
            {
                Threshold = Settings.MatchThreshold,
                AudioDeviceId = Settings.AudioDeviceId,
                AudioVolume = Settings.AudioVolume
            };

            var startHidden = Environment.GetCommandLineArgs()
                .Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));

            _window = new MainWindow(
                _runtime,
                Settings,
                SettingsStore,
                UpdateService,
                _logger,
                startHidden);
            _window.Activate();
        }
        catch (Exception exception)
        {
            _logger?.Error("启动失败。", exception);
            Shutdown();
        }
    }
}

