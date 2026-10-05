package com.kanami.reporter.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawPlainBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.runtimeShaderEffect
import com.kyant.backdrop.isRuntimeShaderSupported

/**
 * 滚动区上下边缘的**渐进式模糊**：内容滚到边缘时不是被硬切掉，而是逐渐模糊、淡出。
 *
 * 做法照抄官方示例 destinations/ProgressiveBlurContent.kt：先 `blur`，再用
 * `runtimeShaderEffect` 按纵向坐标做 alpha mask（越靠边越不透明）。
 * 折射源是"背景壁纸 + 滚动内容"合并后的 backdrop，所以模糊的确实是页面内容本身。
 *
 * [fadeEnd] 决定"完全不模糊"落在模糊带的哪个位置（0 = 紧贴边缘，1 = 模糊带的内侧尽头）。
 * **它和 [edgeHeight] 是两个独立的东西**：高度决定渐变有多长（越大越柔和），
 * fadeEnd 决定从哪里开始变模糊。想要"很早就开始模糊、而且过渡很长"，就把高度调大、fadeEnd 调小。
 *
 * API 33 以下没有 RuntimeShader，退化成单纯的一块模糊（仍有过渡感，只是没有渐隐）。
 */
@Composable
fun ProgressiveBlurEdge(
    backdrop: Backdrop,
    fromTop: Boolean,
    modifier: Modifier = Modifier,
    edgeHeight: Dp = 72.dp,
    fadeEnd: Float = 0.35f
) {
    val shaderSupported = isRuntimeShaderSupported()
    Box(
        modifier
            .fillMaxWidth()
            .height(edgeHeight)
            .drawPlainBackdrop(
                backdrop = backdrop,
                shape = { RectangleShape },
                effects = {
                    blur(10f.dp.toPx())
                    if (shaderSupported) {
                        runtimeShaderEffect(
                            "KanamiEdgeMask",
                            """
    uniform shader content;
    uniform float2 size;
    uniform float fromTop;
    uniform float fadeEnd;

    half4 main(float2 coord) {
        float t = fromTop > 0.5 ? coord.y / size.y : 1.0 - coord.y / size.y;
        float alpha = 1.0 - smoothstep(0.0, fadeEnd, t);
        return content.eval(coord) * alpha;
    }""",
                            "content"
                        ) {
                            setFloatUniform("size", size.width, size.height)
                            setFloatUniform("fromTop", if (fromTop) 1f else 0f)
                            setFloatUniform("fadeEnd", fadeEnd)
                        }
                    }
                }
            )
    )
}
