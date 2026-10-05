package com.kanami.reporter.permissions

import android.Manifest
import android.accessibilityservice.AccessibilityServiceInfo
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.core.content.ContextCompat
import com.kanami.reporter.permissions.KanamiAccessibilityService

/** 一项必须权限的状态与授权入口。 */
data class PermissionItem(
    val key: String,
    val title: String,
    val description: String,
    val granted: Boolean,
    /** 点击「去授权」执行的跳转；为 null 表示没有直接入口（例如厂商设置页找不到）。 */
    val intents: List<Pair<String, Intent>>
)

/**
 * 权限中心：悬浮窗、后台驻留（电池优化 + 厂商自启动）、无障碍、通知。
 *
 * 这四项都不是为了"多要权限"：悬浮窗要显示游戏内 HUD，后台驻留/无障碍是为了让采集进程
 * 不被系统干掉（MediaProjection 一旦被杀就得重新授权），通知是前台服务必须可见的载体。
 */
class PermissionHub(private val context: Context) {

    fun overlayGranted(): Boolean = Settings.canDrawOverlays(context)

    fun notificationGranted(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED
        } else {
            true
        }

    fun batteryUnrestricted(): Boolean {
        val pm = context.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return true
        return pm.isIgnoringBatteryOptimizations(context.packageName)
    }

    /** 无障碍服务是否已在本机启用。 */
    fun accessibilityEnabled(): Boolean {
        val manager = context.getSystemService(Context.ACCESSIBILITY_SERVICE) as? AccessibilityManager
            ?: return false
        val target = ComponentName(context, KanamiAccessibilityService::class.java)
        return manager.getEnabledAccessibilityServiceList(AccessibilityServiceInfo.FEEDBACK_ALL_MASK)
            .any { info ->
                val serviceInfo = info.resolveInfo?.serviceInfo ?: return@any false
                ComponentName(serviceInfo.packageName, serviceInfo.name) == target
            }
    }

    fun items(): List<PermissionItem> = listOf(
        PermissionItem(
            key = KEY_OVERLAY,
            title = "悬浮窗",
            description = "在游戏画面上显示香奈美的实时状态（事件、识别是否正常、比分）",
            granted = overlayGranted(),
            intents = listOf("去开启悬浮窗" to overlayIntent())
        ),
        PermissionItem(
            key = KEY_BATTERY,
            title = "后台驻留",
            description = "允许后台运行并加入白名单，避免游戏时采集被系统清理（被杀后需重新授权录屏）",
            granted = batteryUnrestricted(),
            intents = batteryIntents()
        ),
        PermissionItem(
            key = KEY_ACCESSIBILITY,
            title = "无障碍",
            description = "仅用于识别当前前台应用 + 提升进程存活率，不读取任何屏幕内容",
            granted = accessibilityEnabled(),
            intents = listOf("去开启无障碍" to accessibilityIntent())
        ),
        PermissionItem(
            key = KEY_NOTIFICATION,
            title = "通知",
            description = "前台识别服务的常驻通知（Android 要求采集时必须显示）",
            granted = notificationGranted(),
            intents = listOf("去开启通知" to appNotificationIntent())
        )
    )

    fun missing(): List<PermissionItem> = items().filterNot { it.granted }

    fun overlayIntent(): Intent = Intent(
        Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
        Uri.parse("package:${context.packageName}")
    )

    fun accessibilityIntent(): Intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)

    fun appNotificationIntent(): Intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
        .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)

    fun appDetailsIntent(): Intent = Intent(
        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
        Uri.parse("package:${context.packageName}")
    )

    /** 电池优化白名单入口，外加各厂商的自启动管理页（能解析到哪个就给哪个）。 */
    private fun batteryIntents(): List<Pair<String, Intent>> {
        val list = mutableListOf<Pair<String, Intent>>()
        list += "忽略电池优化" to Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
            .setData(Uri.parse("package:${context.packageName}"))
        AUTOSTART_PAGES.forEach { (label, component) ->
            val intent = Intent().setComponent(component)
            if (context.packageManager.resolveActivity(intent, 0) != null) {
                list += label to intent
            }
        }
        // 兜底：直接进应用详情页，用户可以在「电池」里自行设置
        val info = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
        if (context.packageManager.resolveActivity(info, 0) != null) {
            list += "电池优化列表" to info
        }
        list += "应用详情" to appDetailsIntent()
        return list
    }

    companion object {
        const val KEY_OVERLAY = "overlay"
        const val KEY_BATTERY = "battery"
        const val KEY_ACCESSIBILITY = "accessibility"
        const val KEY_NOTIFICATION = "notification"

        private val AUTOSTART_PAGES: List<Pair<String, ComponentName>> = listOf(
            "自启动管理" to ComponentName(
                "com.miui.securitycenter",
                "com.miui.permcenter.autostart.AutoStartManagementActivity"
            ),
            "自启动管理" to ComponentName(
                "com.huawei.systemmanager",
                "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"
            ),
            "自启动管理" to ComponentName(
                "com.hihonor.systemmanager",
                "com.hihonor.systemmanager.startupmgr.ui.StartupNormalAppListActivity"
            ),
            "自启动管理" to ComponentName(
                "com.coloros.safecenter",
                "com.coloros.safecenter.permission.startup.StartupAppListActivity"
            ),
            "自启动管理" to ComponentName(
                "com.coloros.safecenter",
                "com.coloros.safecenter.startupapp.StartupAppListActivity"
            ),
            "自启动管理" to ComponentName(
                "com.vivo.permissionmanager",
                "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"
            ),
            "自启动管理" to ComponentName(
                "com.iqoo.secure",
                "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity"
            ),
            "自启动管理" to ComponentName(
                "com.meizu.safe",
                "com.meizu.safe.security.SHOW_APPSEC"
            ),
            "后台限制" to ComponentName(
                "com.samsung.android.lool",
                "com.samsung.android.sm.ui.battery.BatteryActivity"
            )
        )
    }
}
