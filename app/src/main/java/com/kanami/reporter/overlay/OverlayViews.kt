package com.kanami.reporter.overlay

import android.content.Context
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import com.kanami.reporter.core.VoiceTable
import com.kanami.reporter.status.RecognitionStatus
import com.kanami.reporter.capture.FreezeDetector
import kotlin.math.roundToInt

/**
 * 悬浮窗视图：纯 Android View 实现。
 *
 * 为什么不用 Compose / 液态玻璃：第三方悬浮窗无法真正折射它下面的游戏画面（跨窗口采样做不到），
 * 所以玻璃效果在这里只有"半透明+描边"这一层意义，用 View 实现更稳、更省电、没有生命周期坑。
 */
internal object OverlayViews {

    const val COLOR_OK = 0xFF7FD1AE.toInt()
    const val COLOR_WARN = 0xFFFFC46B.toInt()
    const val COLOR_IDLE = 0xFF9FB4D8.toInt()
    const val COLOR_TEXT = 0xFFE8F0FF.toInt()
    const val COLOR_DIM = 0xFF9FB4D8.toInt()

    fun dp(context: Context, value: Float): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, context.resources.displayMetrics)
            .roundToInt()

    private fun rounded(context: Context, radiusDp: Float, color: Int, borderColor: Int? = null): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            cornerRadius = dp(context, radiusDp).toFloat()
            setColor(color)
            borderColor?.let { setStroke(dp(context, 1f).coerceAtLeast(1), it) }
        }

    fun panelBackground(context: Context) =
        rounded(context, 16f, 0xB3101A2E.toInt(), 0x33FFFFFF)

    fun pillBackground(context: Context) =
        rounded(context, 14f, 0x8C101A2E.toInt(), 0x2EFFFFFF)

    fun text(
        context: Context,
        sizeSp: Float,
        color: Int = COLOR_TEXT,
        bold: Boolean = false,
        mono: Boolean = false
    ): TextView = TextView(context).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
        setTextColor(color)
        if (bold) setTypeface(typeface, Typeface.BOLD)
        if (mono) typeface = Typeface.MONOSPACE
        includeFontPadding = false
    }

    fun dot(context: Context, color: Int): View = View(context).apply {
        background = rounded(context, 6f, color)
    }

    fun statusColor(status: RecognitionStatus): Int = when {
        !status.running -> COLOR_IDLE
        status.capture.frozen -> COLOR_WARN
        status.capture.error != null -> COLOR_WARN
        else -> COLOR_OK
    }

    fun statusShort(status: RecognitionStatus): String = when {
        !status.running -> "未识别"
        status.capture.error != null -> "采集异常"
        status.capture.frozen -> "画面不动了"
        !status.capture.landscape -> "等待横屏对局"
        else -> "识别中"
    }

    fun sideLabel(side: Int): String = when (side) {
        1 -> "攻方"
        2 -> "守方"
        else -> "阵营未知"
    }

    fun clock(seconds: Int): String = "%d:%02d".format(seconds / 60, seconds % 60)

    fun scoreLine(status: RecognitionStatus): String {
        val us = status.match.scoreUs
        val enemy = status.match.scoreEnemy
        val est = "估算 ${status.match.estScoreUs}:${status.match.estScoreEnemy}"
        return when {
            us != null && enemy != null -> "比分 $us:$enemy（读屏）"
            us == null && enemy == null -> "比分 --:--（$est）"
            else -> "比分 ${us ?: "?"}:${enemy ?: "?"}（部分读屏 · $est）"
        }
    }

    fun freezeDetail(status: RecognitionStatus): String = when {
        !status.running -> "未在识别"
        status.capture.frozen -> "${status.capture.frozenReason?.let { FreezeDetector.reasonLabel(it) } ?: "画面停止"}" +
            "（${status.capture.frozenForMs / 1000}s）"

        !status.capture.landscape -> "当前不是横屏画面，暂不做匹配"
        else -> "正常"
    }
}

/** 主悬浮窗：收起 = 小胶囊，展开 = 状态面板。 */
internal class MainOverlayView(context: Context) : LinearLayout(context) {

    private val dot: View = OverlayViews.dot(context, OverlayViews.COLOR_IDLE)
    private val pillText: TextView = OverlayViews.text(context, 12f, OverlayViews.COLOR_TEXT, bold = true)
    private val pill: LinearLayout
    private val panel: LinearLayout
    private val titleText: TextView = OverlayViews.text(context, 14f, OverlayViews.COLOR_TEXT, bold = true)
    private val detail: TextView = OverlayViews.text(context, 12f, OverlayViews.COLOR_DIM)
    private val eventText: TextView = OverlayViews.text(context, 12f, OverlayViews.COLOR_OK)
    private val footer: TextView = OverlayViews.text(context, 11f, OverlayViews.COLOR_DIM)

    init {
        orientation = VERTICAL

        pill = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = OverlayViews.pillBackground(context)
            val padH = OverlayViews.dp(context, 10f)
            val padV = OverlayViews.dp(context, 6f)
            setPadding(padH, padV, padH, padV)
            addView(dot, LayoutParams(OverlayViews.dp(context, 10f), OverlayViews.dp(context, 10f)).apply {
                marginEnd = OverlayViews.dp(context, 8f)
            })
            addView(pillText)
        }
        addView(pill)

