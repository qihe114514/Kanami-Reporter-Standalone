package com.kanami.reporter.ui

import android.content.Context
import android.content.Intent
import android.os.SystemClock
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kanami.reporter.core.RecognitionEngine
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.debug.FrameDump
import com.kanami.reporter.permissions.PermissionHub
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.RecognitionStatus
import com.kanami.reporter.status.StatusHub
import com.kanami.reporter.ui.liquid.LiquidSlider
import com.kyant.backdrop.Backdrop
import kotlinx.coroutines.delay

@Composable
fun SettingsPage(
    context: Context,
    settings: Settings,
    engine: RecognitionEngine,
    hub: PermissionHub,
    backdrop: Backdrop,
    revision: Int,
    status: RecognitionStatus,
    onLaunch: (Intent) -> Unit
) {
    // 注意：这几个滑块状态**不能**写成 `remember(revision) { ... }`。
    // 带上 revision 当 key 时，每次写设置都会重建出一个新的 MutableState，而传给 LiquidSlider 的
    // `value = { threshold }` 这类 lambda 会被 Compose 记忆化（捕获的仍是**旧**的 state 对象），
    // 于是滑块读旧值、写旧值，界面上的数字纹丝不动，点"恢复默认"也回不去。
    var threshold by remember { mutableStateOf(settings.threshold.toFloat()) }
    var refraction by remember { mutableStateOf(settings.glassRefraction) }
    var blurStrength by remember { mutableStateOf(settings.glassBlur) }
    var tapCount by remember { mutableIntStateOf(0) }
    var lastTapAt by remember { mutableLongStateOf(0L) }
    var showsLog by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }

    // 拖动时实时改引擎；落盘延后到停手，免得每动一格都写 SharedPreferences 触发整页重组。
    LaunchedEffect(threshold) {
        engine.threshold = threshold.toDouble()
        delay(250)
        settings.threshold = threshold.toDouble()
    }

    // 玻璃外观同理：先改内存态（立刻重绘），停手 300ms 后才落盘
    LaunchedEffect(refraction) {
        GlassTuning.refraction = refraction
        delay(300)
        settings.glassRefraction = refraction
    }
    LaunchedEffect(blurStrength) {
        GlassTuning.blur = blurStrength
        delay(300)
        settings.glassBlur = blurStrength
    }

    Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Text("匹配阈值：%.2f".format(threshold), color = StatusColors.text)
                Spacer(Modifier.height(14.dp))
                LiquidSlider(
                    value = { threshold },
                    onValueChange = { threshold = it },
                    valueRange = 0.80f..0.99f,
                    visibilityThreshold = 0.001f,
                    backdrop = backdrop
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    "低于 0.90 容易误报；手机端模板已按实机录屏验证，建议保持默认。",
                    color = StatusColors.dim,
                    fontSize = 12.sp
                )
            }
        }

        GlassToggle(
            checked = settings.showOverlay,
            onCheckedChange = { settings.showOverlay = it },
            backdrop = backdrop,
            title = "游戏内悬浮窗",
            subtitle = if (settings.showOverlay) "识别时显示" else "已关闭"
        )

        GlassToggle(
            checked = settings.autoCheckUpdates,
            onCheckedChange = { settings.autoCheckUpdates = it },
            backdrop = backdrop,
            title = "启动时自动检查更新",
            subtitle = if (settings.autoCheckUpdates) "每次启动检查一次" else "已关闭"
        )

        // 个性化：液态玻璃的观感。只影响界面渲染，与识别无关。
        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text("个性化", color = StatusColors.text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.weight(1f))
                    GlassChip("恢复默认", {
                        refraction = GlassTuning.DEFAULT
                        blurStrength = GlassTuning.DEFAULT
                    }, backdrop)
                }
                Spacer(Modifier.height(6.dp))
                Text(
                    "调的是界面玻璃的折射与模糊，纯观感，不影响识别。",
                    color = StatusColors.dim,
                    fontSize = 12.sp
                )

                Spacer(Modifier.height(16.dp))
                Text("液态玻璃反射强度：%.2f".format(refraction), color = StatusColors.text)
                Spacer(Modifier.height(14.dp))
                LiquidSlider(
                    value = { refraction },
                    onValueChange = { refraction = it },
                    valueRange = GlassTuning.MIN..GlassTuning.MAX,
                    visibilityThreshold = 0.005f,
                    backdrop = backdrop
                )

                Spacer(Modifier.height(20.dp))
                Text("液态玻璃模糊强度：%.2f".format(blurStrength), color = StatusColors.text)
                Spacer(Modifier.height(14.dp))
                LiquidSlider(
                    value = { blurStrength },
                    onValueChange = { blurStrength = it },
                    valueRange = GlassTuning.MIN..GlassTuning.MAX,
                    visibilityThreshold = 0.005f,
                    backdrop = backdrop
                )
            }
        }

        PermissionListCard(
            items = hub.items(),
            backdrop = backdrop,
            onLaunch = onLaunch
        )

        if (settings.debugMode) {
            DebugCard(context, settings, backdrop, status, showsLog) { showsLog = it }
        }

        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text(
                        "关于",
                        color = StatusColors.text,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                        // 只有作者知道的入口：连点标题 5 次开启调试模式，界面上不做任何提示。
                        modifier = Modifier.clickable {
                            val now = SystemClock.elapsedRealtime()
                            tapCount = if (now - lastTapAt > 3000L) 1 else tapCount + 1
                            lastTapAt = now
                            if (tapCount >= 5) {
                                tapCount = 0
                                settings.debugMode = true
                            }
                        }
                    )
                    Spacer(Modifier.weight(1f))
                    Text("v${appVersion(context)}", color = StatusColors.dim, fontSize = 12.sp)
                }
                Spacer(Modifier.height(6.dp))
                Text(
                    "香奈美x黑潮爆破 · 手机版\n" +
                        "作者：其核\n" +
                        "许可证：GPL-2.0-or-later\n" +
                        "识别逻辑与桌面版同源，不依赖 OBS、不注入游戏进程。",
                    color = StatusColors.idle,
                    fontSize = 13.sp
                )

                Spacer(Modifier.height(14.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    GlassChip("检查更新", {
                        message = "正在检查…"
                        checkUpdate(context) { _, text -> message = text }
                    }, backdrop)
                    GlassChip("官网", { openUrl(context, HOME_URL) }, backdrop)
                    GlassChip("GitHub", { openUrl(context, GITHUB_URL) }, backdrop)
                }
                message?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = StatusColors.ok, fontSize = 12.sp)
                }

                Spacer(Modifier.height(14.dp))
                Text(
                    "背景为《卡拉比丘》玩家同人插画「kanami / 你看 世界好美」（作者 鲜榨豆豆奶 / ddmilk），" +
                        "仅作个人学习使用；角色与美术版权归原作者及原权利方所有，请勿二次分发。\n" +
                        "本应用只读屏识别对局阶段并播放本地语音，不修改游戏、不做任何自动化操作。",
                    color = StatusColors.dim,
                    fontSize = 12.sp
                )
            }
        }
    }
}

