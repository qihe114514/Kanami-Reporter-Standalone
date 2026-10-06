# 香奈美x黑潮爆破（Android 版）

Kanami-Reporter 的安卓版：对《三角洲行动》手游「竞技爆破」（黑潮爆破）对局画面做
模板匹配 + 状态机判断，按阶段自动播报香奈美语音，并在游戏画面上显示实时悬浮窗。
UI 采用 [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)
（Backdrop，`io.github.kyant0:backdrop:2.0.1`）打造液态玻璃风格。

模板与适配依据见 `Kanami-Reporter-Standalone/docs/mobile-adaptation.md`
（基于 2026-10-05 手机实机完整对局录屏裁剪并验证）。

## 工作原理

```
MediaProjection 屏幕采集（面板真实尺寸 + 当前朝向，RGBA）
      │  ImageReader，约 15 fps，丢帧保最新
      │  非横屏帧不做匹配（只用于画面活性判断）
      ▼
FrameProcessing.normalize   按屏幕高度等比缩放、水平居中、顶对齐（内容高度恒为 887，见下）
      ▼
FrameProcessing.score       固定 ROI ZNCC + 13 个整数/半像素偏移（与 PC 端一致）
FrameProcessing.scoreBinary 比分数字专用：二值形状相关（灰度相关区分度不够，见下）
      ▼
ReporterStateMachine        状态机（手机版优先级规则：回合结束 > 购买 > 炸弹 > 开局计时）
      ▼
VoicePlayer                 assets/voices 中文 MP3 播报
      ▼
StatusHub (StateFlow)       界面 / 悬浮窗统一取数
```

- 模板：`app/src/main/assets/templates/*.mobile.krt`（12 个状态模板 + 2 个阵营辅助 + 51 个比分数字，共 65 个）
- 语音：`app/src/main/assets/voices/*.mp3`（30 条，与 PC 端同源）
- 阵营：购买横幅副标题「攻方：安放炸弹 / 守方：歼灭敌军」逐回合校正，翻转即播「攻守互换」

### 采集几何（v1.1.0 修复的关键点）

v1.0.0 用 `resources.displayMetrics` 加 `maxOf/minOf` 强凑横屏建虚拟屏，且全程没有旋转/尺寸
变化处理。虚拟屏尺寸与实际被镜像屏幕不一致时，系统会给镜像内容**加黑边或缩放**，整幅 HUD
平移/缩放几十像素，而 ZNCC 只搜 ±1 像素 → 所有模板分数一起崩、状态机永不命中。
这就是"手机版完全识别不到对局数据"的根因。

现在：用 `Display.getRealMetrics` 取面板真实尺寸；`onCapturedContentResize`（Android 14+）、
`DisplayListener`（低版本）、`onConfigurationChanged` 任一触发就重建采集面；采集异常会显示在界面上
（原来只写 logcat）。

### 分辨率适配：归一化的不变量是内容高度（v1.2.4 修复的关键点）

游戏 HUD 的屏幕坐标只随屏幕**高度**等比变化，与宽度、宽高比无关 —— 这是第二台实机
（2376×1080，22:9，2026-10-06 录屏）与参考机（2772×1280，19.5:9）同一元素对比实测出来的：
两台上同一个元素的屏幕 y 之比恰为 1080/1280，屏幕 x 到屏幕中心的距离之比也是 1080/1280。

所以归一化**不能**按宽度缩放到 1920：宽高比一变，HUD 在归一化坐标里就被纵向压扁
（19.5:9 → 22:9 压 1.6%）。实测下来，计时数字这种小模板还扛得住（0.93+），
宽文字横幅整体掉到 0.6~0.7，阈值 0.90 下完全不命中——购买阶段、回合获胜/战败、
选人画面、对局结算全部失效。

现在 `FrameProcessing.normalize` 改成：

