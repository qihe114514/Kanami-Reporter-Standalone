package com.kanami.reporter.ui

import android.content.Intent
import android.os.SystemClock
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.EaseOutCubic
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kanami.reporter.permissions.PermissionItem
import com.kanami.reporter.status.RecognitionStatus
import com.kanami.reporter.status.TemplateScore
import com.kanami.reporter.ui.liquid.LiquidButton
import com.kyant.backdrop.Backdrop
import com.kyant.shapes.Capsule

/**
 * 文字/状态配色。跟着 [AdaptiveGlass] 走：背景偏亮时切到深色一套，保证可读性。
 * 用 `get()` 而不是固定值，这样在 composable 里读会自动订阅亮度变化。
 */
object StatusColors {
    val ok: Color get() = if (AdaptiveGlass.isLightBackground) Color(0xFF1E7D5A) else Color(0xFF7FD1AE)
    val warn: Color get() = if (AdaptiveGlass.isLightBackground) Color(0xFF8A5A00) else Color(0xFFFFC46B)
    val idle: Color get() = if (AdaptiveGlass.isLightBackground) Color(0xFF3F5675) else Color(0xFF9FB4D8)
    val dim: Color get() = if (AdaptiveGlass.isLightBackground) Color(0xFF51637F) else Color(0xFF6E7FA0)
    val text: Color get() = if (AdaptiveGlass.isLightBackground) Color(0xFF101A2E) else Color(0xFFE8F0FF)
}

fun statusHeadline(status: RecognitionStatus): Pair<String, Color> = when {
    !status.running -> "未在识别" to StatusColors.idle
    status.capture.error != null -> "采集异常" to StatusColors.warn
    // 竖屏不是故障：用户就停在本应用里。采集照跑，切回游戏（横屏）自动开始匹配。
    !status.capture.landscape -> "采集运行中 · 切回游戏后自动识别" to StatusColors.ok
    status.match.stateLabel.isEmpty() -> "识别中…（等待进入对局）" to StatusColors.ok
    else -> status.match.stateLabel to StatusColors.ok
}

fun sideLabel(side: Int): String = when (side) {
    1 -> "攻方"
    2 -> "守方"
    else -> "阵营未知"
}

fun clockText(seconds: Int): String = "%d:%02d".format(seconds / 60, seconds % 60)

fun scoreText(status: RecognitionStatus): String {
    val us = status.match.scoreUs
    val enemy = status.match.scoreEnemy
    val est = "估算 ${status.match.estScoreUs}:${status.match.estScoreEnemy}"
    return when {
        us != null && enemy != null -> "比分 $us:$enemy"
        us == null && enemy == null -> "比分 --:--（$est）"
        else -> "比分 ${us ?: "?"}:${enemy ?: "?"}（$est）"
    }
}

fun roundLine(status: RecognitionStatus): String {
    val match = status.match
    val round = if (match.round > 0) "第 ${match.round} 回合 · ${sideLabel(match.side)}" else "未进入回合"
    val clock = match.remainingSeconds?.let { " · 估算 ${clockText(it)}" } ?: ""
    return round + clock
}

fun latestEventLine(status: RecognitionStatus): String {
    val latest = status.events.firstOrNull() ?: return "最近播报：暂无"
    val age = (SystemClock.elapsedRealtime() - latest.atMs) / 1000
    return "最近播报：${latest.label}（${age}s 前）"
}

fun captureLine(status: RecognitionStatus, templateCount: Int): String =
    "帧率：${status.capture.fps} 帧/秒 · ${status.capture.frameWidth}×${status.capture.frameHeight}" +
        " · 面板 ${status.capture.panelWidth}×${status.capture.panelHeight} · 模板 $templateCount"

/**
 * 运行页主卡：**状态、对局信息、开始/停止操作放在同一张卡里**。
 *
 * 之前的排版把「开始识别」压在三张卡下面，要滚动才看得见；权限清单和调试信息反而常驻占屏。
 * 现在一屏之内就能看清「现在什么状态、下一步按哪个键」，其余的都折叠到下面。
 */
@Composable
fun ControlCard(
    status: RecognitionStatus,
    templateCount: Int,
    backdrop: Backdrop,
    onStart: () -> Unit,
    onStop: () -> Unit
) {
    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(20.dp)) {
            val (headline, color) = statusHeadline(status)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier
                        .size(10.dp)
                        .clip(Capsule())
                        .background(color)
                )
                Spacer(Modifier.width(10.dp))
                Text(headline, color = color, fontSize = 21.sp, fontWeight = FontWeight.SemiBold)
            }

            Spacer(Modifier.height(12.dp))
            if (status.running) {
                Text(roundLine(status), color = StatusColors.text, fontSize = 16.sp)
                Spacer(Modifier.height(4.dp))
                Text(scoreText(status), color = StatusColors.text, fontSize = 16.sp)
                Spacer(Modifier.height(10.dp))
                Text(latestEventLine(status), color = StatusColors.ok, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                Text(captureLine(status, templateCount), color = StatusColors.dim, fontSize = 12.sp)
                if (!status.capture.landscape) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "现在看到的是本应用的竖屏界面，不做模板匹配，但采集一直在跑；" +
                            "切回游戏会自动切成横屏并开始识别，不需要重新授权。",
                        color = StatusColors.idle,
                        fontSize = 12.sp
                    )
                }
            } else {
                Text(
                    "把游戏切到横屏，点下面的按钮并允许录屏；之后香奈美会随对局阶段自动播报，" +
                        "悬浮窗显示实时状态。",
                    color = StatusColors.idle,
                    fontSize = 14.sp
                )
            }

            status.notice?.let {
                Spacer(Modifier.height(10.dp))
                Text(
                    it,
                    color = if (status.noticeIsError) StatusColors.warn else StatusColors.ok,
                    fontSize = 13.sp
                )
            }
            status.capture.error?.let {
                Spacer(Modifier.height(6.dp))
                Text("采集异常：$it", color = StatusColors.warn, fontSize = 12.sp)
            }

            Spacer(Modifier.height(18.dp))
            LiquidButton(
                onClick = { if (status.running) onStop() else onStart() },
                backdrop = backdrop,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(
                    if (status.running) "停止识别" else "开始识别",
                    color = if (status.running) Color(0xFFFFD9D9) else StatusColors.text,
                    fontSize = 16.sp
                )
            }
        }
    }
}

