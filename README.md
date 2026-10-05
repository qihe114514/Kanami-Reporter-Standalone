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
FrameProcessing.normalize   宽度等比缩放到 1920、顶对齐（手机 HUD 契合点）
      ▼
FrameProcessing.score       固定 ROI ZNCC + 13 个整数/半像素偏移（与 PC 端一致）
FrameProcessing.scoreBinary 比分数字专用：二值形状相关（灰度相关区分度不够，见下）
      ▼
ReporterStateMachine        状态机（手机版优先级规则：回合结束 > 购买 > 炸弹 > 开局计时）
      ▼
VoicePlayer                 assets/voices 中文 MP3 播报
      ▼
StatusHub (StateFlow)       界面 / 悬浮窗统一取数；FreezeDetector 判断"画面不动了"
```

- 模板：`app/src/main/assets/templates/*.mobile.krt`（13 个状态模板 + 51 个比分数字模板）
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

### 比分读取

比分数字是白字深底、位置稳定，但**灰度 ZNCC 区分度不足**（同一数字跨帧 0.97，而 0 与 3/6 之间
也能到 0.77~0.80）。因此比分用**二值形状相关**（两侧都按亮度二值化后再算相关，异数字降到
0.54~0.86），每个数字备 3~4 个不同背景的实例取最高分，再过一遍"连续 3 帧一致才更新"的稳定器。
离线复算（对 2026-10-05 录屏的 1456 帧）：对方比分 100% 正确，我方 95%+（12 个人工核对点全部命中）。
读不到时界面回退显示按回合结算累计的**估算比分**，不会留空。

## 界面

- **背景**：香奈美立绘合成的竖屏背景（`assets/background/kanami_bg.webp`，127KB）
- **底栏**：液态玻璃底栏（官方示例组件 vendor 到 `ui/liquid/`：胶囊玻璃 + 滑动透镜指示器 +
  拖动切换 + 按触摸点挤压缩放与径向高亮），按钮同理，并附轻震动反馈
- **运行页**：权限清单 → 状态卡（识别状态/回合/阵营/估算计时/比分/采集尺寸）→ 开始/停止 →
  模板匹配值前 8
- **设置页**：匹配阈值、悬浮窗开关、权限清单、使用说明（连点 5 次进调试模式）、调试卡、关于

## 悬浮窗

由识别服务托管：点「开始识别」出现，点「停止」移除，设置里可整体关闭。

- **主悬浮窗**：收起时是 48dp 半透明小胶囊（绿=识别中/黄=画面不动了或采集异常/灰=未识别），
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

- 日志写进应用私有目录 `files/logs/kanami-<时间戳>.log`：设备与面板信息、每次采集几何重建、
  节流后的帧行、前 8 模板分数、每次状态迁移与语音、冻结进出、全部异常、前台应用变化
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

- 背景立绘：卡拉彼丘 Wiki（BWiki）公开素材「香奈美-初始立绘」，合成为竖屏背景，**仅个人使用**；
  角色与美术版权归原权利方所有。
- 液态玻璃组件：`ui/liquid/` 下 6 个文件 vendor 自
  [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) tag `2.0.1`
  的示例代码（Apache-2.0），已标注来源与改动。
