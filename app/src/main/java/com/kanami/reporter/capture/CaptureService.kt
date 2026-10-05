package com.kanami.reporter.capture

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.util.DisplayMetrics
import android.view.Display
import com.kanami.reporter.KanamiApp
import com.kanami.reporter.R
import com.kanami.reporter.audio.VoicePlayer
import com.kanami.reporter.core.RecognitionEngine
import com.kanami.reporter.core.ReporterStates
import com.kanami.reporter.core.VoiceTable
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.debug.FrameDump
import com.kanami.reporter.overlay.OverlayController
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.StatusHub
import com.kanami.reporter.status.TemplateScore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * 前台采集服务：MediaProjection 屏幕采集 → RGBA 帧 → 归一化 + 模板匹配 → 状态机 → 语音。
 *
 * **采集几何（2026-10-06 修复的关键点）**：虚拟屏尺寸必须等于「被镜像的那块屏幕」的真实尺寸，
 * 否则系统会给镜像内容加黑边或缩放，整幅 HUD 平移/缩放，而 ZNCC 只搜 ±1 像素 —— 所有模板
 * 分数会一起崩掉，状态机永远不命中（这就是"手机版识别不到对局数据"的根因）。
 * 因此这里：
 * - 用 `Display.getRealMetrics` 取面板真实尺寸（不是 `resources.displayMetrics` 的窗口区尺寸），
 *   也不再强行把尺寸凑成横屏；
 * - 监听 `onCapturedContentResize`（Android 14+）与显示变化（低版本），尺寸一变就重建采集面，
 *   而不是像以前那样一次定死、转到游戏也不管；
 * - 非横屏（竖屏）帧不做匹配，只用于画面活性判断，避免拿手机桌面去套游戏 HUD 模板。
 *
 * 节拍控制：处理耗时超过采集间隔时直接丢帧（只处理最新帧），与 PC 端"队列只保留最新帧"一致。
 */
class CaptureService : Service() {

    companion object {
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        const val CHANNEL_ID = "capture"
        const val NOTIFICATION_ID = 1
        const val TARGET_FPS_MS = 66L

        private const val TAG = "CaptureService"
        private const val REBUILD_MIN_INTERVAL_MS = 300L
        private const val STATUS_MIN_INTERVAL_MS = 200L
        private const val FPS_WINDOW_MS = 2000L
        private const val FREEZE_WATCHDOG_MS = 800L
        private const val MAX_IMAGES = 4
    }

    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var imageReader: ImageReader? = null
    private var workerThread: HandlerThread? = null
    private var workerHandler: Handler? = null
    private var displayManager: DisplayManager? = null
    private var displayListenerRegistered = false

    private lateinit var engine: RecognitionEngine
    private lateinit var voicePlayer: VoicePlayer
    private lateinit var settings: Settings
    private var overlay: OverlayController? = null

    private val freezeDetector = FreezeDetector()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val delayedRebuild = Runnable { ensureDisplaySize(force = true) }

    private var lastProcessedAt = 0L
    private var processing = false

    private var frameCount = 0L
    private var fpsWindowStart = 0L
    private var fpsWindowFrames = 0
    private var currentFps = 0

    private var lastStatusAt = 0L
    private var lastFrozen = false
    private var lastFrozenReason: String? = null
    private var lastStateLabel = ""
    private var lastRebuildAtMs = 0L
    private var loggedFrames = 0

