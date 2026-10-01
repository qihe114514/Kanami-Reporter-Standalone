# Kanami Reporter 独立版

> 本仓库是 [panedioic/Kanami-Reporter](https://github.com/panedioic/Kanami-Reporter) 的独立桌面重制版，保留原仓库的 GPL-2.0-or-later 许可证与识别逻辑来源。

Kanami Reporter 是从原 OBS 视频滤镜重做而来的 Windows 桌面软件。它直接使用
Windows Graphics Capture 读取游戏窗口或显示器，在本机完成模板匹配、状态机判断
和语音播报，不要求安装或启动 OBS，也不会注入游戏进程。

界面已使用 WinUI 3 / Windows App SDK 重做，采用自绘标题栏、Fluent 风格与精简的左侧导航栏。
模板和中文语音资源已内置在程序中，首次启动会自动写入本地数据目录，不再需要
先导入资源包才能使用。

## 当前能力

- 内置模板和语音资源包，首次启动自动准备，无需手动导入。
- 内置资源不会覆盖用户已经保存或替换过的同名模板、语音文件。
- 捕获窗口或整个显示器，窗口采集连续黑屏时自动回退到所在显示器。
- 以 1920×1080 为识别基准，自动缩放 2560×1440 等来源尺寸。
- 兼容原有 KRT v1 模板、中文 MP3 文件名和旧资源 ZIP。
- 支持 15 个原状态名称和原有事件 ID。
- 捕获源列表每 2 秒自动刷新并保留当前选择；运行中也会重新枚举新出现的窗口。
- 支持模板匹配阈值、常驻实时预览、按分数降序的逐模板匹配值和精简调试信息。
- 支持枚举并选择本机音频输出设备、音量调整；同一事件配置多条语音时会随机选择可播放文件。
- 主界面会显示识别推算的回合、阵营、阶段计时、倒计时和下一语音判定，方便核对模板状态。
- 资源状态会显示模板加载数、语音映射数、未映射语音文件以及暂未自动触发的动态事件。
- 支持托盘运行、全局热键 `Ctrl+Alt+R`、开机启动和 GitHub Releases 更新检查。
- 使用 Windows Graphics Capture，不注入、不 Hook、不修改游戏文件。

## 系统要求

- Windows 10 2004（内部版本 19041）或更高版本。
- Windows 11 受支持。
- 仅提供 Windows x64 版本。
- 使用安装器时不需要另行安装 .NET 或 Windows App SDK，应用为自包含发布。

## 使用流程

1. 直接启动程序。首次启动会自动准备内置模板和语音。
2. 在“运行”页选择《三角洲行动》窗口或所在显示器。
3. 点击“开始识别”。主界面会持续刷新实时画面、识别推算数据、倒计时和按分数降序的模板匹配列表。
4. 在“设置”页选择语音输出设备、调整音量，并按需配置开机启动、托盘和更新检查。
5. 展开“识别调试信息”查看帧信息与最近日志；需要替换资源时，可打开数据目录手动放置模板或语音文件。

数据默认保存在：

```text
%LOCALAPPDATA%\KanamiReporter\
├── config.json
├── .builtin-resources-v1   # 内置资源包已准备完成的标记
├── templates\*.krt
├── voices\*.mp3
└── logs\kanami-YYYYMMDD.log
```

内置资源会在首次启动时释放到 `templates` 和 `voices`；如果目标文件已经存在，
则保留用户文件。主界面的“资源状态”会直接列出模板缺口、未映射语音文件和动态事件边界。

## 采集兼容性

Windows Graphics Capture 是系统提供的安全采集 API。在启用了内核级反作弊的游戏
中，窗口采集仍可能返回黑屏；此时程序会尝试回退到所在显示器。若两种方式均被系统
阻止，程序会显示错误并记录日志，不会改用注入或 Hook。

在《三角洲行动》实机中应分别验证：

- 全屏和无边框窗口模式。
- 2560×1440 等目标分辨率下的缩放和 ROI 对齐。
- 多显示器、HDR、DPI 缩放和切换显示器。
- 连续运行至少 2 小时时的帧率、内存和音频稳定性。

可使用采集探针独立检查捕获目标：

```powershell
dotnet run --project tools/KanamiReporter.CaptureProbe -- list
dotnet run --project tools/KanamiReporter.CaptureProbe -- display
dotnet run --project tools/KanamiReporter.CaptureProbe -- window delta
```

## 构建

需要 .NET 8 SDK。WinUI 3 所需的 Windows App SDK 与 Windows SDK BuildTools 会通过
NuGet 还原；发布时 `WindowsAppSDKSelfContained=true`，无需额外安装 Windows App
SDK Runtime。

构建命令：

```powershell
dotnet restore KanamiReporter.sln
dotnet test KanamiReporter.sln
dotnet run --project src/KanamiReporter.App
```

生成自包含发布目录：

```powershell
dotnet publish src/KanamiReporter.App/KanamiReporter.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:Version=1.0.0 `
  -o artifacts/publish/win-x64
```

生成安装器和更新清单：

```powershell
./scripts/Build-Release.ps1 -Version 1.0.0
```

安装器使用 Inno Setup 6，默认按当前用户安装到：

```text
%LOCALAPPDATA%\Programs\KanamiReporter
```

## 已知边界

原 OBS 插件中的比分、玩家击杀数、双方存活人数和炸弹携带者判断是占位实现。
首版没有把占位代码伪装成 OCR 或真实识别，因此以下动态事件会显示为未配置，
但模板驱动的状态和计时事件仍正常工作：

- `event_bomber_down`
- `event_alive_players_us_1`
- `event_alive_players_enemy_1`
- `event_decide_round`
- `event_overtime_round`

首版不提供游戏内悬浮层、不输出到麦克风、不使用游戏 Hook、不做 OCR。
发布包未进行 Authenticode 签名，Windows 可能显示 SmartScreen 提示。

## 许可证

本项目由 GPL-2.0-or-later 代码派生，继续使用 GPL-2.0-or-later。
对应源代码应随公开二进制发布一并提供。

## 图标与第三方素材

应用图标选用《卡拉彼丘》官方香奈美表情包“MVP”。素材来源：

- Wiki 页面：https://wiki.biligame.com/klbq/%E8%A1%A8%E6%83%85%E5%8C%85
- 原图：https://patchwiki.biligame.com/images/klbq/3/31/g1jgpl8e3jqjlr2g12xg0ml98dn14ms.png

该图片版权归原权利方所有，GPL-2.0-or-later 仅覆盖本项目代码，不覆盖该第三方图像素材。
