package com.kanami.reporter.core

import android.content.Context
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 识别引擎：加载 assets 内的手机模板（`.mobile.krt`，含秒值变体），对归一化帧做 ZNCC，
 * 组装阵营信号并驱动状态机。全部方法线程安全（内部锁覆盖匹配 + 状态推进）。
 *
 * 除 15 个状态模板外，还支持两组辅助模板：
 * - `side_attacker` / `side_defender`：购买横幅副标题，判断攻/守方
 * - `score_us.d0..d7` / `score_enemy.d0..d7`：左右比分板的数字，直接读出比分
 */
class RecognitionEngine(private val context: Context) {

    class Detection(
        val stateId: StateId?,
        val stateTimeMs: Long,
        val round: Int,
        val side: Int,
        val scores: DoubleArray,
        val threshold: Double,
        val stateLabel: String,
        /** 从画面读到的比分（读不到为 null，展示时回退到估算值）。 */
        val scoreUs: Int?,
        val scoreEnemy: Int?,
        /** 估算的本阶段剩余秒数。 */
        val remainingSeconds: Int?,
        /** 按回合结算累计的估算比分。 */
        val estScoreUs: Int,
        val estScoreEnemy: Int
    )

    private val lock = Any()
    private val stateModels = arrayOfNulls<MutableList<TemplateModel>>(ReporterStates.Count)
    private val sideModels = arrayOfNulls<TemplateModel>(2)
    private val scoreModels = arrayOfNulls<MutableList<ScoreModel>>(2)
    private val stateMachine = ReporterStateMachine()
    private val normalized = FrameProcessing.NormalizedFrame(ByteArray(0), 0)
    private val scoreStabilizer = ScoreStabilizer()
    private var frameIndex = 0L

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

    val isRunning: Boolean get() = running.get()

    var loadedTemplateCount: Int = 0
        private set

    var loadedScoreTemplateCount: Int = 0
        private set

    /** 模板文件名（不含扩展名）→ StateId 映射；不在表内的前缀按前缀匹配到状态。 */
    fun loadTemplates() {
        synchronized(lock) {
            for (i in 0 until ReporterStates.Count) stateModels[i] = mutableListOf()
            sideModels[0] = null
            sideModels[1] = null
            scoreModels[0] = mutableListOf()
            scoreModels[1] = mutableListOf()

            val assets = context.assets
            val files = assets.list("templates")?.sorted() ?: emptyList()
            var loadedCount = 0
            var scoreCount = 0
            for (file in files) {
                if (!file.endsWith(".krt")) continue
                val base = file.removeSuffix(".krt")
                val scoreTarget = resolveScoreGroup(base)
                val stateIndex = if (scoreTarget < 0) resolveStateIndex(base) else -1
                if (scoreTarget < 0 && stateIndex == null) continue
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
                when {
                    scoreTarget >= 0 -> {
                        val digitPart = base.substringAfterLast(".d").substringBefore(".")
                        val digit = digitPart.toIntOrNull() ?: continue
                        scoreModels[scoreTarget]!!.add(ScoreModel(digit, model))
                        scoreCount++
                    }

                    stateIndex == SIDE_ATTACKER -> sideModels[0] = model
                    stateIndex == SIDE_DEFENDER -> sideModels[1] = model
                    else -> stateModels[stateIndex!!]!!.add(model)
                }
                loadedCount++
            }
            loadedTemplateCount = loadedCount
            loadedScoreTemplateCount = scoreCount
            scoreStabilizer.reset()
            stateMachine.reset()
        }
    }

    /** 返回 0=我方比分板 1=对方比分板，非比分模板返回 -1。 */
    private fun resolveScoreGroup(baseName: String): Int = when {
        baseName == ReporterStates.ScoreUsTemplatePrefix ||
            baseName.startsWith("${ReporterStates.ScoreUsTemplatePrefix}.") -> 0

        baseName == ReporterStates.ScoreEnemyTemplatePrefix ||
            baseName.startsWith("${ReporterStates.ScoreEnemyTemplatePrefix}.") -> 1

        else -> -1
    }

    private fun resolveStateIndex(baseName: String): Int? {
        ReporterStates.Names.forEachIndexed { index, name ->
            if (baseName == name || baseName.startsWith("$name.")) return index
        }
        // 阵营辅助模板：side_attacker / side_defender（任意标签变体）
        return when {
            baseName == ReporterStates.AttackerSideTemplateName ||
                baseName.startsWith("${ReporterStates.AttackerSideTemplateName}.") -> SIDE_ATTACKER

            baseName == ReporterStates.DefenderSideTemplateName ||
                baseName.startsWith("${ReporterStates.DefenderSideTemplateName}.") -> SIDE_DEFENDER

            else -> null
        }
    }

