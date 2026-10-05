package com.kanami.reporter

import android.Manifest
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.ActivityCompat
import com.kanami.reporter.capture.CaptureService
import com.kanami.reporter.capture.RecognitionBus
import com.kanami.reporter.core.ReporterStates
import com.kanami.reporter.core.StateId
import com.kanami.reporter.ui.GlassCard
import com.kanami.reporter.ui.GlassSlider
import com.kanami.reporter.ui.LiquidButton
import com.kanami.reporter.ui.LiquidTab
import com.kanami.reporter.ui.Text
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity(), RecognitionBus.Listener {

    private var running by mutableStateOf(false)
    private var lastEvent by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ActivityCompat.requestPermissions(
            this, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1
        )
        setContent {
            KanamiScreen()
        }
    }

    override fun onResume() {
        super.onResume()
        RecognitionBus.addListener(this)
    }

    override fun onPause() {
        RecognitionBus.removeListener(this)
        super.onPause()
    }

    override fun onRunningChanged(r: Boolean) {
        running = r
    }

    override fun onVoiceEvent(eventId: String) {
        lastEvent = eventId
    }

    @Composable
    private fun KanamiScreen() {
        val engine = (application as KanamiApp).engine
        var tab by remember { mutableIntStateOf(0) }

        // 玻璃数据源：记录根布局背景，玻璃组件从中采样折射
        val backdrop = rememberLayerBackdrop()

        Box(Modifier.fillMaxSize()) {
            // 背景层：被 backdrop 记录，供液态玻璃折射
            Box(
                Modifier
                    .fillMaxSize()
                    .layerBackdrop(backdrop)
                    .background(
                        Brush.verticalGradient(
                            listOf(Color(0xFF0B1020), Color(0xFF22345C), Color(0xFF0B1020))
                        )
                    )
            )

            Column(
                Modifier
                    .fillMaxSize()
                    .systemBarsPadding()
            ) {
                Text(
                    "香奈美x黑潮爆破",
                    color = Color(0xFFE8F0FF),
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
                    if (tab == 0) RunPage(engine, backdrop) else SettingsPage(engine, backdrop)
                    Spacer(Modifier.height(96.dp))
                }

                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 24.dp, vertical = 12.dp),
                    horizontalArrangement = Arrangement.Center,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    LiquidTab(selected = tab == 0, onClick = { tab = 0 }, backdrop = backdrop) {
                        Text("运行", color = Color(0xFFE8F0FF))
                    }
                    Spacer(Modifier.width(10.dp))
                    LiquidTab(selected = tab == 1, onClick = { tab = 1 }, backdrop = backdrop) {
                        Text("设置", color = Color(0xFFE8F0FF))
                    }
                }
            }
        }
    }

    @Composable
    private fun RunPage(engine: com.kanami.reporter.core.RecognitionEngine, backdrop: com.kyant.backdrop.backdrops.LayerBackdrop) {
        val projectionLauncher = rememberLauncherForActivityResult(
            ActivityResultContracts.StartActivityForResult()
        ) { result ->
            val data = result.data
            if (result.resultCode == RESULT_OK && data != null) {
                val intent = Intent(this, CaptureService::class.java)
                    .putExtra(CaptureService.EXTRA_RESULT_CODE, result.resultCode)
                    .putExtra(CaptureService.EXTRA_RESULT_DATA, data)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    startForegroundService(intent)
                } else {
                    startService(intent)
                }
            }
        }

        // 定时刷新：引擎结果在服务线程更新，这里轮询读取
        var tick by remember { mutableIntStateOf(0) }
        LaunchedEffect(Unit) {
            while (true) {
                tick++
                delay(500)
            }
        }

        val detection = engine.lastDetection
        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            GlassCard(backdrop = backdrop) {
                Column(Modifier.padding(20.dp)) {
                    Text(
                        when {
                            !running -> "未在识别"
                            detection?.stateId == null -> "识别中…（等待进入对局）"
                            else -> ReporterStates.DisplayNames[detection.stateId!!.id]
                        },
                        color = Color(0xFFE8F0FF),
                        fontSize = 24.sp,
                        fontWeight = FontWeight.SemiBold
                    )
                    Spacer(Modifier.height(8.dp))
                    val round = detection?.round ?: 0
                    val side = detection?.side ?: 0
                    Text(
                        "回合 $round · " + when (side) {
                            1 -> "攻方"
                            2 -> "守方"
                            else -> "阵营未知"
                        } + " · 已加载模板 ${engine.loadedTemplateCount}",
                        color = Color(0xFF9FB4D8),
                        fontSize = 14.sp
                    )
                    lastEvent?.let {
                        Spacer(Modifier.height(8.dp))
                        Text("最近播报：$it", color = Color(0xFF7FD1AE), fontSize = 13.sp)
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                LiquidButton(
                    onClick = {
                        val manager =
                            getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                        projectionLauncher.launch(manager.createScreenCaptureIntent())
                    },
                    backdrop = backdrop
                ) {
                    Text("开始识别", color = Color(0xFFE8F0FF))
                }
                LiquidButton(
                    onClick = { stopService(Intent(this@MainActivity, CaptureService::class.java)) },
                    backdrop = backdrop
                ) {
                    Text("停止", color = Color(0xFFFFD9D9))
                }
            }

            GlassCard(backdrop = backdrop) {
                Column(Modifier.padding(20.dp)) {
                    Text("模板匹配值（前 8）", color = Color(0xFF9FB4D8), fontSize = 14.sp)
                    Spacer(Modifier.height(8.dp))
                    val scores = detection?.scores
                    if (scores != null) {
                        scores
                            .mapIndexed { i, s -> Triple(i, s, s >= detection.threshold) }
                            .sortedByDescending { it.second }
                            .take(8)
                            .forEach { (i, s, hit) ->
                                Row(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                                    Text(
                                        ReporterStates.DisplayNames[i],
                                        color = if (hit) Color(0xFF7FD1AE) else Color(0xFF6E7FA0),
                                        fontSize = 13.sp,
                                        modifier = Modifier.width(130.dp)
                                    )
                                    Text(
                                        "%.3f".format(s),
                                        color = if (hit) Color(0xFF7FD1AE) else Color(0xFF6E7FA0),
                                        fontSize = 13.sp
                                    )
                                }
                            }
                    } else {
                        Text("开始识别后显示实时匹配值", color = Color(0xFF6E7FA0), fontSize = 13.sp)
                    }
                }
            }
        }
    }

    @Composable
    private fun SettingsPage(engine: com.kanami.reporter.core.RecognitionEngine, backdrop: com.kyant.backdrop.backdrops.LayerBackdrop) {
        var threshold by remember { mutableStateOf(engine.threshold.toFloat()) }
        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            GlassCard(backdrop = backdrop) {
                Column(Modifier.padding(20.dp)) {
                    Text("匹配阈值：%.2f".format(threshold), color = Color(0xFFE8F0FF))
                    GlassSlider(
                        value = threshold,
                        onValueChange = {
                            threshold = it
                            engine.threshold = it.toDouble()
                        },
                        valueRange = 0.80f..0.99f
                    )
                    Text(
                        "低于 0.90 容易误报；手机端模板已按实机录屏验证，建议保持默认。",
                        color = Color(0xFF6E7FA0),
                        fontSize = 12.sp
                    )
                }
            }
            GlassCard(backdrop = backdrop) {
                Column(Modifier.padding(20.dp)) {
                    Text("使用说明", color = Color(0xFFE8F0FF), fontSize = 16.sp)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "1. 进入《三角洲行动》手游竞技爆破对局；\n" +
                            "2. 回到本应用，点「开始识别」并允许屏幕录制；\n" +
                            "3. 切回游戏，香奈美会随对局阶段自动播报。\n\n" +
                            "识别按 1920×1080 宽度等比、顶对齐归一化，适配全面屏比例。" +
                            "购买阶段打开全屏购买菜单时顶栏被遮挡，属正常现象，不影响播报。",
                        color = Color(0xFF9FB4D8),
                        fontSize = 13.sp
                    )
                }
            }
        }
    }
}
