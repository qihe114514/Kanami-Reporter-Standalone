package com.kanami.reporter.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kanami.reporter.ui.liquid.GlassDialog
import com.kanami.reporter.ui.liquid.GlassDialogActions
import com.kanami.reporter.ui.liquid.LiquidButton
import com.kyant.backdrop.Backdrop

/** 更新弹窗里正在进行的动作状态。 */
data class UpdateDialogState(
    val downloading: Boolean = false,
    /** 0..100；-1 表示拿不到总长度。 */
    val progress: Int = -1,
    val message: String? = null,
)

/**
 * 「发现新版本」弹窗：左边去发布页（浏览器打开），右边直接下载并安装。
 *
 * 下载走的是镜像回退（见 [downloadUpdateApk]），不直连 GitHub。
 */
@Composable
fun UpdateDialog(
    backdrop: Backdrop,
    info: UpdateInfo,
    currentVersion: String,
    state: UpdateDialogState,
    onDismiss: () -> Unit,
    onOpenReleasePage: () -> Unit,
    onDownloadAndInstall: () -> Unit
) {
    GlassDialog(backdrop = backdrop, onDismiss = onDismiss) {
        Column(Modifier.padding(start = 24.dp, end = 24.dp, top = 26.dp)) {
            Text("发现新版本", color = StatusColors.text, fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(10.dp))
            Text(
                "v${info.latestVersion}（当前 v$currentVersion）",
                color = StatusColors.ok,
                fontSize = 15.sp
            )
            Spacer(Modifier.height(10.dp))
            Text(
                "下载安装包会走加速通道（不直连 GitHub）；装之前系统会先问你一次是否允许本应用安装应用。",
                color = StatusColors.idle,
                fontSize = 13.sp
            )
            state.message?.let {
                Spacer(Modifier.height(10.dp))
                Text(it, color = StatusColors.warn, fontSize = 13.sp)
            }
            if (state.downloading) {
                Spacer(Modifier.height(10.dp))
                Text(
                    if (state.progress >= 0) "正在下载… ${state.progress}%" else "正在下载…",
                    color = StatusColors.ok,
                    fontSize = 13.sp
                )
            }
        }

        GlassDialogActions(Modifier.fillMaxWidth()) {
            LiquidButton(
                onClick = onOpenReleasePage,
                backdrop = backdrop,
                modifier = Modifier.weight(1f),
                height = 46.dp
            ) {
                Text("前往发布页", color = StatusColors.text, fontSize = 15.sp)
            }
            LiquidButton(
                onClick = { if (!state.downloading) onDownloadAndInstall() },
                backdrop = backdrop,
                modifier = Modifier.weight(1f),
                height = 46.dp
            ) {
                Text(
                    when {
                        state.downloading && state.progress >= 0 -> "${state.progress}%"
                        state.downloading -> "下载中…"
                        else -> "下载并安装"
                    },
                    color = if (state.downloading) StatusColors.dim else Color(0xFFBFE0FF),
                    fontSize = 15.sp
                )
            }
        }
    }
}
