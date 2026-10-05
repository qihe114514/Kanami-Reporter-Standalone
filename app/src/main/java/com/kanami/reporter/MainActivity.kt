package com.kanami.reporter

import android.Manifest
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.ActivityCompat
import com.kanami.reporter.capture.CaptureService
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.permissions.PermissionHub
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.StatusHub
import com.kanami.reporter.ui.PermissionListCard
import com.kanami.reporter.ui.ScoreListCard
import com.kanami.reporter.ui.StatusCard
import com.kanami.reporter.ui.StatusColors
import com.kanami.reporter.ui.Text
import com.kanami.reporter.ui.rememberHapticTick
import com.kanami.reporter.ui.liquid.LiquidBottomTab
import com.kanami.reporter.ui.liquid.LiquidBottomTabs
import com.kanami.reporter.ui.liquid.LiquidButton
import com.kanami.reporter.ui.liquid.TabIcon
import com.kanami.reporter.ui.liquid.TabIconKind
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

class MainActivity : ComponentActivity() {

    /** onResume 时自增，用于刷新权限状态。 */
    private val resumeTick = mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ActivityCompat.requestPermissions(
            this, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1
        )
        maybeGuideToOverlayPermission()
        setContent {
            KanamiScreen(resumeTick.intValue)
        }
    }

    override fun onResume() {
        super.onResume()
        resumeTick.intValue++
    }

    /** 首次进入先索要悬浮窗权限：没有它游戏内 HUD 就不存在。 */
    private fun maybeGuideToOverlayPermission() {
        val app = application as KanamiApp
        if (app.settings.firstRunDone) return
        app.settings.firstRunDone = true
        val hub = PermissionHub(this)
        if (hub.overlayGranted()) return
        DebugLog.log("permission", "首次启动，引导开启悬浮窗权限")
        launchSafely(hub.overlayIntent())
    }

    private fun launchSafely(intent: Intent, fallback: Intent? = null) {
        try {
            startActivity(intent)
        } catch (e: Exception) {
            DebugLog.log("permission", "跳转失败：${intent.action} ${e.javaClass.simpleName}")
            fallback?.let {
                try {
                    startActivity(it)
                } catch (_: Exception) {
                }
            }
        }
    }

    @Composable
    private fun KanamiScreen(resumeTick: Int) {
        val context = LocalContext.current
        val app = context.applicationContext as KanamiApp
        val settings: Settings = app.settings
        val engine = app.engine
        val hub = remember { PermissionHub(context) }
        val status by StatusHub.status.collectAsState()
        val settingsRevision by settings.revisions.collectAsState()
        var tab by remember { mutableIntStateOf(0) }

        // 玻璃数据源：记录根布局背景，玻璃组件从中采样折射
        val backdrop = rememberLayerBackdrop()

        Box(Modifier.fillMaxSize()) {
            AppBackground(context, backdrop)

            Column(
                Modifier
                    .fillMaxSize()
                    .systemBarsPadding()
            ) {
                Text(
                    "香奈美x黑潮爆破",
                    color = StatusColors.text,
                    fontSize = 22.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 16.dp)
                )

                Column(
                    Modifier
                        .weight(1f)
                        .fillMaxWidth()
                        .verticalScroll(rememberScrollState())
                        .padding(horizontal = 20.dp)
                ) {
                    if (tab == 0) {
                        RunPage(
                            context = context,
                            settings = settings,
                            engine = engine,
                            hub = hub,
                            backdrop = backdrop,
                            resumeTick = resumeTick,
                            status = status
                        )
                    } else {
                        com.kanami.reporter.ui.SettingsPage(
                            context = context,
                            settings = settings,
                            engine = engine,
                            hub = hub,
                            backdrop = backdrop,
                            revision = settingsRevision + resumeTick,
                            status = status,
                            onLaunch = { intent -> launchSafely(intent, hub.appDetailsIntent()) }
                        )
                    }
                    Spacer(Modifier.height(96.dp))
                }

                BottomBar(
                    tab = tab,
                    onSelect = { tab = it },
                    backdrop = backdrop
                )
            }
        }
    }

    /** 背景层：壁纸 + 压暗遮罩，被 backdrop 记录，供液态玻璃折射。 */
    @Composable
    private fun AppBackground(context: Context, backdrop: com.kyant.backdrop.backdrops.LayerBackdrop) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
        ) {
            val wallpaper = remember {
                runCatching {
                    context.assets.open("background/kanami_bg.webp").use { BitmapFactory.decodeStream(it) }
                }.getOrNull()?.asImageBitmap()
            }
            if (wallpaper != null) {
                Image(
                    bitmap = wallpaper,
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize()
                )
            } else {
                Box(
                    Modifier
                        .fillMaxSize()
                        .background(
                            Brush.verticalGradient(
                                listOf(Color(0xFF0B1020), Color(0xFF22345C), Color(0xFF0B1020))
                            )
                        )
                )
            }
            // 上/下加深：保证状态栏区域与底栏上的文字可读
            Box(
                Modifier
                    .fillMaxSize()
                    .background(
                        Brush.verticalGradient(
                            listOf(
                                Color(0x99000000),
                                Color(0x22000000),
                                Color(0x66000000),
                                Color(0xCC000000)
                            )
                        )
                    )
            )
        }
    }

    /** 底部液态玻璃底栏（官方示例组件：胶囊玻璃 + 滑动透镜 + 拖动切换 + 按压反馈）。 */
    @Composable
    private fun BottomBar(
        tab: Int,
        onSelect: (Int) -> Unit,
        backdrop: com.kyant.backdrop.backdrops.LayerBackdrop
    ) {
        val haptic = rememberHapticTick()
        LiquidBottomTabs(
            selectedTabIndex = { tab },
            onTabSelected = {
                haptic()
                onSelect(it)
            },
            backdrop = backdrop,
            tabsCount = 2,
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp, vertical = 8.dp)
        ) {
            LiquidBottomTab({
                haptic()
                onSelect(0)
            }) {
                TabIcon(TabIconKind.Run, StatusColors.text, Modifier.size(22.dp))
                Text("运行", color = StatusColors.text, fontSize = 11.sp)
            }
            LiquidBottomTab({
                haptic()
                onSelect(1)
            }) {
                TabIcon(TabIconKind.Settings, StatusColors.text, Modifier.size(22.dp))
                Text("设置", color = StatusColors.text, fontSize = 11.sp)
            }
        }
    }

    @Composable
    private fun RunPage(
        context: Context,
        settings: Settings,
        engine: com.kanami.reporter.core.RecognitionEngine,
        hub: PermissionHub,
        backdrop: com.kyant.backdrop.backdrops.LayerBackdrop,
        resumeTick: Int,
        status: com.kanami.reporter.status.RecognitionStatus
    ) {
        val projectionLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.StartActivityForResult()
        ) { result ->
            val data = result.data
            if (result.resultCode == RESULT_OK && data != null) {
                val intent = Intent(context, CaptureService::class.java)
                    .putExtra(CaptureService.EXTRA_RESULT_CODE, result.resultCode)
                    .putExtra(CaptureService.EXTRA_RESULT_DATA, data)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
                DebugLog.log("ui", "已请求开始识别")
            } else {
                DebugLog.log("ui", "用户取消了录屏授权")
            }
        }

        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            val haptic = rememberHapticTick()
            PermissionListCard(
                items = remember(resumeTick, status.running) { hub.items() },
                backdrop = backdrop,
                onLaunch = { intent -> launchSafely(intent, hub.appDetailsIntent()) }
            )

            StatusCard(status = status, templateCount = engine.loadedTemplateCount, backdrop = backdrop)

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                LiquidButton(
                    onClick = {
                        haptic()
                        val manager =
                            context.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                        projectionLauncher.launch(manager.createScreenCaptureIntent())
                    },
                    backdrop = backdrop
                ) {
                    Text("开始识别", color = StatusColors.text)
                }
                LiquidButton(
                    onClick = {
                        haptic()
                        context.stopService(Intent(context, CaptureService::class.java))
                        DebugLog.log("ui", "已请求停止识别")
                    },
                    backdrop = backdrop
                ) {
                    Text("停止", color = Color(0xFFFFD9D9))
                }
            }

            if (!hub.overlayGranted()) {
                Text(
                    "提示：悬浮窗权限未开启，游戏内不会显示实时状态（识别本身仍可用）。",
                    color = StatusColors.warn,
                    fontSize = 12.sp
                )
            }

            ScoreListCard(status = status, backdrop = backdrop)
        }
    }
}
