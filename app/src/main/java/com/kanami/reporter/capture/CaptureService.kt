package com.kanami.reporter.capture

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
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
import android.util.Log
import com.kanami.reporter.R
import com.kanami.reporter.audio.VoicePlayer
import com.kanami.reporter.core.RecognitionEngine

/**
 * 前台采集服务：MediaProjection 屏幕采集 → RGBA 帧 → 归一化 + 模板匹配 → 状态机 → 语音。
 *
 * 节拍控制：处理耗时超过采集间隔时直接丢帧（只处理最新帧），与 PC 端"队列只保留
 * 最新帧"的策略一致，避免延迟累积。
 */
class CaptureService : Service() {

    companion object {
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        const val CHANNEL_ID = "capture"
        const val NOTIFICATION_ID = 1
        const val TARGET_FPS_MS = 66L

        const val ACTION_STATE = "com.kanami.reporter.CAPTURE_STATE"
        const val EXTRA_RUNNING = "running"
        const val EXTRA_ERROR = "error"

        private const val TAG = "CaptureService"
    }

    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var imageReader: ImageReader? = null
    private var workerThread: HandlerThread? = null
    private var workerHandler: Handler? = null

    private lateinit var engine: RecognitionEngine
    private lateinit var voicePlayer: VoicePlayer

    private var lastProcessedAt = 0L
    private var processing = false

    private val projectionCallback = object : MediaProjection.Callback() {
        override fun onStop() {
            stopCapture()
            stopSelf()
        }
    }

    private val frameListener = ImageReader.OnImageAvailableListener { reader ->
        val now = System.currentTimeMillis()
        if (processing || now - lastProcessedAt < TARGET_FPS_MS) return@OnImageAvailableListener
        processing = true
        lastProcessedAt = now
        var image: Image? = null
        try {
            image = reader.acquireLatestImage() ?: return@OnImageAvailableListener
            val plane = image.planes[0]
            val width = image.width
            val height = image.height
            val rowStride = plane.rowStride
            val pixelStride = plane.pixelStride
            val buffer = plane.buffer

            val rgba: ByteArray = if (pixelStride == 4 && rowStride == width * 4) {
                val bytes = ByteArray(width * height * 4)
                buffer.get(bytes)
                bytes
            } else {
                // 有行填充 / 非紧密布局：逐行拷贝
                val bytes = ByteArray(width * height * 4)
                var offset = 0
                for (row in 0 until height) {
                    buffer.position(row * rowStride)
                    if (pixelStride == 4) {
                        buffer.get(bytes, offset, width * 4)
                        offset += width * 4
                    } else {
                        for (col in 0 until width) {
                            buffer.position(row * rowStride + col * pixelStride)
                            bytes[offset++] = buffer.get()
                            bytes[offset++] = buffer.get()
                            bytes[offset++] = buffer.get()
                            bytes[offset++] = buffer.get()
                        }
                    }
                }
                bytes
            }

            val detection = engine.processFrame(rgba, width, height, now)
            // processFrame 内部推进状态机并已通过 onEvent 播语音
        } catch (e: Exception) {
            Log.e(TAG, "frame error", e)
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
        engine = (application as com.kanami.reporter.KanamiApp).engine
        engine.onEvent = { eventId ->
            voicePlayer.playEvent(eventId)
            RecognitionBus.publishEvent(eventId)
        }
        voicePlayer = VoicePlayer(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val resultCode = intent?.getIntExtra(EXTRA_RESULT_CODE, 0) ?: 0
        val resultData = intent?.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)
        if (resultData == null) {
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
            stopSelf()
            return START_NOT_STICKY
        }
        projection = mp
        mp.registerCallback(projectionCallback, Handler(mainLooper))

        val metrics = resources.displayMetrics
        // 游戏为横屏全屏画面：无论启动时手机朝向如何，统一按横屏尺寸建虚拟屏。
        // 竖屏桌面下画面被缩放进横屏画布，模板分数低不会误触发；进入游戏即对齐。
        val screenW = maxOf(metrics.widthPixels, metrics.heightPixels)
        val screenH = minOf(metrics.widthPixels, metrics.heightPixels)

        workerThread = HandlerThread("kanami-capture").also { it.start() }
        workerHandler = Handler(workerThread!!.looper)

        imageReader = ImageReader.newInstance(screenW, screenH, PixelFormat.RGBA_8888, 4).apply {
            setOnImageAvailableListener(frameListener, workerHandler)
        }
        virtualDisplay = mp.createVirtualDisplay(
            "KanamiCapture", screenW, screenH, metrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            imageReader!!.surface, null, workerHandler
        )

        engine.start()
        RecognitionBus.publishRunning(true)
        return START_STICKY
    }

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
        RecognitionBus.publishRunning(false)
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
    }

    override fun onDestroy() {
        stopCapture()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}

/** 进程内事件总线：服务 → UI。 */
object RecognitionBus {
    interface Listener {
        fun onRunningChanged(running: Boolean)
        fun onVoiceEvent(eventId: String)
    }

    private val listeners = mutableListOf<Listener>()

    @Synchronized
    fun addListener(l: Listener) {
        listeners.add(l)
    }

    @Synchronized
    fun removeListener(l: Listener) {
        listeners.remove(l)
    }

    @Synchronized
    private fun publish(block: (Listener) -> Unit) {
        listeners.toList().forEach(block)
    }

    fun publishRunning(running: Boolean) = publish { it.onRunningChanged(running) }
    fun publishEvent(eventId: String) = publish { it.onVoiceEvent(eventId) }
}
