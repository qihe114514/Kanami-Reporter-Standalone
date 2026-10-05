package com.kanami.reporter.permissions

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.status.StatusHub

/**
 * 最小无障碍服务。
 *
 * 只做两件事：
 * 1. 上报前台应用包名（判断"当前是不是在游戏里"，并把包名写进调试日志）；
 * 2. 让本进程带一个系统绑定的服务，显著降低后台被清理的概率——采集靠 MediaProjection，
 *    一旦进程被杀就必须重新授权录屏，游戏中弹授权框非常影响体验。
 *
 * **不读取任何屏幕内容**（`canRetrieveWindowContent="false"`），画面完全来自录屏授权。
 */
class KanamiAccessibilityService : AccessibilityService() {

    private var lastPackage: String? = null

    override fun onServiceConnected() {
        super.onServiceConnected()
        DebugLog.log("accessibility", "无障碍服务已连接")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event == null) return
        if (event.eventType != AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) return
        val pkg = event.packageName?.toString()?.takeIf { it.isNotEmpty() } ?: return
        if (pkg == lastPackage) return
        lastPackage = pkg
        DebugLog.logThrottled("foreground", 4000L, "accessibility") { "前台应用：$pkg" }
        StatusHub.setForegroundPackage(pkg)
    }

    override fun onInterrupt() {
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        DebugLog.log("accessibility", "无障碍服务已断开")
        return super.onUnbind(intent)
    }
}