/** 可折叠的玻璃卡：收起时只留一行标题 + 摘要。 */
@Composable
fun CollapsibleCard(
    title: String,
    summary: String,
    summaryColor: Color,
    backdrop: Backdrop,
    initiallyExpanded: Boolean = false,
    content: @Composable () -> Unit
) {
    var expanded by remember { mutableStateOf(initiallyExpanded) }
    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(horizontal = 20.dp, vertical = 16.dp)) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { expanded = !expanded },
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(title, color = StatusColors.text, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.width(10.dp))
                Text(summary, color = summaryColor, fontSize = 12.sp)
                Spacer(Modifier.weight(1f))
                Text(if (expanded) "收起" else "展开", color = StatusColors.dim, fontSize = 12.sp)
                Spacer(Modifier.width(4.dp))
                Box(Modifier.rotate(if (expanded) 90f else -90f)) {
                    Text("›", color = StatusColors.dim, fontSize = 16.sp)
                }
            }
            AnimatedVisibility(
                visible = expanded,
                enter = expandVertically(tween(320, easing = EaseOutCubic)) + fadeIn(tween(220, delayMillis = 60)),
                exit = shrinkVertically(tween(240, easing = EaseOutCubic)) + fadeOut(tween(140))
            ) {
                Column {
                    Spacer(Modifier.height(12.dp))
                    content()
                }
            }
        }
    }
}

/** 模板匹配值前 8（调试信息，默认收起）。 */
@Composable
fun ScoreListCard(status: RecognitionStatus, backdrop: Backdrop) {
    val scores: List<TemplateScore> = status.scores
    val hits = scores.count { it.hit }
    CollapsibleCard(
        title = "模板匹配值",
        summary = if (scores.isEmpty()) "开始识别后显示" else "命中 $hits / 前 8",
        summaryColor = if (hits > 0) StatusColors.ok else StatusColors.dim,
        backdrop = backdrop
    ) {
        Column {
            scores.take(8).forEach { item ->
                val color = if (item.hit) StatusColors.ok else StatusColors.dim
                Row(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                    Text(item.label, color = color, fontSize = 13.sp, modifier = Modifier.width(160.dp))
                    Text("%.3f".format(item.score), color = color, fontSize = 13.sp)
                }
            }
        }
    }
}

/**
 * 必须权限清单。全部开启时收成一行，缺项时自动展开并给出跳转入口
 * （用户手动点过之后按用户的选择来，不再被自动展开覆盖）。
 */
@Composable
fun PermissionListCard(
    items: List<PermissionItem>,
    backdrop: Backdrop,
    onLaunch: (Intent) -> Unit
) {
    val missing = items.count { !it.granted }
    var userChoice by remember { mutableStateOf<Boolean?>(null) }
    val expanded = userChoice ?: (missing > 0)

    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(horizontal = 20.dp, vertical = 16.dp)) {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { userChoice = !expanded },
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("必须权限", color = StatusColors.text, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.width(10.dp))
                Text(
                    if (missing == 0) "${items.size}/${items.size} 已开启" else "还有 $missing 项未开启",
                    color = if (missing == 0) StatusColors.ok else StatusColors.warn,
                    fontSize = 12.sp
                )
                Spacer(Modifier.weight(1f))
                Text(if (expanded) "收起" else "展开", color = StatusColors.dim, fontSize = 12.sp)
                Spacer(Modifier.width(4.dp))
                Box(Modifier.rotate(if (expanded) 90f else -90f)) {
                    Text("›", color = StatusColors.dim, fontSize = 16.sp)
                }
            }
            AnimatedVisibility(
                visible = expanded,
                enter = expandVertically(tween(320, easing = EaseOutCubic)) + fadeIn(tween(220, delayMillis = 60)),
                exit = shrinkVertically(tween(240, easing = EaseOutCubic)) + fadeOut(tween(140))
            ) {
                Column {
                    Spacer(Modifier.height(6.dp))
                    PermissionItems(items, backdrop, onLaunch)
                }
            }
        }
    }
}

@Composable
private fun PermissionItems(
    items: List<PermissionItem>,
    backdrop: Backdrop,
    onLaunch: (Intent) -> Unit
) {
    Column {
        items.forEach { item ->
            Column(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        Modifier
                            .size(8.dp)
                            .clip(Capsule())
                            .background(if (item.granted) StatusColors.ok else StatusColors.warn)
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(item.title, color = StatusColors.text, fontSize = 14.sp)
                    Spacer(Modifier.width(8.dp))
                    Text(
                        if (item.granted) "已开启" else "未开启",
                        color = if (item.granted) StatusColors.ok else StatusColors.warn,
                        fontSize = 12.sp
                    )
                }
                if (!item.granted) {
                    Spacer(Modifier.height(3.dp))
                    Text(item.description, color = StatusColors.dim, fontSize = 12.sp)
                    Spacer(Modifier.height(6.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        item.intents.forEach { (label, intent) ->
                            GlassChip(label, { onLaunch(intent) }, backdrop)
                        }
                    }
                }
            }
        }
    }
}