- 按**屏幕高度**等比缩放，内容高度恒为 887（= 参考机 1280×1920/2772 四舍五入）；
- **水平按屏幕中心对齐**（模板覆盖的 HUD 元素都是居中锚定的），比参考机更宽/更窄的屏幕两侧补黑；
- 因为 887 就是参考机在旧"按宽度归一化"下的内容高度，**参考机上的结果与旧实现逐像素一致**，
  既有模板一个都不用重切。

第二台机上 12 个状态模板的命中分：购买阶段 0.98、回合获胜 0.94、回合战败 0.94、
选人 0.98、结算 1.00、计时数字 0.95+、炸弹面板 0.95、比分数字 0.99+。
结算页的「胜利」在两台机上存在整体右移 15px 的布局差异（属边缘锚定，不是等比缩放），
另存了一份机型变体 `game_end_win.wide.mobile.krt`，引擎取最高分，两台机各自命中。

阵营副标题（购买横幅的「攻方：安放炸弹 / 守方：歼灭敌军」）模板 ROI 里混着半透明横幅背景，
分数会随场景亮度整体漂移，所以不沿用状态模板的 0.90 阈值：改用一个宽松下限
`ReporterStates.SideTemplateFloor = 0.60`，胜负交给 `ReporterStateMachine.SideScoreMargin`
做相对比较。实测横幅不可见时两侧最高只有 0.44，可见时正确一侧 0.69~0.80、错误一侧 ≤0.54，
整段 979 帧里 8 个购买窗口全部判对、窗口外零误判。

### 比分读取

比分数字是白字深底、位置稳定，但**灰度 ZNCC 区分度不足**（同一数字跨帧 0.97，而 0 与 3/6 之间
也能到 0.77~0.80）。因此比分用**二值形状相关**（两侧都按亮度二值化后再算相关，异数字降到
0.54~0.86），每个数字备 3~4 个不同背景的实例取最高分，再过一遍"连续 3 帧一致才更新"的稳定器。
离线复算（对 2026-10-05 录屏的 1456 帧）：对方比分 100% 正确，我方 95%+（12 个人工核对点全部命中）。
读不到时界面回退显示按回合结算累计的**估算比分**，不会留空。

## 界面

- **图标**：香奈美「MVP」表情包，与 Windows 桌面版同一张（自适应图标，前景留 6% 边距）
- **背景**：香奈美竖屏壁纸（`assets/background/kanami_bg.webp`）
- **底栏**：液态玻璃底栏，只占屏幕中间约七成宽，附轻震动反馈。**交互是重写过的**，见下
- **内容区**：占满整屏，卡片可以一路滚到屏幕最顶/最底（穿过标题条与底栏，在那里被渐进模糊
  渐隐），而不是被截断在"标题下方到 底栏上方"这段中间区域里。首尾用 Spacer 让出标题条与
  底栏的位置，所以静止时的观感和以前一致
- **运行页**：一张主卡包办「识别状态 + 回合/阵营/比分 + 最近播报 + 开始/停止」——
  打开就能看见主操作，不用滚动；权限清单全开时收成一行、缺项自动展开；
  模板匹配值默认收起。下面两张卡按需展开
- **设置页**：匹配阈值（官方 `LiquidSlider`）、悬浮窗开关（官方 `LiquidToggle`）、
  **个性化**（液态玻璃的反射强度 / 模糊强度，两个 `LiquidSlider`，0～2.2 倍，实时生效）、
  权限清单、使用说明（连点 5 次进调试模式）、调试卡、关于
- **自适应对比度**：`ui/GlassAppearance.kt` 的 `AdaptiveGlass` 从壁纸取上/中/下三段亮度，
  背景偏亮时把 [StatusColors] 整体切到深色一套，亮壁纸下文字不再糊。因为背景是静态壁纸，
  只在加载时算一次，**运行时零开销** —— 官方示例是每帧把 GPU 图层读回 CPU 取平均亮度，
  那是 demo 写法，1272×2772 的图层一秒读几十次在真机上会直接拖垮帧率，不能照搬

