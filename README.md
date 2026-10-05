# 香奈美x黑潮爆破（Android 版）

Kanami-Reporter 的安卓版：对《三角洲行动》手游「竞技爆破」（黑潮爆破）对局画面做
模板匹配 + 状态机判断，按阶段自动播报香奈美语音。UI 采用
[Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)（Backdrop，
`io.github.kyant0:backdrop:2.0.1`）打造液态玻璃风格。

模板与适配依据见 `Kanami-Reporter-Standalone/docs/mobile-adaptation.md`
（基于 2026-10-05 手机实机完整对局录屏裁剪并验证）。

## 工作原理

```
MediaProjection 屏幕采集（横屏尺寸，RGBA）
      │  ImageReader，约 15 fps，丢帧保最新
      ▼
FrameProcessing.normalize   宽度等比缩放到 1920、顶对齐（手机 HUD 契合点）
      ▼
FrameProcessing.score       固定 ROI ZNCC + 13 个整数/半像素偏移（与 PC 端一致）
      ▼
ReporterStateMachine        状态机（手机版优先级规则：回合结束 > 购买 > 炸弹 > 开局计时）
      ▼
VoicePlayer                 assets/voices 中文 MP3 播报
```

- 模板：`app/src/main/assets/templates/*.mobile.krt`（13 个，含开局计时秒值变体 d112–d116）
- 语音：`app/src/main/assets/voices/*.mp3`（30 条，与 PC 端同源）
- 阵营：购买横幅副标题「攻方：安放炸弹 / 守方：歼灭敌军」逐回合校正，翻转即播「攻守互换」

## 构建

- Android Studio（AGP 9.3.2 / Kotlin 2.4.10 / Gradle 9.7.1，JDK 17+）
- `./gradlew :app:assembleDebug` 或直接 Run
- minSdk 29 / targetSdk 36

## 使用

1. 启动应用 → 「开始识别」→ 允许屏幕录制（系统弹窗）；
2. 切入游戏（横屏全屏），正常打完对局即可，播报自动进行；
3. 「设置」页可调匹配阈值（默认 0.90）。

## 已知限制

- `game_end_lose / game_end_draw / 五杀 / 绝境翻盘` 等结算变体暂无手机模板
  （录屏素材未覆盖），对应语音不会触发；补模板流程见适配文档第 6 节。
- 开局/换边没有独立画面，开局语音由首个购买阶段 + 阵营副标题推导。
- 识别服务采集整块屏幕，竖屏桌面下不会误触发（画面被缩放进横屏画布）。
- 许可证沿用 GPL-2.0-or-later（识别逻辑源自 Kanami-Reporter）。