        panel = LinearLayout(context).apply {
            orientation = VERTICAL
            background = OverlayViews.panelBackground(context)
            val pad = OverlayViews.dp(context, 14f)
            setPadding(pad, pad, pad, pad)
            val titleRow = LinearLayout(context).apply {
                orientation = HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                addView(dot, LayoutParams(OverlayViews.dp(context, 10f), OverlayViews.dp(context, 10f)).apply {
                    marginEnd = OverlayViews.dp(context, 8f)
                })
                addView(titleText)
                addView(View(context), LayoutParams(0, 1, 1f))
                addView(OverlayViews.text(context, 10f, OverlayViews.COLOR_DIM).apply { text = "点击收起" })
            }
            addView(titleRow)
            addView(detail, marginTop(8f))
            addView(eventText, marginTop(6f))
            addView(footer, marginTop(6f))
        }
        addView(panel)
    }

    private fun marginTop(dp: Float): LayoutParams =
        LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT).apply {
            topMargin = OverlayViews.dp(context, dp)
        }

    fun render(status: RecognitionStatus, collapsed: Boolean) {
        val color = OverlayViews.statusColor(status)
        dot.background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
        pillText.text = OverlayViews.statusShort(status)
        pill.visibility = if (collapsed) VISIBLE else GONE
        panel.visibility = if (collapsed) GONE else VISIBLE

        if (collapsed) return

        titleText.text = OverlayViews.statusShort(status)
        titleText.setTextColor(color)

        val match = status.match
        val stateName = match.stateLabel.ifEmpty { "等待进入对局" }
        val roundPart = if (match.round > 0) "第 ${match.round} 回合 · ${OverlayViews.sideLabel(match.side)}" else "未进入回合"
        val clockPart = match.remainingSeconds?.let { " · 估算 ${OverlayViews.clock(it)}" } ?: ""
        detail.text = "$stateName\n$roundPart$clockPart"

        val latest = status.events.firstOrNull()
        eventText.text = if (latest != null) {
            val ageSec = ((android.os.SystemClock.elapsedRealtime() - latest.atMs) / 1000L)
            "最近播报：${latest.label}（${ageSec}s 前）"
        } else {
            "最近播报：暂无"
        }

        footer.text = buildString {
            append("${OverlayViews.freezeDetail(status)}  ·  ")
            append("${status.capture.frameWidth}×${status.capture.frameHeight}@${status.capture.fps}fps")
        }
    }

    /** 展开面板里的语音事件行，供调试窗复用。 */
    fun latestEventLabel(status: RecognitionStatus): String =
        status.events.firstOrNull()?.let { VoiceTable.displayName(it.id) }.orEmpty()
}

/** 调试悬浮窗：比分 / 阵营 / 计时 / 模板匹配排序。 */
internal class DebugOverlayView(context: Context) : LinearLayout(context) {

    private val scoreText: TextView = OverlayViews.text(context, 13f, OverlayViews.COLOR_TEXT, bold = true)
    private val matchText: TextView = OverlayViews.text(context, 12f, OverlayViews.COLOR_DIM)
    private val captureText: TextView = OverlayViews.text(context, 11f, OverlayViews.COLOR_DIM)
    private val foregroundText: TextView = OverlayViews.text(context, 11f, OverlayViews.COLOR_DIM)
    private val scoreHeader: TextView = OverlayViews.text(context, 11f, OverlayViews.COLOR_DIM)
    private val scoreList: TextView = OverlayViews.text(context, 11f, OverlayViews.COLOR_TEXT, mono = true)

    init {
        orientation = VERTICAL
        background = OverlayViews.panelBackground(context)
        val pad = OverlayViews.dp(context, 12f)
        setPadding(pad, pad, pad, pad)
        addView(OverlayViews.text(context, 13f, OverlayViews.COLOR_TEXT, bold = true).apply { text = "调试 · 对局信息" })
        addView(scoreText, marginTop(8f))
        addView(matchText, marginTop(4f))
        addView(foregroundText, marginTop(4f))
        addView(captureText, marginTop(4f))
        scoreHeader.text = "模板匹配值（前 8）"
        addView(scoreHeader, marginTop(8f))
        addView(scoreList, marginTop(2f))
    }

    private fun marginTop(dp: Float): LayoutParams =
        LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT).apply {
            topMargin = OverlayViews.dp(context, dp)
        }

    fun render(status: RecognitionStatus) {
        val match = status.match
        scoreText.text = OverlayViews.scoreLine(status)
        val clockPart = match.remainingSeconds?.let { "估算 ${OverlayViews.clock(it)}" } ?: "计时未知"
        matchText.text = "${OverlayViews.sideLabel(match.side)} · 第 ${match.round} 回合 · $clockPart"
        foregroundText.text = "前台：${status.foregroundPackage ?: "未知（未开无障碍）"}"
        captureText.text = "采集 ${status.capture.frameWidth}×${status.capture.frameHeight}@${status.capture.fps}fps" +
            "  面板 ${status.capture.panelWidth}×${status.capture.panelHeight}" +
            "  ${OverlayViews.statusShort(status)}" +
            (status.capture.error?.let { "  异常：$it" } ?: "")

        scoreList.text = if (status.scores.isEmpty()) {
            "—"
        } else {
            status.scores.take(8).joinToString("\n") { s ->
                val mark = if (s.hit) " ✓" else ""
                "%.3f  %s%s".format(s.score, s.label, mark)
            }
        }
    }
}
