package com.kanami.reporter.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.isSpecified
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kanami.reporter.ui.liquid.LiquidButton
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.Shadow

/** 轻量文本（替代 material3.Text，随本项目去 material3 化）。 */
@Composable
fun Text(
    text: String,
    modifier: Modifier = Modifier,
    color: Color = Color.Unspecified,
    fontSize: TextUnit = TextUnit.Unspecified,
    fontWeight: FontWeight? = null,
    fontFamily: FontFamily? = null
) {
    BasicText(
        text,
        modifier,
        style = TextStyle(
            color = if (color.isSpecified) color else Color.Unspecified,
            fontSize = fontSize,
            fontWeight = fontWeight,
            fontFamily = fontFamily
        )
    )
}

/**
 * 液态玻璃组件集 —— 基于 Kyant0/AndroidLiquidGlass（Backdrop）。
 *
 * 按钮、底栏、滑杆、开关全部用官方示例组件（见 `ui/liquid/`），按下反馈统一是
 * "按触摸点挤压玻璃 + 径向高亮 + 弹回"，不要再自绘。这里只留卡片与入口型小按钮。
 */

/** 玻璃容器卡片。 */
@Composable
fun GlassCard(
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    shape: Shape = RoundedCornerShape(24.dp),
    content: @Composable () -> Unit
) {
    Box(
        modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { shape },
                effects = {
                    // 强度倍率来自设置页「个性化」；在绘制阶段读，调滑块只重绘不重组
                    val blurScale = GlassTuning.blur
                    val lensScale = GlassTuning.refraction
                    vibrancy()
                    blur(4f.dp.toPx() * blurScale)
                    lens(10f.dp.toPx() * lensScale, 20f.dp.toPx() * lensScale)
                },
                highlight = { Highlight.Default },
                shadow = { Shadow.Default }
            )
    ) {
        content()
    }
}

/**
 * 小号玻璃按钮（权限跳转、调试操作等密集场景用）。
 *
 * 就是矮一号的 [LiquidButton]，这样「开始识别」和「开启无障碍」按下去的手感完全一致；
 * 以前这里只有 clickable，没有按压反馈，看起来像另一套控件。
 */
@Composable
fun GlassChip(
    text: String,
    onClick: () -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier
) {
    LiquidButton(
        onClick = onClick,
        backdrop = backdrop,
        modifier = modifier,
        height = 36.dp
    ) {
        Text(text, color = StatusColors.text, fontSize = 12.sp)
    }
}
