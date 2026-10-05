package com.kanami.reporter.status

import android.os.SystemClock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/** 一次播报事件（id 如 event_round_ingame_last_40s）。 */
data class EventRecord(val id: String, val label: String, val atMs: Long)

/** 一条模板匹配结果，label 为中文状态名。 */
data class TemplateScore(val label: String, val score: Double, val hit: Boolean)

/** 对局信息。 */
data class MatchInfo(
    val stateLabel: String = "",
    val stateMs: Long = 0,
    val round: Int = 0,
    /** 0=未知 1=攻方 2=守方 */
    val side: Int = 0,
    val remainingSeconds: Int? = null,
    /** 比分（从画面读取，读不到为 null）。 */
    val scoreUs: Int? = null,
    val scoreEnemy: Int? = null,
    /** 比分（按回合结算累计的估算值）。 */
    val estScoreUs: Int = 0,
    val estScoreEnemy: Int = 0
)

/** 采集链路健康度。 */
data class CaptureInfo(
    val panelWidth: Int = 0,
    val panelHeight: Int = 0,
    val frameWidth: Int = 0,
    val frameHeight: Int = 0,
    /** 采集到的帧是否为横屏（手机 HUD 只在横屏下做匹配）。 */
    val landscape: Boolean = true,
    val fps: Int = 0,
    val frames: Long = 0,
    val lastFrameAtMs: Long = 0,
    val frozen: Boolean = false,
    /** "no_change" 画面静止 / "no_frames" 采集停摆 */
    val frozenReason: String? = null,
    val frozenForMs: Long = 0,
    val error: String? = null
)

data class RecognitionStatus(
    val running: Boolean = false,
    val capture: CaptureInfo = CaptureInfo(),
    val match: MatchInfo = MatchInfo(),
    val threshold: Double = 0.9,
    val scores: List<TemplateScore> = emptyList(),
    val events: List<EventRecord> = emptyList(),
    val foregroundPackage: String? = null,
    val voicePlaying: String? = null,
    /** 最近一次「保存帧」等操作的结果提示。 */
    val notice: String? = null,
    val updatedAtMs: Long = 0
) {
    /** 画面是否卡住（用于悬浮窗与界面提示）。 */
    val screenStalled: Boolean get() = capture.frozen
}

/**
 * 进程内状态总线：采集服务写、界面/悬浮窗读。取代原来的 RecognitionBus。
 *
 * 采集线程每帧都会推状态，但只在必要时（状态变化或超过节流间隔）才真正发新值。
 */
object StatusHub {

    private const val EVENT_CAPACITY = 4

    private val _status = MutableStateFlow(RecognitionStatus())
    val status: StateFlow<RecognitionStatus> = _status

    @Volatile
    private var frameDumpRequested = false

    fun reset(running: Boolean) {
        _status.value = RecognitionStatus(
            running = running,
            threshold = _status.value.threshold,
            foregroundPackage = _status.value.foregroundPackage,
            updatedAtMs = SystemClock.elapsedRealtime()
        )
    }

    fun update(block: (RecognitionStatus) -> RecognitionStatus) {
        _status.update { block(it).copy(updatedAtMs = SystemClock.elapsedRealtime()) }
    }

    fun updateCapture(block: (CaptureInfo) -> CaptureInfo) =
        update { it.copy(capture = block(it.capture)) }

    fun updateMatch(block: (MatchInfo) -> MatchInfo) =
        update { it.copy(match = block(it.match)) }

    fun setScores(scores: List<TemplateScore>, threshold: Double) =
        update { it.copy(scores = scores, threshold = threshold) }

    fun setRunning(running: Boolean) =
        update { it.copy(running = running) }

    fun setCaptureError(message: String?) =
        updateCapture { it.copy(error = message) }

    fun setThreshold(threshold: Double) =
        update { it.copy(threshold = threshold) }

    fun setForegroundPackage(pkg: String?) =
        update { it.copy(foregroundPackage = pkg) }

    fun setVoicePlaying(label: String?) =
        update { it.copy(voicePlaying = label) }

    fun setNotice(notice: String?) =
        update { it.copy(notice = notice) }

    fun pushEvent(id: String, label: String) {
        update {
            val record = EventRecord(id, label, SystemClock.elapsedRealtime())
            it.copy(events = (listOf(record) + it.events).take(EVENT_CAPACITY))
        }
    }

    /** 请求在下一帧保存原始帧 + 归一化帧（调试用）。 */
    fun requestFrameDump() {
        frameDumpRequested = true
        setNotice("已请求保存当前帧，下一帧写入…")
    }

    fun consumeFrameDumpRequest(): Boolean {
        if (!frameDumpRequested) return false
        frameDumpRequested = false
        return true
    }

    val frameDumpPending: Boolean get() = frameDumpRequested
}
