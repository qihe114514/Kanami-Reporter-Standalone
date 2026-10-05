package com.kanami.reporter.settings

import android.content.Context
import com.kanami.reporter.debug.DebugLog
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * 应用设置（SharedPreferences）。任何写入都会让 [revisions] +1，
 * UI 与悬浮窗据此重新读取，避免到处传回调。
 */
class Settings(context: Context) {

    private val prefs = context.getSharedPreferences("kanami", Context.MODE_PRIVATE)

    private val _revisions = MutableStateFlow(0)
    val revisions: StateFlow<Int> = _revisions

    /** 匹配阈值。 */
    var threshold: Double
        get() = prefs.getFloat(KEY_THRESHOLD, 0.90f).toDouble()
        set(value) = prefs.edit().putFloat(KEY_THRESHOLD, value.toFloat()).also { it.apply() }.let { bump() }

    /** 调试模式：开启后写私有目录日志，并额外显示调试悬浮窗。 */
    var debugMode: Boolean
        get() = prefs.getBoolean(KEY_DEBUG_MODE, false)
        set(value) {
            prefs.edit().putBoolean(KEY_DEBUG_MODE, value).apply()
            // 运行时开启要立刻开始写日志（关闭时停止写文件，logcat 仍保留）
            DebugLog.enabled = value
            bump()
        }

    /** 识别时是否显示悬浮窗。 */
    var showOverlay: Boolean
        get() = prefs.getBoolean(KEY_SHOW_OVERLAY, true)
        set(value) = prefs.edit().putBoolean(KEY_SHOW_OVERLAY, value).also { it.apply() }.let { bump() }

    /** 主悬浮窗是否处于收起（小胶囊）状态。 */
    var overlayCollapsed: Boolean
        get() = prefs.getBoolean(KEY_OVERLAY_COLLAPSED, true)
        set(value) = prefs.edit().putBoolean(KEY_OVERLAY_COLLAPSED, value).also { it.apply() }.let { bump() }

    var overlayX: Int
        get() = prefs.getInt(KEY_OVERLAY_X, -1)
        set(value) = prefs.edit().putInt(KEY_OVERLAY_X, value).also { it.apply() }.let { bump() }

    var overlayY: Int
        get() = prefs.getInt(KEY_OVERLAY_Y, -1)
        set(value) = prefs.edit().putInt(KEY_OVERLAY_Y, value).also { it.apply() }.let { bump() }

    var debugOverlayX: Int
        get() = prefs.getInt(KEY_DEBUG_X, -1)
        set(value) = prefs.edit().putInt(KEY_DEBUG_X, value).also { it.apply() }.let { bump() }

    var debugOverlayY: Int
        get() = prefs.getInt(KEY_DEBUG_Y, -1)
        set(value) = prefs.edit().putInt(KEY_DEBUG_Y, value).also { it.apply() }.let { bump() }

    /** 液态玻璃折射/反射强度倍率（1 = 默认）。 */
    var glassRefraction: Float
        get() = prefs.getFloat(KEY_GLASS_REFRACTION, 1f)
        set(value) = prefs.edit().putFloat(KEY_GLASS_REFRACTION, value).also { it.apply() }.let { bump() }

    /** 液态玻璃模糊强度倍率（1 = 默认）。 */
    var glassBlur: Float
        get() = prefs.getFloat(KEY_GLASS_BLUR, 1f)
        set(value) = prefs.edit().putFloat(KEY_GLASS_BLUR, value).also { it.apply() }.let { bump() }

    /** 启动时自动检查更新（与桌面端同名设置项）。 */
    var autoCheckUpdates: Boolean
        get() = prefs.getBoolean(KEY_AUTO_CHECK_UPDATES, true)
        set(value) = prefs.edit().putBoolean(KEY_AUTO_CHECK_UPDATES, value).also { it.apply() }.let { bump() }

    /** 首次启动引导（权限清单）是否已展示过。 */
    var firstRunDone: Boolean
        get() = prefs.getBoolean(KEY_FIRST_RUN_DONE, false)
        set(value) = prefs.edit().putBoolean(KEY_FIRST_RUN_DONE, value).also { it.apply() }.let { bump() }

    fun bump() {
        _revisions.value = _revisions.value + 1
    }

    private companion object {
        const val KEY_THRESHOLD = "threshold"
        const val KEY_DEBUG_MODE = "debug_mode"
        const val KEY_SHOW_OVERLAY = "show_overlay"
        const val KEY_OVERLAY_COLLAPSED = "overlay_collapsed"
        const val KEY_OVERLAY_X = "overlay_x"
        const val KEY_OVERLAY_Y = "overlay_y"
        const val KEY_DEBUG_X = "debug_overlay_x"
        const val KEY_DEBUG_Y = "debug_overlay_y"
        const val KEY_GLASS_REFRACTION = "glass_refraction"
        const val KEY_GLASS_BLUR = "glass_blur"
        const val KEY_AUTO_CHECK_UPDATES = "auto_check_updates"
        const val KEY_FIRST_RUN_DONE = "first_run_done"
    }
}
