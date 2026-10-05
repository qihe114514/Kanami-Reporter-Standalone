package com.kanami.reporter.ui

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.clickable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.isSpecified
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.Shadow
import com.kyant.shapes.Capsule

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

/** 简洁横向滑杆（拖动调节，用于阈值设置）。 */
@Composable
fun GlassSlider(
    value: Float,
    onValueChange: (Float) -> Unit,
    modifier: Modifier = Modifier,
    valueRange: ClosedFloatingPointRange<Float> = 0f..1f
) {
    val trackColor = Color(0xFF2A3B60)
    val fillColor = Color(0xFF6FA8FF)
    Box(
        modifier
            .fillMaxWidth()
            .height(36.dp)
            .pointerInput(valueRange) {
                detectHorizontalDragGestures(
                    onDragStart = { offset ->
                        val fraction = (offset.x / size.width).coerceIn(0f, 1f)
                        onValueChange(valueRange.start + fraction * (valueRange.endInclusive - valueRange.start))
                    },
                    onHorizontalDrag = { change, _ ->
                        change.consume()
                        val fraction = (change.position.x / size.width).coerceIn(0f, 1f)
                        onValueChange(valueRange.start + fraction * (valueRange.endInclusive - valueRange.start))
                    }
                )
            },
        contentAlignment = Alignment.CenterStart
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(6.dp)
                .clip(Capsule())
                .background(trackColor)
        )
        val fraction = ((value - valueRange.start) /
            (valueRange.endInclusive - valueRange.start).coerceAtLeast(1e-6f)).coerceIn(0f, 1f)
        Box(
            Modifier
                .fillMaxWidth(fraction)
                .height(6.dp)
                .clip(Capsule())
                .background(fillColor)
        )
        Box(
            Modifier
                .align(Alignment.CenterStart)
                .padding(start = (fraction * 324).dp)
                .height(24.dp)
                .width(24.dp)
                .clip(Capsule())
                .background(Color(0xFFDCE9FF))
        )
    }
}

/**
 * 液态玻璃组件集 —— 基于 Kyant0/AndroidLiquidGlass（Backdrop）。
 * 效果组合与官方示例组件（LiquidButton 等）一致：vibrancy + 轻模糊 + 边缘透镜折射。
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
                    vibrancy()
                    blur(4f.dp.toPx())
                    lens(10f.dp.toPx(), 20f.dp.toPx())
                },
                highlight = { Highlight.Default },
                shadow = { Shadow.Default }
            )
    ) {
        content()
    }
}

/** 液态玻璃按钮：按下时轻微缩放，松开回弹。 */
@Composable
fun LiquidButton(
    onClick: () -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    shape: Shape = Capsule(),
    tint: Color = Color.Unspecified,
    content: @Composable RowScope.() -> Unit
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale = remember { Animatable(1f) }
    LaunchedEffect(pressed) {
        scale.animateTo(
            if (pressed) 0.96f else 1f,
            spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMediumLow)
        )
    }
    Row(
        modifier
            .scale(scale.value)
            .drawBackdrop(
                backdrop = backdrop,
                shape = { shape },
                effects = {
                    vibrancy()
                    blur(2f.dp.toPx())
                    lens(12f.dp.toPx(), 24f.dp.toPx())
                },
                highlight = { Highlight.Default },
                shadow = { Shadow.Default },
                onDrawSurface = {
                    if (tint.isSpecified) {
                        drawRect(tint)
                    }
                }
            )
            .clickable(interactionSource = interaction, indication = null, onClick = onClick)
            .height(56.dp)
            .padding(horizontal = 24.dp),
        verticalAlignment = Alignment.CenterVertically,
        content = content
    )
}

/** 底部玻璃标签栏中的单个标签。 */
@Composable
fun LiquidTab(
    selected: Boolean,
    onClick: () -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit
) {
    Box(
        modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    if (selected) {
                        vibrancy()
                        blur(2f.dp.toPx())
                        lens(10f.dp.toPx(), 22f.dp.toPx())
                    } else {
                        blur(1f.dp.toPx())
                    }
                },
                highlight = { if (selected) Highlight.Default else null },
                shadow = { if (selected) Shadow.Default else null }
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 12.dp),
        contentAlignment = Alignment.Center
    ) {
        content()
    }
}
