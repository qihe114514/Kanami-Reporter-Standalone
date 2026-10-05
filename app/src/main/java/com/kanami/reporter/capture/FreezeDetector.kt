package com.kanami.reporter.capture

import android.os.SystemClock

/**
 * 「画面不动了」检测。
 *
 * 对采集帧按固定网格抽样（与分辨率无关），比较相邻帧：
 * - 整幅平均绝对差 ≥ [meanAbsThreshold]，或
 * - 变化超过 [deltaThreshold] 灰阶的格子数 ≥ 网格的 [changedFraction]
 * 才算「画面在动」。只有一个秒数数字在跳时两者都不满足，仍算静止——这正是我们要提示的情况。
 *
 * 连续 [freezeAfterMs] 没动 → frozen=no_change；
 * 连续 [noFrameAfterMs] 没收到帧 → frozen=no_frames（采集链路停摆）。
 */
class FreezeDetector(
    private val gridCols: Int = 80,
    private val gridRows: Int = 45,
    private val deltaThreshold: Int = 8,
    private val meanAbsThreshold: Double = 1.5,
    private val changedFraction: Double = 0.01,
    private val freezeAfterMs: Long = 3000L,
    private val noFrameAfterMs: Long = 2500L
) {

    class State(val frozen: Boolean, val reason: String?, val frozenForMs: Long)

    private val samples = IntArray(gridCols * gridRows)
    private val previous = IntArray(gridCols * gridRows)
    private var havePrevious = false
    private var lastChangeAtMs = 0L
    private var lastFrameAtMs = 0L

    /** 最近一次「画面在动」度量，供调试日志观察/调参。 */
    var lastMeanAbsDiff: Double = 0.0
        private set
    var lastChangedFraction: Double = 0.0
        private set

    fun reset(nowMs: Long = SystemClock.elapsedRealtime()) {
        havePrevious = false
        lastChangeAtMs = nowMs
        lastFrameAtMs = 0L
        lastMeanAbsDiff = 0.0
        lastChangedFraction = 0.0
    }

    fun onFrame(rgba: ByteArray, width: Int, height: Int, nowMs: Long): State {
        if (width <= 0 || height <= 0 || rgba.size < width * height * 4) return evaluate(nowMs)
        val stepX = width.toDouble() / gridCols
        val stepY = height.toDouble() / gridRows
        var index = 0
        for (gy in 0 until gridRows) {
            val y = ((gy + 0.5) * stepY).toInt().coerceIn(0, height - 1)
            val rowBase = y * width
            for (gx in 0 until gridCols) {
                val x = ((gx + 0.5) * stepX).toInt().coerceIn(0, width - 1)
                val p = (rowBase + x) * 4
                val r = rgba[p].toInt() and 0xFF
                val g = rgba[p + 1].toInt() and 0xFF
                val b = rgba[p + 2].toInt() and 0xFF
                samples[index] = (29 * b + 150 * g + 77 * r) shr 8
                index++
            }
        }

        if (havePrevious) {
            var sum = 0L
            var changed = 0
            for (i in samples.indices) {
                val diff = samples[i] - previous[i]
                val abs = if (diff < 0) -diff else diff
                sum += abs
                if (abs >= deltaThreshold) changed++
            }
            lastMeanAbsDiff = sum.toDouble() / samples.size
            lastChangedFraction = changed.toDouble() / samples.size
            val moved = lastMeanAbsDiff >= meanAbsThreshold || lastChangedFraction >= changedFraction
            if (moved) lastChangeAtMs = nowMs
        } else {
            lastChangeAtMs = nowMs
            havePrevious = true
        }
        System.arraycopy(samples, 0, previous, 0, samples.size)
        lastFrameAtMs = nowMs
        return evaluate(nowMs)
    }

    /** 由看门狗周期调用：帧停了也要能报出来。 */
    fun onNoFrame(nowMs: Long): State = evaluate(nowMs)

    private fun evaluate(nowMs: Long): State {
        val noFrames = lastFrameAtMs > 0L && nowMs - lastFrameAtMs >= noFrameAfterMs
        if (noFrames) {
            return State(true, REASON_NO_FRAMES, nowMs - lastFrameAtMs)
        }
        val stillForMs = nowMs - lastChangeAtMs
        return if (stillForMs >= freezeAfterMs) {
            State(true, REASON_NO_CHANGE, stillForMs)
        } else {
            State(false, null, stillForMs)
        }
    }

    companion object {
        const val REASON_NO_CHANGE = "no_change"
        const val REASON_NO_FRAMES = "no_frames"

        fun reasonLabel(reason: String?): String = when (reason) {
            REASON_NO_CHANGE -> "画面无变化"
            REASON_NO_FRAMES -> "采集已停摆"
            else -> "正常"
        }
    }
}
