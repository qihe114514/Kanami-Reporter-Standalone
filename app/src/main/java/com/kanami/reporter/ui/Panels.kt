package com.kanami.reporter.ui

import android.content.Context
import android.content.Intent
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kanami.reporter.capture.FreezeDetector
import com.kanami.reporter.permissions.PermissionHub
import com.kanami.reporter.permissions.PermissionItem
import com.kanami.reporter.status.RecognitionStatus
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kanami.reporter.status.TemplateScore
import com.kyant.backdrop.shadow.Shadow
import com.kyant.shapes.Capsule

object StatusColors {
    val ok = Color(0xFF7FD1AE)
    val warn = Color(0xFFFFC46B)
    val idle = Color(0xFF9FB4D8)
    val dim = Color(0xFF6E7FA0)
    val text = Color(0xFFE8F0FF)
}

/** 小号玻璃按钮（权限跳转等密集场景用）。 */
@Composable
fun GlassChip(
    text: String,
    onClick: () -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier
) {
    Box(
        modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    vibrancy()
                    blur(2f.dp.toPx())
                    lens(8f.dp.toPx(), 16f.dp.toPx())
                },
                highlight = { Highlight.Default },
                shadow = { Shadow.Default }
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 7.dp)
    ) {
        Text(text, color = StatusColors.text, fontSize = 12.sp)
    }
}

fun statusHeadline(status: RecognitionStatus): Pair<String, Color> = when {
    !status.running -> "未在识别" to StatusColors.idle
    status.capture.error != null -> "采集异常" to StatusColors.warn
    status.capture.frozen -> "现在画面不动了" to StatusColors.warn
    !status.capture.landscape -> "当前非横屏，暂不匹配" to StatusColors.warn
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

/** 运行页主状态卡。 */
@Composable
fun StatusCard(status: RecognitionStatus, templateCount: Int, backdrop: Backdrop) {
    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(20.dp)) {
            val (headline, color) = statusHeadline(status)
            Text(headline, color = color, fontSize = 23.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(8.dp))
            val match = status.match
            Text(
                "第 ${match.round} 回合 · ${sideLabel(match.side)}" +
                    (match.remainingSeconds?.let { " · 估算 ${clockText(it)}" } ?: ""),
                color = StatusColors.idle,
                fontSize = 14.sp
            )
            Spacer(Modifier.height(4.dp))
            Text(scoreText(status), color = StatusColors.text, fontSize = 14.sp)
            Spacer(Modifier.height(4.dp))
            Text(
                "采集 ${status.capture.frameWidth}×${status.capture.frameHeight}" +
                    " @${status.capture.fps}fps · 面板 ${status.capture.panelWidth}×${status.capture.panelHeight}" +
                    " · 已加载模板 $templateCount",
                color = StatusColors.dim,
                fontSize = 12.sp
            )
            status.capture.error?.let {
                Spacer(Modifier.height(4.dp))
                Text("采集异常：$it", color = StatusColors.warn, fontSize = 12.sp)
            }
            if (status.capture.frozen) {
                Spacer(Modifier.height(4.dp))
                Text(
                    "现在画面不动了（${FreezeDetector.reasonLabel(status.capture.frozenReason)}，" +
                        "${status.capture.frozenForMs / 1000}s）",
                    color = StatusColors.warn,
                    fontSize = 13.sp
                )
            }
            val latest = status.events.firstOrNull()
            Spacer(Modifier.height(8.dp))
            Text(
                if (latest != null) {
                    val age = (android.os.SystemClock.elapsedRealtime() - latest.atMs) / 1000
                    "最近播报：${latest.label}（${age}s 前）"
                } else {
                    "最近播报：暂无"
                },
                color = StatusColors.ok,
                fontSize = 13.sp
            )
        }
    }
}

/** 模板匹配值前 8。 */
@Composable
fun ScoreListCard(status: RecognitionStatus, backdrop: Backdrop) {
    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(20.dp)) {
            Text("模板匹配值（前 8）", color = StatusColors.idle, fontSize = 14.sp)
            Spacer(Modifier.height(8.dp))
            val scores: List<TemplateScore> = status.scores.take(8)
            if (scores.isEmpty()) {
                Text("开始识别后显示实时匹配值", color = StatusColors.dim, fontSize = 13.sp)
            } else {
                scores.forEach { item ->
                    val color = if (item.hit) StatusColors.ok else StatusColors.dim
                    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                        Text(item.label, color = color, fontSize = 13.sp, modifier = Modifier.width(150.dp))
                        Text("%.3f".format(item.score), color = color, fontSize = 13.sp)
                    }
                }
            }
        }
    }
}

/** 必须权限清单：悬浮窗 / 后台驻留 / 无障碍 / 通知。 */
@Composable
fun PermissionListCard(
    items: List<PermissionItem>,
    backdrop: Backdrop,
    onLaunch: (Intent) -> Unit,
    compact: Boolean = false
) {
    val missing = items.count { !it.granted }
    GlassCard(backdrop = backdrop) {
        Column(Modifier.padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("必须权限", color = StatusColors.text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.width(10.dp))
                Text(
                    if (missing == 0) "已全部开启" else "还有 $missing 项未开启",
                    color = if (missing == 0) StatusColors.ok else StatusColors.warn,
                    fontSize = 12.sp
                )
            }
            if (compact && missing == 0) return@Column
            Spacer(Modifier.height(6.dp))
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
}