    fun start() {
        running.set(true)
        synchronized(lock) {
            stateMachine.reset()
            scoreStabilizer.reset()
        }
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

            // 比分变化很慢，每 ScoreReadInterval 帧读一次即可（省 CPU）
            frameIndex++
            val readScoreNow = frameIndex % ScoreReadInterval == 1L
            val scoreUs = readScore(gray, 0, readScoreNow)
            val scoreEnemy = readScore(gray, 1, readScoreNow)

            val detection = Detection(
                stateId = stateMachine.currentStateId,
                stateTimeMs = stateMachine.currentStateTimeMs,
                round = stateMachine.currentRound,
                side = stateMachine.currentSide,
                scores = scores,
                threshold = threshold,
                stateLabel = stateMachine.currentStateId?.let { ReporterStates.DisplayNames[it.id] } ?: "",
                scoreUs = scoreUs,
                scoreEnemy = scoreEnemy,
                remainingSeconds = stateMachine.estimatedRemainingSeconds(),
                estScoreUs = stateMachine.estimatedScoreUs,
                estScoreEnemy = stateMachine.estimatedScoreEnemy
            )
            lastDetection = detection
            return detection
        }
    }

    /** 调试用：拷一份归一化灰度帧（与模板同坐标系）与内容高度。 */
    fun snapshotNormalizedGray(): Pair<ByteArray, Int>? = synchronized(lock) {
        if (normalized.gray.isEmpty()) return null
        normalized.gray.copyOf() to normalized.contentHeight
    }

    /**
     * 读一侧比分：该组数字模板里取最高分，命中阈值才算读出。
     *
     * 用**二值形状**打分（[FrameProcessing.scoreBinary]）而不是灰度 ZNCC：比分数字在半透明
     * 底板上，灰度相关区分度不足（0 与 3/6 能到 0.8），二值化后可稳定区分。
     * 单帧读数会上报，但对外只暴露 [ScoreStabilizer] 稳定后的值，避免数字闪跳。
     */
    private fun readScore(gray: ByteArray, group: Int, readNow: Boolean): Int? {
        if (!readNow) return scoreStabilizer.current(group)
        val models = scoreModels[group] ?: return null
        if (models.isEmpty()) return null
        var bestDigit: Int? = null
        var bestScore = -1.0
        for (m in models) {
            val s = FrameProcessing.scoreBinary(m.model, gray)
            if (s > bestScore) {
                bestScore = s
                bestDigit = m.digit
            }
        }
        return scoreStabilizer.accept(group, if (bestScore >= threshold) bestDigit else null)
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

    class ScoreModel(val digit: Int, val model: TemplateModel)

    /**
     * 比分稳定器：连续 [CONFIRM_FRAMES] 帧读到同一个数字才更新对外读数；
     * 连续 [CLEAR_FRAMES] 帧读不到就清空（例如打开全屏购买菜单时比分被遮挡）。
     */
    private class ScoreStabilizer {
        private val candidate = IntArray(2) { -1 }
        private val candidateHits = IntArray(2)
        private val committed = arrayOfNulls<Int>(2)
        private val missCount = IntArray(2)

        fun current(group: Int): Int? = committed[group]

        fun reset() {
            for (i in 0..1) {
                candidate[i] = -1
                candidateHits[i] = 0
                committed[i] = null
                missCount[i] = 0
            }
        }

        fun accept(group: Int, digit: Int?): Int? {
            if (digit == null) {
                missCount[group]++
                if (missCount[group] >= CLEAR_FRAMES) {
                    committed[group] = null
                    candidate[group] = -1
                    candidateHits[group] = 0
                }
                return committed[group]
            }
            missCount[group] = 0
            if (committed[group] == digit) {
                candidate[group] = -1
                candidateHits[group] = 0
                return committed[group]
            }
            if (candidate[group] == digit) {
                candidateHits[group]++
            } else {
                candidate[group] = digit
                candidateHits[group] = 1
            }
            if (candidateHits[group] >= CONFIRM_FRAMES) {
                committed[group] = digit
                candidate[group] = -1
                candidateHits[group] = 0
            }
            return committed[group]
        }

        private companion object {
            const val CONFIRM_FRAMES = 3
            /** 连续这么多次读不到比分才清空（打开全屏购买菜单会遮挡，别一秒就清）。 */
            const val CLEAR_FRAMES = 100
        }
    }

    companion object {
        private const val SIDE_ATTACKER = 100
        private const val SIDE_DEFENDER = 101

        /** 每多少帧读一次比分（比分变化慢，不需要每帧读）。 */
        private const val ScoreReadInterval = 3L
    }
}
