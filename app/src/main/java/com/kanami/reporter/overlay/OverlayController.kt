package com.kanami.reporter.overlay

import android.app.Service
import android.graphics.PixelFormat
import android.os.SystemClock
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.permissions.PermissionHub
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.RecognitionStatus
import kotlin.math.abs

/**
 * 悬浮窗管理：主悬浮窗（状态/事件）+ 调试悬浮窗（比分/阵营/计时/模板匹配排序）。
 *
 * 由采集服务托管：开始识别时出现，停止时移除；设置里可整体关闭。
 * 两个窗口都用 [WindowManager.LayoutParams.FLAG_SECURE]，这样它们不会把内容拍进自己的录屏画面里
 * （悬浮窗本身会出现在被采集的画面中，安全标志能保证它只贡献一块黑，不会污染模板匹配之外的区域）。
 */
class OverlayController(private val service: Service, private val settings: Settings) {

    private val windowManager: WindowManager =
        service.getSystemService(Service.WINDOW_SERVICE) as WindowManager
    private val hub = PermissionHub(service)

    private var mainView: MainOverlayView? = null
    private var mainParams: WindowManager.LayoutParams? = null
    private var debugView: DebugOverlayView? = null
    private var debugParams: WindowManager.LayoutParams? = null

    private var lastStatus = RecognitionStatus()
    private var attached = false

    val isAttached: Boolean get() = attached

    /** 按设置与权限添加窗口。返回是否真的显示了悬浮窗。 */
    fun attach(): Boolean {
        if (attached) return true
        if (!settings.showOverlay) {
            DebugLog.log("overlay", "设置里关闭了悬浮窗")
            return false
        }
        if (!hub.overlayGranted()) {
            DebugLog.log("overlay", "没有悬浮窗权限，跳过显示")
            return false
        }
        val screenHeight = service.resources.displayMetrics.heightPixels
        val screenWidth = service.resources.displayMetrics.widthPixels
        val margin = OverlayViews.dp(service, 12f)

        val main = MainOverlayView(service)
        val mainLp = newParams(
            x = settings.overlayX.takeIf { it >= 0 } ?: margin,
            y = settings.overlayY.takeIf { it >= 0 } ?: (screenHeight * 0.56f).toInt()
        )
        attachTouch(
            main, mainLp,
            persist = { params -> settings.overlayX = params.x; settings.overlayY = params.y },
            onTap = { settings.overlayCollapsed = !settings.overlayCollapsed }
        )
        try {
            windowManager.addView(main, mainLp)
        } catch (e: Exception) {
            DebugLog.log("overlay", "添加主悬浮窗失败：$e")
            return false
        }
        mainView = main
        mainParams = mainLp

        if (settings.debugMode) addDebugWindow(screenWidth, screenHeight, margin)

        attached = true
        DebugLog.log("overlay", "悬浮窗已显示（主${if (settings.debugMode) " + 调试" else ""}）")
        render()
        return true
    }

    fun detach() {
        mainView?.let { runCatching { windowManager.removeView(it) } }
        debugView?.let { runCatching { windowManager.removeView(it) } }
        mainView = null
        mainParams = null
        debugView = null
        debugParams = null
        if (attached) DebugLog.log("overlay", "悬浮窗已移除")
        attached = false
    }

    fun onStatus(status: RecognitionStatus) {
        lastStatus = status
        if (attached) render()
    }

    /** 设置变化（开关悬浮窗、调试模式、收起状态）后调用。 */
    fun onSettingsChanged() {
        if (!settings.showOverlay) {
            if (attached) detach()
            return
        }
        if (!attached) {
            attach()
            return
        }
        if (settings.debugMode && debugView == null) {
            val metrics = service.resources.displayMetrics
            addDebugWindow(metrics.widthPixels, metrics.heightPixels, OverlayViews.dp(service, 12f))
        } else if (!settings.debugMode && debugView != null) {
            debugView?.let { runCatching { windowManager.removeView(it) } }
            debugView = null
            debugParams = null
        }
        render()
    }

    private fun addDebugWindow(screenWidth: Int, screenHeight: Int, margin: Int) {
        val view = DebugOverlayView(service)
        val lp = newParams(
            x = settings.debugOverlayX.takeIf { it >= 0 } ?: margin,
            y = settings.debugOverlayY.takeIf { it >= 0 } ?: (screenHeight * 0.06f).toInt()
        )
        attachTouch(
            view, lp,
            persist = { params -> settings.debugOverlayX = params.x; settings.debugOverlayY = params.y },
            onTap = null
        )
        try {
            windowManager.addView(view, lp)
            debugView = view
            debugParams = lp
        } catch (e: Exception) {
            DebugLog.log("overlay", "添加调试悬浮窗失败：$e")
        }
    }

    private fun render() {
        mainView?.render(lastStatus, settings.overlayCollapsed)
        debugView?.render(lastStatus)
    }

    private fun newParams(x: Int, y: Int): WindowManager.LayoutParams =
        WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL
                or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                or WindowManager.LayoutParams.FLAG_SECURE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            this.x = x
            this.y = y
        }

    /** 拖动移动窗口，轻点触发 [onTap]。 */
    private fun attachTouch(
        view: View,
        params: WindowManager.LayoutParams,
        persist: (WindowManager.LayoutParams) -> Unit,
        onTap: (() -> Unit)?
    ) {
        val touchSlop = OverlayViews.dp(service, 6f)
        var startRawX = 0f
        var startRawY = 0f
        var startX = 0
        var startY = 0
        var moved = false
        var downAt = 0L
        view.setOnTouchListener { v, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    startRawX = event.rawX
                    startRawY = event.rawY
                    startX = params.x
                    startY = params.y
                    moved = false
                    downAt = SystemClock.elapsedRealtime()
                    true
                }

                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - startRawX
                    val dy = event.rawY - startRawY
                    if (!moved && (abs(dx) > touchSlop || abs(dy) > touchSlop)) moved = true
                    if (moved) {
                        params.x = (startX + dx).toInt()
                        params.y = (startY + dy).toInt()
                        runCatching { windowManager.updateViewLayout(v, params) }
                        true
                    } else {
                        false
                    }
                }

                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    if (moved) {
                        persist(params)
                        DebugLog.logThrottled("overlay-move", 1000L, "overlay") {
                            "悬浮窗位置 (${params.x}, ${params.y})"
                        }
                    } else if (event.actionMasked == MotionEvent.ACTION_UP &&
                        SystemClock.elapsedRealtime() - downAt < 400L
                    ) {
                        onTap?.invoke()
                    }
                    true
                }

                else -> false
            }
        }
    }
}