界面上所有可点控件都用官方示例组件，按压反馈一致（按触摸点挤压玻璃 + 径向高亮 + 弹回）：
大按钮/小按钮是 `LiquidButton`，滑杆是 `LiquidSlider`，开关是 `LiquidToggle` ——
不要再往里加自绘的按钮/滑杆/开关。

### 底栏：官方实现 + 一处索引同步修正

底栏是官方示例组件原样搬过来的（胶囊玻璃 + 滑动透镜指示器 + **拖动时整条底栏跟着轻微平移** +
按住时按触摸点挤压并泛径向高光），只改了一处：

官方写的是 `remember(selectedTabIndex)` —— 那个 lambda 每次重组都是新实例，会把内部 `currentIndex`
连同它和指示器动画的同步一起重建。症状是**页面切了、底栏指示器不动**，而且停在旧位置的指示器
盖住了那一格的点击，看起来像"点不动"。

修的时候踩了个更深的坑：把 `selectedTabIndex` 保持成 `() -> Int`、只在 effect 里读它是不够的 ——
**Compose 会把"只捕获稳定值的 lambda"记忆化**，`{ tab }` 每次重组都是同一个实例，于是整个
`LiquidBottomTabs` 被判为"参数没变"而**跳过重组**，任何写在里面的 `LaunchedEffect`／组合期读值
都不会被拉起。所以这里的参数改成了 `Int` **值**（`selectedTabIndex: Int`），传值才会真正触发重组；
再用 `LaunchedEffect(值)` 驱动指示器动画。拖动结束则直接回调 `onTabSelected`。

### 屏幕边缘的渐进式模糊

屏幕最顶部和最底部各叠一条模糊带（`ui/ProgressiveBlurEdge.kt`，高度取状态栏/导航条高度），
盖在系统栏区域上、向下渐隐。做法照抄官方示例 `destinations/ProgressiveBlurContent.kt` ——
先 `blur`，再用 `runtimeShaderEffect` 按纵向坐标做 alpha mask。它是**纯 overlay，不参与布局**，
卡片位置完全不受影响。API 33 以下没有 RuntimeShader，退化成一块纯模糊。

### 其它界面约定

- 应用**锁竖屏**（识别针对的是横屏游戏，本界面不需要跟着系统转）
- 两个页面之间用 `Crossfade`（260ms）过渡，不硬切
- 「必须权限」「模板匹配值」折叠卡片的展开/收起有 `AnimatedVisibility` 动画

## 竖屏 / 横屏：应用内"暂不匹配"是正常的

只对横屏帧做模板匹配（手机 HUD 是横屏布局）。**在应用内点「开始识别」时手机还是竖屏，
这是正常的**：采集会立刻跑起来，只是不做匹配。切回游戏（横屏）后，采集面会按新尺寸自动重建，
自动开始识别，**不需要重新授权**。三条触发路径都接了：`onCapturedContentResize`（Android 14+）、
`DisplayListener.onDisplayChanged`、`Service.onConfigurationChanged`。

为了让这件事在界面上说得明白：

- 竖屏期间状态是「采集运行中 · 切回游戏后自动识别」（不再写成"暂不匹配"那种像故障的话），
  状态卡里同时显示「已采集 N 帧」，可以确认采集真的在跑
- **完全不做"画面有没有在动"这类健康度检测**（2026-10-06 用户要求彻底去掉）。
  游戏卡住、加载中、停在桌面、看菜单，画面本来就不动 —— 不算故障；
  而且 **MediaProjection 只在屏幕内容发生变化时才投递帧**，画面静止时"收不到帧"同样是正常的。
  实测：在应用内点「开始识别」后只收到 1 帧就再也没有了，那是因为屏幕没变，不是采集断了。
  所以界面上只报「已采集 N 帧」，不报帧率、也不报"停摆"。真正需要知道的异常只有
  `capture.error`（采集抛异常）和录屏授权被系统回收（`MediaProjection.Callback.onStop`）

## 采集启动链路全程可见

点「开始识别」后每一步都有反馈，失败也一样：

