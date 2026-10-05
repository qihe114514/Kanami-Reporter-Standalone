package com.kanami.reporter.core

import android.content.Context
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 识别引擎：加载 assets 内的手机模板（`.mobile.krt`，含秒值变体），对归一化帧做 ZNCC，
 * 组装阵营信号并驱动状态机。全部方法线程安全（内部锁覆盖匹配 + 状态推进）。
 */
class RecognitionEngine(private val context: Context) {

    class Detection(
        val stateId: StateId?,
        val stateTimeMs: Long,
        val round: Int,
        val side: Int,
        val scores: DoubleArray,
        val threshold: Double
    )

    private val lock = Any()
    private val stateModels = arrayOfNulls<MutableList<TemplateModel>>(ReporterStates.Count)
    private val sideModels = arrayOfNulls<TemplateModel>(2)
    private val stateMachine = ReporterStateMachine()
    private val normalized = FrameProcessing.NormalizedFrame(ByteArray(0), 0)

    var threshold: Double = ReporterStates.DefaultThreshold

    /** 状态机触发的事件（event_*）回调，在采集线程调用。 */
    var onEvent: ((String) -> Unit)? = null

    init {
        stateMachine.eventTriggered = { eventId -> onEvent?.invoke(eventId) }
    }

    @Volatile
    var lastDetection: Detection? = null
        private set

    private val running = AtomicBoolean(false)

    /** 模板文件名（不含扩展名）→ StateId 映射；不在表内的前缀按前缀匹配到状态。 */
    fun loadTemplates() {
        synchronized(lock) {
            for (i in 0 until ReporterStates.Count) stateModels[i] = mutableListOf()
            sideModels[0] = null
            sideModels[1] = null

            val assets = context.assets
            val files = assets.list("templates")?.sorted() ?: emptyList()
            var loadedCount = 0
            for (file in files) {
                if (!file.endsWith(".krt")) continue
                val base = file.removeSuffix(".krt")
                val stateIndex = resolveStateIndex(base) ?: continue
                val bytes = try {
                    assets.open("templates/$file").use { it.readBytes() }
                } catch (e: IOException) {
                    continue
                }
                val model = try {
                    KrtTemplateStore.load(base, bytes)
                } catch (e: IOException) {
                    continue
                }
                if (stateIndex == SIDE_ATTACKER) {
                    sideModels[0] = model
                } else if (stateIndex == SIDE_DEFENDER) {
                    sideModels[1] = model
                } else {
                    stateModels[stateIndex]!!.add(model)
                }
                loadedCount++
            }
            loadedTemplateCount = loadedCount
            stateMachine.reset()
        }
    }

    var loadedTemplateCount: Int = 0
        private set

    private fun resolveStateIndex(baseName: String): Int {
        ReporterStates.Names.forEachIndexed { index, name ->
            if (baseName == name || baseName.startsWith("$name.")) return index
        }
        // 阵营辅助模板：side_attacker / side_defender（任意标签变体）
        return when {
            baseName == ReporterStates.AttackerSideTemplateName ||
                baseName.startsWith("${ReporterStates.AttackerSideTemplateName}.") -> SIDE_ATTACKER
            baseName == ReporterStates.DefenderSideTemplateName ||
                baseName.startsWith("${ReporterStates.DefenderSideTemplateName}.") -> SIDE_DEFENDER
            else -> -1
        }
    }

    fun start() {
        running.set(true)
        synchronized(lock) { stateMachine.reset() }
    }

    fun stop() {
        running.set(false)
    }

    /**
     * 处理一帧 RGBA_8888 原始像素。返回最新 Detection；服务未启动时返回 null。
     */
    fun processFrame(rgba: ByteArray, srcWidth: Int, srcHeight: Int, timestampMs: Long): Detection? {
        if (!running.get()) return null
        synchronized(lock) {
            FrameProcessing.normalize(rgba, srcWidth, srcHeight, normalized)
            val gray = normalized.gray

            val scores = DoubleArray(ReporterStates.Count) { -1.0 }
            val matches = BooleanArray(ReporterStates.Count)
            for (i in 0 until ReporterStates.Count) {
                val models = stateModels[i] ?: continue
                var best = -1.0
                for (m in models) {
                    val s = FrameProcessing.score(m, gray)
                    if (s > best) best = s
                }
                scores[i] = best
                matches[i] = best >= threshold
            }

            val sideSignal = buildSideSignal(gray)
            stateMachine.processFrame(timestampMs, matches, scores, sideSignal)

            val detection = Detection(
                stateId = stateMachine.currentStateId,
                stateTimeMs = stateMachine.currentStateTimeMs,
                round = stateMachine.currentRound,
                side = stateMachine.currentSide,
                scores = scores,
                threshold = threshold
            )
            lastDetection = detection
            return detection
        }
    }

    private fun buildSideSignal(gray: ByteArray): ReporterStateMachine.SideSignal? {
        val attacker = sideModels[0]
        val defender = sideModels[1]
        if (attacker == null && defender == null) return null
        val sideThreshold = (threshold - ReporterStates.SideThresholdMargin)
            .coerceIn(ReporterStates.SideThresholdFloor, 1.0)
        val attackerScore = if (attacker != null) FrameProcessing.score(attacker, gray) else -1.0
        val defenderScore = if (defender != null) FrameProcessing.score(defender, gray) else -1.0
        return ReporterStateMachine.SideSignal(
            attackerScore >= sideThreshold,
            defenderScore >= sideThreshold,
            attackerScore,
            defenderScore
        )
    }
}

private const val SIDE_ATTACKER = 100
private const val SIDE_DEFENDER = 101
