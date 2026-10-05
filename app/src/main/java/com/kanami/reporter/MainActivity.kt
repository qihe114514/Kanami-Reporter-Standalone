package com.kanami.reporter

import android.Manifest
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
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
import androidx.core.content.ContextCompat
import com.kanami.reporter.capture.CaptureService
import com.kanami.reporter.capture.ProjectionHandoff
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.permissions.PermissionHub
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.StatusHub
import com.kanami.reporter.ui.AdaptiveGlass
import com.kanami.reporter.ui.ControlCard
import com.kanami.reporter.ui.PermissionListCard
import com.kanami.reporter.ui.ProgressiveBlurEdge
import com.kanami.reporter.ui.ScoreListCard
import com.kanami.reporter.ui.StatusColors
import com.kanami.reporter.ui.Text
import com.kanami.reporter.ui.UpdateDialog
import com.kanami.reporter.ui.UpdateDialogState
import com.kanami.reporter.ui.UpdateHolder
import com.kanami.reporter.ui.appVersion
import com.kanami.reporter.ui.canInstallPackages
import com.kanami.reporter.ui.downloadUpdateApk
import com.kanami.reporter.ui.installDownloadedApk
import com.kanami.reporter.ui.openUrl
import com.kanami.reporter.ui.rememberHapticTick
import com.kanami.reporter.ui.requestInstallPermission
import com.kanami.reporter.ui.liquid.LiquidBottomTab
import com.kanami.reporter.ui.liquid.LiquidBottomTabs
import com.kanami.reporter.ui.liquid.LiquidButton
import com.kanami.reporter.ui.liquid.TabIcon
import com.kanami.reporter.ui.liquid.TabIconKind
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberCombinedBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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

        // 玻璃数据源：壁纸层供玻璃卡片折射；滚动内容层供上下边缘的渐进式模糊折射
        val backdrop = rememberLayerBackdrop()
        val scrollBackdrop = rememberLayerBackdrop()
        // 顺序 = (下层, 上层)：壁纸在下、滚动内容在上
        val edgeBackdrop = rememberCombinedBackdrop(backdrop, scrollBackdrop)
        val systemBars = WindowInsets.systemBars.asPaddingValues()
        val statusBarHeight = systemBars.calculateTopPadding()
        val navigationBarHeight = systemBars.calculateBottomPadding()

        // 更新：发现有新版就把运行页的主按钮换成更新入口，点了弹液态玻璃弹窗
        val updateInfo = UpdateHolder.info
        var showUpdateDialog by remember { mutableStateOf(false) }
        var updateState by remember { mutableStateOf(UpdateDialogState()) }
        val updateScope = rememberCoroutineScope()
        val uiHandler = remember { Handler(Looper.getMainLooper()) }
        // 标题条 / 底栏各自占的高度（含它们自己的内边距）。用固定值而不是实测：
        // 实测要等首帧，会让内容先闪一下再归位。
        val titleBarHeight = statusBarHeight + 64.dp
        val bottomBarHeight = navigationBarHeight + 80.dp

        Box(Modifier.fillMaxSize()) {
            AppBackground(context, backdrop)

            // 内容层占满整屏：卡片可以一路滚到屏幕最顶/最底（穿过标题与底栏），
            // 而不是被截断在"标题下方到 底栏上方"这段中间区域里。
            // 首尾用 Spacer 让出标题条与底栏的位置，所以静止时的观感和以前一致。
            Column(
                Modifier
                    .fillMaxSize()
                    .layerBackdrop(scrollBackdrop)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp)
            ) {
                Spacer(Modifier.height(titleBarHeight))
                // 两个页面之间做交叉淡入淡出，别硬切
                Crossfade(
                    targetState = tab,
                    animationSpec = tween(durationMillis = 260),
                    label = "tab-content"
                ) { current ->
                    if (current == 0) {
                            RunPage(
                                context = context,
                                settings = settings,
                                engine = engine,
                                hub = hub,
                                backdrop = backdrop,
                                resumeTick = resumeTick,
                                status = status,
                                update = updateInfo,
                                onUpdate = { showUpdateDialog = true }
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
                }
                Spacer(Modifier.height(bottomBarHeight + 16.dp))
            }

            // 顶部 / 底部渐进式模糊：画在内容之上、标题与底栏之下，所以标题文字始终清晰。
            //
            // fadeDistance 就是"模糊区域从边缘伸进来多远"，用物理尺寸直接指定，
            // 不再用模糊带高度的比例（那样一调高度，模糊位置就跟着漂）。
            //   顶部 —— 正好到标题文字的上边缘，内容一和标题重叠就开始虚化；
            //   底部 —— 越过底栏顶边再往上伸 40dp，让内容进入底栏之前就已经虚化。
            ProgressiveBlurEdge(
                backdrop = edgeBackdrop,
                fromTop = true,
                fadeDistance = statusBarHeight + 16.dp,
                modifier = Modifier.align(Alignment.TopCenter)
            )
            ProgressiveBlurEdge(
                backdrop = edgeBackdrop,
                fromTop = false,
                fadeDistance = navigationBarHeight + 80.dp + 40.dp,
                modifier = Modifier.align(Alignment.BottomCenter)
            )

            // 标题条与底栏固定在屏幕两端，盖在内容之上
            Box(
                Modifier
                    .align(Alignment.TopStart)
                    .fillMaxWidth()
                    .systemBarsPadding()
            ) {
                Text(
                    "香奈美x黑潮爆破",
                    color = StatusColors.text,
                    fontSize = 22.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(horizontal = 24.dp, vertical = 16.dp)
                )
            }

            Box(
                Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .systemBarsPadding()
            ) {
                BottomBar(
                    tab = tab,
                    onSelect = { tab = it },
                    backdrop = backdrop
                )
            }

            val pending = updateInfo
            if (showUpdateDialog && pending != null) {
                UpdateDialog(
                    backdrop = backdrop,
                    info = pending,
                    currentVersion = appVersion(context),
                    state = updateState,
                    onDismiss = { showUpdateDialog = false },
                    onOpenReleasePage = {
                        openUrl(context, pending.releasePageUrl)
                        showUpdateDialog = false
                    },
                    onDownloadAndInstall = {
                        // 要求：**先**申请安装权限再开始下载，而不是下载完才发现没权限
                        if (!canInstallPackages(context)) {
                            updateState = UpdateDialogState(
                                message = "请先允许本应用安装应用，授权后回来再点一次「下载并安装」"
                            )
                            requestInstallPermission(context)
                        } else {
                            updateState = UpdateDialogState(downloading = true, progress = -1)
                            updateScope.launch {
                                val result = withContext(Dispatchers.IO) {
                                    runCatching {
                                        downloadUpdateApk(context, pending.apkUrl) { percent ->
                                            uiHandler.post {
                                                updateState = updateState.copy(progress = percent)
                                            }
                                        }
                                    }
                                }
                                result
                                    .onSuccess { apk ->
                                        updateState = UpdateDialogState(message = "下载完成，正在拉起安装器…")
                                        runCatching { installDownloadedApk(context, apk) }
                                            .onFailure {
                                                updateState = UpdateDialogState(
                                                    message = "拉起安装器失败：${it.message ?: it.javaClass.simpleName}"
                                                )
                                            }
                                    }
                                    .onFailure {
                                        updateState = UpdateDialogState(
                                            message = "下载失败：${it.message ?: it.javaClass.simpleName}（所有通道都不通）"
                                        )
                                    }
                            }
                        }
                    }
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
                }.getOrNull()?.also {
                    // 顺手算一次背景亮度，决定文字走浅色还是深色（静态壁纸只需算这一次）
                    AdaptiveGlass.updateFromWallpaper(it)
                }?.asImageBitmap()
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
        Box(
            Modifier
                .fillMaxWidth()
                .padding(vertical = 8.dp),
            contentAlignment = Alignment.Center
        ) {
            LiquidBottomTabs(
                selectedTabIndex = tab,
                onTabSelected = {
                    haptic()
                    onSelect(it)
                },
                backdrop = backdrop,
                tabsCount = 2,
                // 只占屏幕中间约七成宽：胶囊底栏不必铺满整行。高度、图标与文字尺寸都不变。
                modifier = Modifier.fillMaxWidth(0.7f)
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
    }

    @Composable
    private fun RunPage(
        context: Context,
        settings: Settings,
        engine: com.kanami.reporter.core.RecognitionEngine,
        hub: PermissionHub,
        backdrop: com.kyant.backdrop.backdrops.LayerBackdrop,
        resumeTick: Int,
        status: com.kanami.reporter.status.RecognitionStatus,
        update: com.kanami.reporter.ui.UpdateInfo?,
        onUpdate: () -> Unit
    ) {
        val projectionLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.StartActivityForResult()
        ) { result ->
            val data = result.data
            if (result.resultCode == RESULT_OK && data != null) {
                // 同一进程直接交接引用，绕开 Android 13+ Intent extra 取 Parcelable 的老坑
                ProjectionHandoff.put(result.resultCode, data)
                val intent = Intent(context, CaptureService::class.java)
                    .putExtra(CaptureService.EXTRA_RESULT_CODE, result.resultCode)
                    .putExtra(CaptureService.EXTRA_RESULT_DATA, data)
                val started = try {
                    ContextCompat.startForegroundService(context, intent)
                    true
                } catch (e: Exception) {
                    DebugLog.log("ui", "启动采集服务失败：${e.javaClass.simpleName}: ${e.message}")
                    StatusHub.setNotice("启动采集服务失败：${e.message}", error = true)
                    false
                }
                if (started) {
                    StatusHub.setNotice("录屏授权已通过，正在启动采集…")
                    DebugLog.log("ui", "已请求开始识别（resultCode=${result.resultCode}）")
                }
            } else {
                DebugLog.log("ui", "用户取消了录屏授权（resultCode=${result.resultCode}）")
                StatusHub.setNotice("没有拿到录屏授权，识别未启动", error = true)
            }
        }

        val haptic = rememberHapticTick()
        Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
            // 主卡：状态 + 对局信息 + 开始/停止，一屏之内看完
            ControlCard(
                status = status,
                templateCount = engine.loadedTemplateCount,
                backdrop = backdrop,
                update = update,
                onUpdate = onUpdate,
                onStart = {
                    haptic()
                    StatusHub.setNotice("正在请求录屏授权…")
                    DebugLog.log("ui", "点开始识别，拉起录屏授权")
                    val manager =
                        context.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                    try {
                        projectionLauncher.launch(manager.createScreenCaptureIntent())
                    } catch (e: Exception) {
                        DebugLog.log("ui", "拉起录屏授权失败：${e.javaClass.simpleName}: ${e.message}")
                        StatusHub.setNotice("拉起录屏授权失败：${e.message}", error = true)
                    }
                },
                onStop = {
                    haptic()
                    context.stopService(Intent(context, CaptureService::class.java))
                    StatusHub.setNotice("已请求停止识别")
                    DebugLog.log("ui", "已请求停止识别")
                }
            )

            // 权限：全开时收成一行，缺项自动展开
            PermissionListCard(
                items = remember(resumeTick, status.running) { hub.items() },
                backdrop = backdrop,
                onLaunch = { intent -> launchSafely(intent, hub.appDetailsIntent()) }
            )

            // 调试信息，默认收起
            ScoreListCard(status = status, backdrop = backdrop)
        }
    }
}