- 授权被取消 / 服务启动失败 / 没拿到授权数据 / 系统没返回录屏会话 / 采集面创建失败
  → 运行页状态卡直接显示原因（红色警示色），并写进日志
- 成功 → 显示"采集已启动；悬浮窗已显示…"，之后状态卡持续显示已采集帧数

授权结果不再靠 Intent extra 传递：Android 13+ 上 `Intent.getParcelableExtra(name)` 取 `Intent`
类型的 extra 在部分系统版本上会返回 null，服务于是拿到空授权、静默 `stopSelf` ——
表现正好是「授权完什么都没发生、也没有悬浮窗」。现在走同进程的 `ProjectionHandoff`，
Intent extra 只作兜底。

### 启动顺序：两个方向的校验互相咬合，只有一个顺序能过

Android 14+ 对这条链路有**两个相反方向**的校验（2026-10-06 两个方向都实测撞过）：

| 调用 | 它检查什么 |
| --- | --- |
| `startForeground(type = mediaProjection)` | 调用方**已经**持有录屏授权（appop `PROJECT_MEDIA`）。缺失时报 `Starting FGS with type mediaProjection ... requires permissions [...]` |
| `getMediaProjection()` | 调用方**已经**有该类型的前台服务在跑。缺失时报 `Media projections require a foreground service of type ... MEDIA_PROJECTION` |

`PROJECT_MEDIA` 是用户在录屏授权弹窗里点「开始录制」时授予的，不需要先调
`getMediaProjection`，所以**授权完立刻 `startForeground` 是通的**。唯一可行顺序：

```
startForeground(NOTIFICATION_ID, notif, TYPE_MEDIA_PROJECTION)
    → getMediaProjection(resultCode, resultData)
    → registerCallback(...)
    → createVirtualDisplay(...)
```

5 秒死线不冲突：所有失败分支都在超时之前 `stopSelf()`，系统不会判 ANR。

服务用 `START_NOT_STICKY`：录屏授权不能跨进程恢复，进程被杀后系统拉起来的服务一定过不了
`startForeground`（日志里那条 `Starting FGS ... requires permissions` 就是系统重启服务的结果），
只会白写一条失败日志。

## 悬浮窗

由识别服务托管：点「开始识别」出现，点「停止」移除，设置里可整体关闭。

- **主悬浮窗**：收起时是 48dp 半透明小胶囊（绿=识别中/黄=采集异常/灰=未识别），
  展开显示状态名、回合/阵营/估算计时、最近播报、采集尺寸与帧率。可拖动（位置会记住），点一下收起/展开。
- **调试悬浮窗**（调试模式开启时叠加）：比分（读屏/估算）、攻守、估算计时、模板匹配值排序（前 8）、
  采集尺寸、当前前台应用。
- 两个窗口都用 `FLAG_SECURE`：悬浮窗会出现在被采集的画面里，安全标志保证它只贡献一块黑、不把内容
  录进去；默认位置贴在左侧、避开顶部中央的匹配区，你可以随时拖走。

## 权限（为什么需要）

进应用即请求，运行页与设置页都有清单，逐项可跳转授权：

| 权限 | 用途 |
| --- | --- |
| 悬浮窗 | 在游戏画面上显示实时状态（事件、识别是否正常、比分） |
| 后台驻留 | 电池优化白名单 + 厂商自启动管理页（小米/华为/荣耀/OPPO/vivo/魅族/三星直达）。进程被杀后必须重新授权录屏，游戏中弹授权框非常影响体验 |
| 无障碍 | **只**用于判断当前前台应用 + 提升进程存活率；`canRetrieveWindowContent=false`，不读取也无法读取屏幕内容，画面完全来自你手动授权的录屏 |
| 通知 | 前台识别服务的常驻通知（Android 要求采集时必须显示） |
| 屏幕录制 | 点「开始识别」时由系统弹窗授权（MediaProjection） |

## 调试模式

设置页「使用说明」标题**连点 5 次**开启（第 3 次起会提示还差几次）。开启后：

