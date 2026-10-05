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
import androidx.core.content.IntentCompat
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
        /** 只留 2 张：每张 RGBA_8888 缓冲就是 14MB，4 张会白占几十 MB。 */
        private const val MAX_IMAGES = 2
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

    private val mainHandler = Handler(Looper.getMainLooper())
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val delayedRebuild = Runnable { ensureDisplaySize(force = true) }

    private var lastProcessedAt = 0L
    private var processing = false
    private var stopped = false

    /** 被节流丢掉的帧数（收走并关闭，不是留着不管）。 */
    private var droppedFrames = 0L

    /** 复用的一帧 RGBA 缓冲（只在采集线程访问）。 */
    private var pixelBuffer: ByteArray? = null

    private var frameCount = 0L
    private var fpsWindowStart = 0L
    private var fpsWindowFrames = 0
    private var currentFps = 0

    private var lastStatusAt = 0L
    private var lastStateLabel = ""
    private var lastRebuildAtMs = 0L
    private var loggedFrames = 0

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
        val nowElapsed = SystemClock.elapsedRealtime()
        if (processing || nowElapsed - lastProcessedAt < TARGET_FPS_MS) {
            // 丢帧也必须把图像收走！
            //
            // ImageReader 的空闲 buffer 数是固定的（MAX_IMAGES）。以前这里直接 return，
            // 被节流丢掉的那些帧就一直以 "已入队未取走" 的状态占着 buffer；占满之后 producer
            // 再也拿不到空位，onImageAvailable 就彻底不再触发 —— 采集看起来"死了"。
            // acquireLatestImage() 会把队列里其余图像一并关闭，正好用来清空。
            // 症状完全对得上：只有屏幕旋转/重建采集面（换了新 ImageReader，自带新 buffer）
            // 之后才会再蹦出几帧，然后又不涨了。
            reader.acquireLatestImage()?.close()
            droppedFrames++
            return@OnImageAvailableListener
        }
        processing = true
        lastProcessedAt = nowElapsed
        var image: Image? = null
        val startedAt = nowElapsed
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
            maybeDumpFrame(rgba, width, height)
            publishFrame(width, height, landscape, detection, SystemClock.elapsedRealtime() - startedAt)
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

    /**
     * 启动顺序是 Android 14+ 的硬约束，**两个方向的要求互相咬合**，只有一个顺序能过：
     *
     * - `startForeground(type = mediaProjection)` 要求调用方已持有录屏授权（appop `PROJECT_MEDIA`），
     *   这个 appop 是用户刚才在授权弹窗里点「开始录制」时授予的，所以**授权完立刻调用是通的**；
     * - `getMediaProjection()` 反过来要求**已经**有一个该类型的前台服务在跑。
     *
     * 于是唯一可行顺序是：`startForeground` → `getMediaProjection` → `registerCallback`
     * → `createVirtualDisplay`。反过来写会得到
     * "Media projections require a foreground service of type ... MEDIA_PROJECTION"。
     *
     * 5 秒死线不冲突：下面所有失败分支都在超时之前 `stopSelf()`，系统不会判 ANR。
     */
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val foregroundOk = try {
            startForeground(
                NOTIFICATION_ID, buildNotification(),
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            )
            true
        } catch (e: Exception) {
            DebugLog.log("capture", "进入前台服务失败：${e.javaClass.simpleName}: ${e.message}")
            false
        }

        // 授权数据优先取进程内交接（见 ProjectionHandoff 里对 Android 13+ getParcelableExtra 的说明）
        val handoff = ProjectionHandoff.take()
        val resultCode = handoff?.first ?: intent?.getIntExtra(EXTRA_RESULT_CODE, 0) ?: 0
        val resultData = handoff?.second
            ?: intent?.let { IntentCompat.getParcelableExtra(it, EXTRA_RESULT_DATA, Intent::class.java) }

        if (resultData == null) {
            fail("没有拿到录屏授权，请重新点「开始识别」")
            stopSelf()
            return START_NOT_STICKY
        }
        if (!foregroundOk) {
            fail("无法进入前台服务（多半是录屏授权已失效），请重新点「开始识别」")
            stopSelf()
            return START_NOT_STICKY
        }
        DebugLog.log("capture", "收到录屏授权：resultCode=$resultCode，来源=${if (handoff != null) "进程内交接" else "Intent extra"}")

        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val mp = try {
            manager.getMediaProjection(resultCode, resultData)
        } catch (e: Exception) {
            DebugLog.log("capture", "getMediaProjection 异常：${e.javaClass.simpleName}: ${e.message}")
            null
        }
        if (mp == null) {
            fail("系统没有返回录屏会话，请重新点「开始识别」")
            stopSelf()
            return START_NOT_STICKY
        }

        projection = mp
        try {
            mp.registerCallback(projectionCallback, mainHandler)
        } catch (e: Exception) {
            DebugLog.log("capture", "注册录屏回调失败：${e.javaClass.simpleName}: ${e.message}")
        }

        // 从这里开始整段兜住：任何一步抛异常都要留下原因，而不是变成一个没有栈的"闪退"。
        try {
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
            DebugLog.log("capture", "准备采集面（面板 ${realDisplayMetrics()?.let { "${it.widthPixels}x${it.heightPixels}" } ?: "未知"}）")
            ensureDisplaySize(force = true)
            DebugLog.log("capture", "采集面就绪，创建悬浮窗")

            val controller = OverlayController(this, settings).also { overlay = it }
            val overlayShown = controller.attach()
            val overlayReason = controller.lastError
            StatusHub.setNotice(
                if (overlayShown) {
                    "采集已启动；悬浮窗已显示，可拖到顺手的位置"
                } else {
                    "采集已启动，但悬浮窗没显示：${overlayReason ?: "未知原因"}"
                },
                // 用户自己关掉的不算问题；他开着却出不来才要提醒
                error = !overlayShown && settings.showOverlay
            )

            DebugLog.log("capture", "开始识别：模板 ${engine.loadedTemplateCount} 个（含比分 ${engine.loadedScoreTemplateCount} 个）")
        } catch (e: Exception) {
            DebugLog.log("capture", "启动异常：${e.javaClass.simpleName}: ${e.message}\n${e.stackTraceToString()}")
            fail("启动采集时出错（${e.javaClass.simpleName}: ${e.message}）")
            stopSelf()
            return START_NOT_STICKY
        }
        // 不用 START_STICKY：录屏授权不能跨进程恢复，进程被杀后系统拉起来的服务
        // 一定过不了 startForeground（日志里那次 "Starting FGS ... requires permissions"
        // 就是系统重启服务的结果），只会白写一条失败日志。
        return START_NOT_STICKY
    }

    /** 启动链路失败：写日志 + 把原因推到界面上，别让用户对着一个安静的应用发呆。 */
    private fun fail(message: String) {
        DebugLog.log("capture", "启动失败：$message")
        StatusHub.reset(running = false)
        StatusHub.setNotice("识别启动失败：$message", error = true)
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
            // 首次建面失败是真致命（采集起不来），而且这里最常见的成因是录屏授权已被系统回收，
            // 必须让用户知道原因，否则就是"点了开始识别什么都没有"。
            val created = try {
                projection?.createVirtualDisplay(
                    "KanamiCapture", width, height, densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                    newReader.surface, null, handler
                )
            } catch (e: Exception) {
                DebugLog.log("capture", "创建采集面异常：${e.javaClass.simpleName}: ${e.message}")
                null
            }
            if (created == null) {
                newReader.close()
                DebugLog.log("capture", "创建采集面失败，采集无法开始")
                StatusHub.setCaptureError("创建采集面失败（录屏授权可能已失效）")
                StatusHub.setNotice("采集面创建失败，请在「运行」页重新点「开始识别」", error = true)
                mainHandler.post { stopSelf() }
                return
            }
            virtualDisplay = created
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

    /**
     * 把 ImageReader 的 plane 拷成紧凑 RGBA 字节数组（处理 rowStride 行填充）。
     *
     * 缓冲复用：这块手机 1272×2772 一帧就是 14MB，按 15fps 每帧 new 一个等于每秒两百多 MB 的垃圾，
     * 全都要 GC 去收。采集线程独占这个缓冲，不需要加锁。
     */
    private fun copyPixels(image: Image, width: Int, height: Int): ByteArray? {
        if (width <= 0 || height <= 0) return null
        val plane = image.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val rowStride = plane.rowStride
        val pixelStride = plane.pixelStride
        val needed = width * height * 4
        val bytes = if (pixelBuffer?.size == needed) {
            pixelBuffer!!
        } else {
            ByteArray(needed).also { pixelBuffer = it }
        }
        if (pixelStride == 4 && rowStride == width * 4 && buffer.remaining() >= needed) {
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
            "帧 #$frameCount ${width}x$height 处理 ${costMs}ms fps=$currentFps" +
                " 丢帧=$droppedFrames 横屏=$landscape"
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
        if (!stateChanged && nowElapsed - lastStatusAt < STATUS_MIN_INTERVAL_MS) return
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
        if (detection != null) lastStateLabel = detection.stateLabel
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
        // onDestroy 与录屏回调都会走到这里，必须幂等。
        if (stopped) return
        stopped = true
        engine.stop()
        StatusHub.setRunning(false)
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