    private val watchdog = object : Runnable {
        override fun run() {
            if (imageReader != null) {
                val state = freezeDetector.onNoFrame(SystemClock.elapsedRealtime())
                maybePublishFreeze(state)
            }
            mainHandler.postDelayed(this, FREEZE_WATCHDOG_MS)
        }
    }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = Unit
        override fun onDisplayRemoved(displayId: Int) = Unit
        override fun onDisplayChanged(displayId: Int) {
            if (displayId != Display.DEFAULT_DISPLAY) return
            DebugLog.log("capture", "显示变化，检查采集几何")
            ensureDisplaySize(force = false)
            overlay?.clampToScreen()
        }
    }

    private val projectionCallback = object : MediaProjection.Callback() {
        override fun onStop() {
            DebugLog.log("capture", "录屏授权被系统回收，停止采集")
            stopCapture()
            stopSelf()
        }

        override fun onCapturedContentResize(width: Int, height: Int) {
            DebugLog.log("capture", "onCapturedContentResize ${width}x$height")
            rebuildVirtualDisplay(width, height, "captured-content-resize")
        }
    }

    private val frameListener = ImageReader.OnImageAvailableListener { reader ->
        val wallNow = System.currentTimeMillis()
        if (processing || wallNow - lastProcessedAt < TARGET_FPS_MS) return@OnImageAvailableListener
        processing = true
        lastProcessedAt = wallNow
        var image: Image? = null
        val startedAt = SystemClock.elapsedRealtime()
        try {
            image = reader.acquireLatestImage() ?: return@OnImageAvailableListener
            val width = image.width
            val height = image.height
            val rgba = copyPixels(image, width, height) ?: return@OnImageAvailableListener

            frameCount++
            fpsWindowFrames++
            if (loggedFrames < 3) {
                loggedFrames++
                DebugLog.log("capture", "第 $loggedFrames 帧 ${width}x$height（reader=${reader.width}x${reader.height}）")
            }

            val landscape = width >= height
            val detection = if (landscape) engine.processFrame(rgba, width, height, wallNow) else null
            val freeze = freezeDetector.onFrame(rgba, width, height, SystemClock.elapsedRealtime())
            maybeDumpFrame(rgba, width, height)
            publishFrame(width, height, landscape, detection, freeze, SystemClock.elapsedRealtime() - startedAt)
        } catch (e: Exception) {
            DebugLog.log("capture", "帧处理异常：${e.javaClass.simpleName}: ${e.message}")
            StatusHub.setCaptureError("${e.javaClass.simpleName}: ${e.message}")
        } finally {
            try {
                image?.close()
            } catch (_: Exception) {
            }
            processing = false
        }
    }

    override fun onCreate() {
        super.onCreate()
        val app = application as KanamiApp
        settings = app.settings
        engine = app.engine
        engine.threshold = settings.threshold
        engine.onEvent = { eventId ->
            val label = VoiceTable.displayName(eventId)
            voicePlayer.playEvent(eventId)
            StatusHub.pushEvent(eventId, label)
            StatusHub.setVoicePlaying(label)
            val files = VoiceTable.fileNamesFor(eventId)
            DebugLog.log("voice", "播放事件 $eventId → ${files.firstOrNull() ?: "（无音频）"}")
        }
        voicePlayer = VoicePlayer(this)

        serviceScope.launch {
            StatusHub.status.collect { status -> overlay?.onStatus(status) }
        }
        serviceScope.launch {
            settings.revisions.collect { overlay?.onSettingsChanged() }
        }
        DebugLog.log("capture", "采集服务已创建")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val resultCode = intent?.getIntExtra(EXTRA_RESULT_CODE, 0) ?: 0
        val resultData = intent?.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)
        if (resultData == null) {
            DebugLog.log("capture", "缺少录屏授权数据，退出")
            stopSelf()
            return START_NOT_STICKY
        }

        startForeground(
            NOTIFICATION_ID, buildNotification(),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        )

        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val mp = manager.getMediaProjection(resultCode, resultData)
        if (mp == null) {
            DebugLog.log("capture", "getMediaProjection 返回 null，退出")
            stopSelf()
            return START_NOT_STICKY
        }
        projection = mp
        mp.registerCallback(projectionCallback, mainHandler)

        displayManager = getSystemService(Context.DISPLAY_SERVICE) as DisplayManager
        if (!displayListenerRegistered) {
            displayManager?.registerDisplayListener(displayListener, mainHandler)
            displayListenerRegistered = true
        }

        workerThread = HandlerThread("kanami-capture").also { it.start() }
        workerHandler = Handler(workerThread!!.looper)

        engine.start()
        engine.threshold = settings.threshold
        StatusHub.reset(running = true)
        loggedFrames = 0
        freezeDetector.reset()
        ensureDisplaySize(force = true)

        overlay = OverlayController(this, settings).also { it.attach() }

        mainHandler.removeCallbacks(watchdog)
        mainHandler.postDelayed(watchdog, FREEZE_WATCHDOG_MS)
        DebugLog.log("capture", "开始识别：模板 ${engine.loadedTemplateCount} 个（含比分 ${engine.loadedScoreTemplateCount} 个）")
        return START_STICKY
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        ensureDisplaySize(force = false)
    }

    // ---------- 采集几何 ----------

    /** 面板真实尺寸（当前朝向，含系统栏），不是 App 窗口区尺寸。 */
    private fun realDisplayMetrics(): DisplayMetrics? {
        val display = displayManager?.getDisplay(Display.DEFAULT_DISPLAY) ?: return null
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        display.getRealMetrics(metrics)
        if (metrics.widthPixels <= 0 || metrics.heightPixels <= 0) return null
        return metrics
    }

    private fun ensureDisplaySize(force: Boolean) {
        val metrics = realDisplayMetrics() ?: return
        val reader = imageReader
        val sameSize = reader != null &&
            reader.width == metrics.widthPixels && reader.height == metrics.heightPixels
        if (!force && sameSize) return
        rebuildVirtualDisplay(
            metrics.widthPixels,
            metrics.heightPixels,
            if (reader == null) "start" else "display-size-change",
            metrics.densityDpi
        )
    }

    /**
     * 按内容尺寸重建采集面：重建 ImageReader（尺寸固定）→ resize 虚拟屏 → 换 surface。
     * 旋转会在短时间内多次触发，这里做最小间隔节流。
     */
    private fun rebuildVirtualDisplay(
        width: Int,
        height: Int,
        reason: String,
        densityDpi: Int = realDisplayMetrics()?.densityDpi ?: DisplayMetrics.DENSITY_DEFAULT
    ) {
        if (width <= 0 || height <= 0) return
        val handler = workerHandler ?: return
        if (imageReader?.width == width && imageReader?.height == height) return
        val now = SystemClock.elapsedRealtime()
        if (virtualDisplay != null && now - lastRebuildAtMs < REBUILD_MIN_INTERVAL_MS) {
            mainHandler.removeCallbacks(delayedRebuild)
            mainHandler.postDelayed(delayedRebuild, REBUILD_MIN_INTERVAL_MS)
            return
        }
        lastRebuildAtMs = now

        val newReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, MAX_IMAGES).apply {
            setOnImageAvailableListener(frameListener, handler)
        }
        val oldReader = imageReader
        val vd = virtualDisplay
        if (vd == null) {
            virtualDisplay = projection?.createVirtualDisplay(
                "KanamiCapture", width, height, densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                newReader.surface, null, handler
            )
        } else {
            try {
                vd.resize(width, height, densityDpi)
                vd.surface = newReader.surface
            } catch (e: Exception) {
                DebugLog.log("capture", "虚拟屏 resize 失败：${e.javaClass.simpleName}: ${e.message}")
                StatusHub.setCaptureError("resize 失败：${e.message}")
            }
        }
        imageReader = newReader
        oldReader?.close()
        freezeDetector.reset()
        loggedFrames = 0

        DebugLog.log(
            "capture",
            "采集几何 ${width}x${height} @${densityDpi}dpi（$reason，横屏=${width >= height}）"
        )
        StatusHub.updateCapture {
            it.copy(
                panelWidth = width,
                panelHeight = height,
                frameWidth = width,
                frameHeight = height,
                landscape = width >= height,
                error = null
            )
        }
    }

    // ---------- 帧处理 ----------

    /** 把 ImageReader 的 plane 拷成紧凑 RGBA 字节数组（处理 rowStride 行填充）。 */
    private fun copyPixels(image: Image, width: Int, height: Int): ByteArray? {
        if (width <= 0 || height <= 0) return null
        val plane = image.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val rowStride = plane.rowStride
        val pixelStride = plane.pixelStride
        val bytes = ByteArray(width * height * 4)
        if (pixelStride == 4 && rowStride == width * 4 && buffer.remaining() >= bytes.size) {
            buffer.get(bytes)
            return bytes
        }
        var offset = 0
        for (row in 0 until height) {
            val rowStart = row * rowStride
            if (pixelStride == 4) {
                if (rowStart + width * 4 > buffer.capacity()) return null
                buffer.position(rowStart)
                buffer.get(bytes, offset, width * 4)
                offset += width * 4
            } else {
                for (col in 0 until width) {
                    val p = rowStart + col * pixelStride
                    if (p + 3 >= buffer.capacity()) return null
                    buffer.position(p)
                    bytes[offset++] = buffer.get()
                    bytes[offset++] = buffer.get()
                    bytes[offset++] = buffer.get()
                    bytes[offset++] = buffer.get()
                }
            }
        }
        return bytes
    }

    private fun maybeDumpFrame(rgba: ByteArray, width: Int, height: Int) {
        if (!StatusHub.consumeFrameDumpRequest()) return
        val raw = FrameDump.saveRaw(this, rgba, width, height)
        val normalized = engine.snapshotNormalizedGray()?.let { (gray, contentHeight) ->
            FrameDump.saveNormalized(this, gray, contentHeight)
        }
        val message = buildString {
            append("原始帧：")
            append(raw?.name ?: "失败")
            append("；归一化帧：")
            append(normalized?.name ?: "失败")
        }
        DebugLog.log("dump", message)
        StatusHub.setNotice(message)
        if (settings.debugMode) FrameDump.cleanupOld(this)
    }

    private fun publishFrame(
        width: Int,
        height: Int,
        landscape: Boolean,
        detection: RecognitionEngine.Detection?,
        freeze: FreezeDetector.State,
        costMs: Long
    ) {
        val nowElapsed = SystemClock.elapsedRealtime()
        if (fpsWindowStart == 0L) fpsWindowStart = nowElapsed
        if (nowElapsed - fpsWindowStart >= FPS_WINDOW_MS) {
            currentFps = (fpsWindowFrames * 1000L / (nowElapsed - fpsWindowStart)).toInt()
            fpsWindowStart = nowElapsed
            fpsWindowFrames = 0
        }

        DebugLog.logThrottled("frame", 2000L, "capture") {
            "帧 #$frameCount ${width}x$height 处理 ${costMs}ms fps=$currentFps " +
                "动的度量 meanAbs=${"%.2f".format(freezeDetector.lastMeanAbsDiff)}" +
                " changed=${"%.4f".format(freezeDetector.lastChangedFraction)}" +
                " 静止=${freeze.frozenForMs / 1000}s 横屏=$landscape"
        }
        if (detection != null) {
            DebugLog.logThrottled("scores", 2000L, "match") {
                val top = detection.scores
                    .mapIndexed { i, s -> ReporterStates.DisplayNames[i] to s }
                    .sortedByDescending { it.second }
                    .take(8)
                    .joinToString("  ") { "%.3f %s".format(it.second, it.first) }
                "阈值 ${"%.2f".format(detection.threshold)}  状态=${detection.stateLabel.ifEmpty { "未进入对局" }}  $top"
            }
        }

        val stateChanged = detection != null && detection.stateLabel != lastStateLabel
        val freezeChanged = freeze.frozen != lastFrozen || freeze.reason != lastFrozenReason
        if (!stateChanged && !freezeChanged && nowElapsed - lastStatusAt < STATUS_MIN_INTERVAL_MS) return
        lastStatusAt = nowElapsed

        val scores = detection?.let { d ->
            d.scores.mapIndexed { i, s ->
                TemplateScore(ReporterStates.DisplayNames[i], s, s >= d.threshold)
            }.sortedByDescending { it.score }.take(15)
        }

        StatusHub.update { status ->
            status.copy(
                running = true,
                capture = status.capture.copy(
                    frameWidth = width,
                    frameHeight = height,
                    landscape = landscape,
                    fps = currentFps,
                    frames = frameCount,
                    lastFrameAtMs = nowElapsed,
                    frozen = freeze.frozen,
                    frozenReason = freeze.reason,
                    frozenForMs = freeze.frozenForMs,
                    error = null
                ),
                match = if (detection != null) {
                    status.match.copy(
                        stateLabel = detection.stateLabel,
                        stateMs = detection.stateTimeMs,
                        round = detection.round,
                        side = detection.side,
                        remainingSeconds = detection.remainingSeconds,
                        scoreUs = detection.scoreUs,
                        scoreEnemy = detection.scoreEnemy,
                        estScoreUs = detection.estScoreUs,
                        estScoreEnemy = detection.estScoreEnemy
                    )
                } else {
                    status.match
                },
                scores = scores ?: status.scores,
                threshold = detection?.threshold ?: status.threshold
            )
        }
        lastFrozen = freeze.frozen
        lastFrozenReason = freeze.reason
        if (detection != null) lastStateLabel = detection.stateLabel
    }

    private fun maybePublishFreeze(state: FreezeDetector.State) {
        if (state.frozen == lastFrozen && state.reason == lastFrozenReason) return
        DebugLog.logThrottled("freeze", 3000L, "capture") {
            "画面状态变化：frozen=${state.frozen} reason=${state.reason} ${state.frozenForMs / 1000}s"
        }
        lastFrozen = state.frozen
        lastFrozenReason = state.reason
        StatusHub.updateCapture {
            it.copy(frozen = state.frozen, frozenReason = state.reason, frozenForMs = state.frozenForMs)
        }
    }

    // ---------- 生命周期 ----------

    private fun buildNotification(): Notification {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val channel = NotificationChannel(
            CHANNEL_ID, getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW
        )
        manager.createNotificationChannel(channel)
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(getString(R.string.notif_text))
            .setOngoing(true)
            .build()
    }

    private fun stopCapture() {
        engine.stop()
        StatusHub.setRunning(false)
        mainHandler.removeCallbacks(watchdog)
        mainHandler.removeCallbacks(delayedRebuild)
        overlay?.detach()
        overlay = null
        try {
            virtualDisplay?.release()
        } catch (_: Exception) {
        }
        try {
            imageReader?.close()
        } catch (_: Exception) {
        }
        try {
            projection?.stop()
        } catch (_: Exception) {
        }
        virtualDisplay = null
        imageReader = null
        projection = null
        workerThread?.quitSafely()
        workerThread = null
        workerHandler = null
        voicePlayer.release()
        DebugLog.log("capture", "采集已停止")
    }

    override fun onDestroy() {
        stopCapture()
        if (displayListenerRegistered) {
            displayManager?.unregisterDisplayListener(displayListener)
            displayListenerRegistered = false
        }
        serviceScope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
