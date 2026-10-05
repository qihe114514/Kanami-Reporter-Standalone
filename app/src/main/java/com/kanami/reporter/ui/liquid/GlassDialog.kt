// 视觉参数取自 Kyant0/AndroidLiquidGlass 的示例 destinations/DialogContent.kt
// （Apache-2.0）：大圆角玻璃面板 + colorControls 提饱和 + blur + 带 depthEffect 的 lens，
// 外加深色压暗遮罩。
//
// 与示例的区别：示例把弹窗画在自己的 Scaffold 里，这里做成**盖在同一棵界面树上的 overlay**，
// 而不是 Compose 的 Dialog 独立窗口 —— 独立窗口拿不到主界面的 layer backdrop，
// 玻璃就折不出下面的内容了。
package com.kanami.reporter.ui.liquid

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.kanami.reporter.ui.GlassTuning
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.colorControls
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.highlight.Highlight
import com.kyant.shapes.RoundedRectangle

/**
 * 液态玻璃弹窗容器：全屏压暗遮罩 + 居中的玻璃面板，点遮罩关闭、点面板不关闭。
 */
@Composable
fun GlassDialog(
    backdrop: Backdrop,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit
) {
    Box(
        modifier
            .fillMaxSize()
            .drawWithContent {
                drawContent()
                drawRect(Color(0xFF0A0A0A).copy(alpha = 0.58f))
            }
            .clickable(interactionSource = null, indication = null, onClick = onDismiss),
        contentAlignment = Alignment.Center
    ) {
        Column(
            Modifier
                .padding(horizontal = 28.dp)
                .fillMaxWidth()
                .drawBackdrop(
                    backdrop = backdrop,
                    shape = { RoundedRectangle(32.dp) },
                    effects = {
                        colorControls(brightness = 0f, saturation = 1.5f)
                        blur(8.dp.toPx() * GlassTuning.blur)
                        lens(
                            24.dp.toPx() * GlassTuning.refraction,
                            48.dp.toPx() * GlassTuning.refraction,
                            depthEffect = true
                        )
                    },
                    highlight = { Highlight.Plain },
                    onDrawSurface = { drawRect(Color(0xFF121212).copy(alpha = 0.42f)) }
                )
                // 吃掉面板上的点击，否则点到面板也会被外层的 onDismiss 接走
                .clickable(interactionSource = null, indication = null) { },
            content = content
        )
    }
}

/** 弹窗底部的按钮行：两个等宽胶囊，左次要、右主要。 */
@Composable
fun GlassDialogActions(
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit
) {
    Row(
        modifier
            .fillMaxWidth()
            .padding(start = 20.dp, end = 20.dp, top = 8.dp, bottom = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
        content = { content() }
    )
}