/** 读取本应用版本号（读取失败时给个占位，不能让"关于"页崩掉）。 */
internal fun appVersion(context: Context): String = runCatching {
    context.packageManager.getPackageInfo(context.packageName, 0).versionName
}.getOrNull() ?: "?"
// 注：openUrl 在 UpdateCheck.kt 里（检查更新、跳发布页共用）。

/** 调试模式卡片：日志路径/大小、常用操作、日志尾部查看。 */
@Composable
private fun DebugCard(
    context: Context,
    settings: Settings,
    backdrop: Backdrop,
    status: RecognitionStatus,
    showsLog: Boolean,
    onShowsLogChange: (Boolean) -> Unit
) {
    var message by remember { mutableStateOf<String?>(null) }
    var logLines by remember { mutableStateOf<List<String>>(emptyList()) }
    var refreshTick by remember { mutableIntStateOf(0) }

    LaunchedEffect(showsLog, refreshTick) {
        if (!showsLog) return@LaunchedEffect
        logLines = DebugLog.tail(160)
        delay(2000)
        refreshTick++
    }

    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(20.dp)) {
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Text("调试模式", color = StatusColors.text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.weight(1f))
                GlassChip("关闭调试", { settings.debugMode = false }, backdrop)
            }
            Spacer(Modifier.height(8.dp))
            Text(
                "日志文件：" + (DebugLog.sessionPath() ?: "（未创建）") + "\n" +
                    "大小：${DebugLog.sessionSizeBytes() / 1024} KB　" +
                    "帧导出：${FrameDump.dir(context).absolutePath}",
                color = StatusColors.dim,
                fontSize = 11.sp,
                fontFamily = FontFamily.Monospace
            )
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                GlassChip("保存当前帧", {
                    StatusHub.requestFrameDump()
                    message = "已请求保存，下一帧写入"
                }, backdrop)
                GlassChip(if (showsLog) "收起日志" else "查看日志", { onShowsLogChange(!showsLog) }, backdrop)
            }
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                GlassChip("分享日志", {
                    val intent = DebugLog.shareIntent()
                    if (intent == null) {
                        message = "还没有日志文件"
                    } else {
                        context.startActivity(Intent.createChooser(intent, "分享调试日志"))
                    }
                }, backdrop)
                GlassChip("导出到下载", {
                    val name = DebugLog.exportToDownloads(context)
                    message = if (name != null) "已导出：$name" else "导出失败（先开始识别产生日志）"
                }, backdrop)
                GlassChip("清空日志", {
                    DebugLog.clear()
                    logLines = emptyList()
                    message = "日志已清空"
                }, backdrop)
            }
            val notice = message ?: status.notice
            if (notice != null) {
                Spacer(Modifier.height(8.dp))
                Text(notice, color = StatusColors.ok, fontSize = 12.sp)
            }
            if (showsLog) {
                Spacer(Modifier.height(10.dp))
                Text(
                    logLines.takeLast(120).joinToString("\n"),
                    color = StatusColors.idle,
                    fontSize = 10.sp,
                    fontFamily = FontFamily.Monospace
                )
            }
        }
    }
}
