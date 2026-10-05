package com.kanami.reporter.ui

import android.content.Context
import android.content.Intent
import android.os.SystemClock
import android.widget.Toast
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
    var threshold by remember(revision) { mutableStateOf(settings.threshold.toFloat()) }
    var tapCount by remember { mutableIntStateOf(0) }
    var lastTapAt by remember { mutableLongStateOf(0L) }
    var debugHint by remember { mutableStateOf<String?>(null) }
    var showsLog by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Text("匹配阈值：%.2f".format(threshold), color = StatusColors.text)
                GlassSlider(
                    value = threshold,
                    onValueChange = {
                        threshold = it
                        engine.threshold = it.toDouble()
                        settings.threshold = it.toDouble()
                    },
                    valueRange = 0.80f..0.99f
                )
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

        PermissionListCard(
            items = hub.items(),
            backdrop = backdrop,
            onLaunch = onLaunch
        )

        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text(
                        "使用说明",
                        color = StatusColors.text,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .weight(1f)
                            .clickable {
                                val now = SystemClock.elapsedRealtime()
                                tapCount = if (now - lastTapAt > 3000L) 1 else tapCount + 1
                                lastTapAt = now
                                when {
                                    tapCount >= 5 -> {
                                        settings.debugMode = true
                                        tapCount = 0
                                        debugHint = "调试模式已开启"
                                        Toast.makeText(context, "调试模式已开启", Toast.LENGTH_SHORT).show()
                                    }

                                    tapCount >= 3 -> debugHint = "再点 ${5 - tapCount} 次开启调试模式"
                                    else -> debugHint = null
                                }
                            }
                    )
                    (debugHint)?.let {
                        Text(it, color = StatusColors.ok, fontSize = 11.sp)
                    }
                }
                Spacer(Modifier.height(8.dp))
                Text(
                    "1. 进入《三角洲行动》手游竞技爆破对局（手机横屏）；\n" +
                        "2. 回到本应用，点「开始识别」并允许屏幕录制；\n" +
                        "3. 切回游戏，香奈美会随对局阶段自动播报，悬浮窗会显示当前事件与识别状态。\n\n" +
                        "识别按 1920×1080 宽度等比、顶对齐归一化，适配全面屏比例。" +
                        "购买阶段打开全屏购买菜单时顶栏被遮挡，属正常现象，不影响播报。\n\n" +
                        "（连点本标题 5 次可开启调试模式）",
                    color = StatusColors.idle,
                    fontSize = 13.sp
                )
            }
        }

        if (settings.debugMode) {
            DebugCard(context, settings, backdrop, status, showsLog) { showsLog = it }
        }

        GlassCard(backdrop = backdrop) {
            Column(Modifier.padding(20.dp)) {
                Text("关于", color = StatusColors.text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(6.dp))
                Text(
                    "香奈美x黑潮爆破 · 手机版\n" +
                        "背景图为《卡拉比丘》同人/官方公开素材，仅个人使用；角色与美术版权归原权利方所有。\n" +
                        "本应用只读屏识别对局阶段并播放本地语音，不修改游戏、不做任何自动化操作。",
                    color = StatusColors.dim,
                    fontSize = 12.sp
                )
            }
        }
    }
}

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