- **未捕获异常**（含崩溃栈）也会写进同一份日志，前缀 `[crash]` —— 实机上的"点一下就闪退"
  以前只能靠猜崩溃前最后一条日志落在哪个区间，现在能直接看到类型和行号
- 日志写进应用私有目录 `files/logs/kanami-<YYYYMMDD>.log`：设备与面板信息、每次采集几何重建、
  节流后的帧行、前 8 模板分数、每次状态迁移与语音、冻结进出、全部异常、前台应用变化。
  **按自然日续写**（同一天多次启动追加到同一个文件），这样"出问题 → 重启 → 导出"拿到的仍然是
  完整现场；单文件超过 8MB 才顺延成 `-2`、`-3`
- 日志卡里可查看尾部日志、分享（文本）、**导出到下载目录**（Android 11+ 文件管理器进不去
  `Android/data`，这是取日志的通道）、清空日志
- 「保存当前帧」会把下一帧写成两张 PNG 到 `files/debug/`：原始采集帧 + **归一化帧**
  （与模板同坐标系，判断"模板为什么没命中"最直接的证据）
- 额外显示调试悬浮窗

## 构建与验证

```bash
# 项目路径含中文时 AGP 会拒绝构建，gradle.properties 里已加 android.overridePathCheck=true
JAVA_HOME=<JDK17+> ./gradlew :app:assembleDebug     # 或 assembleRelease（用 keystore/ 下签名）
```

- minSdk 29 / targetSdk 36 / AGP 9.3.2 / Kotlin 2.4.10 / Gradle 9.7.1
- 离线回归（复刻识别管线，不依赖设备）：
  - `tools/verification/scan_final.py` → 对录屏逐帧复算状态模板分数（`timeline_final.txt`）
  - `tools/verification/extract_score_plates.py` → 从实机帧抽出比分板区域（`data/score_plates.npz`）
  - `tools/verification/cut_score.py` → 切比分数字模板并复算准确率（自带人工核对点）
  - `tools/verification/data/` 是从 `%TEMP%/kanami_work` 抢救出的**唯一**实机素材，请勿删除

## 已知限制

- `game_end_lose / game_end_draw / 五杀 / 绝境翻盘` 等结算变体暂无手机模板（录屏素材未覆盖），
  对应语音不会触发；补模板流程见适配文档第 6 节。
- 开局/换边没有独立画面，开局语音由首个购买阶段 + 阵营副标题推导。
- 计时是**估算值**（按阶段时长倒推，与 PC 端一致）；比分优先读屏，读不到回退估算。
- 比分模板是按 2026-10-05 那台手机（2772×1280，19.5:9）的实机帧切的；换机型或换比例
  可能需要对同机型重切（用调试模式的「保存归一化帧」取证后重跑 `cut_score.py`）。
- 悬浮窗遮挡游戏画面：默认位置已避开匹配区，但不保证不挡到操作按钮，请自行拖到顺手的位置。
- 许可证沿用 GPL-2.0-or-later（识别逻辑源自 Kanami-Reporter）。

## 素材来源

- 背景壁纸：玩家同人插画「kanami / 你看 世界好美」，作者 **鲜榨豆豆奶（ddmilk）**，
  原发米游社 <https://www.miyoushe.com/sr/article/56620673>（Pixiv 同名作者）。
  原图 2600×4600，裁成 1080×2340 后转 WebP（约 200KB）。
  **版权归原作者所有，此处仅作个人使用，不要再分发。**
- 应用图标：香奈美「MVP」表情包，与 Windows 桌面版同一张
  （`Kanami-Reporter-Standalone/artifacts/icon-candidates/kanami-01.png`），同样**仅个人使用**。
- 液态玻璃组件：`ui/liquid/` 下 8 个文件（底栏 2 + 按钮 + 滑杆 + 开关 + 3 个动效工具）
  vendor 自 [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) tag `2.0.1`
  的示例代码（Apache-2.0），已标注来源与改动；`LiquidBottomTabs` 的交互层由本项目重写，
  文件头注明了原因。
