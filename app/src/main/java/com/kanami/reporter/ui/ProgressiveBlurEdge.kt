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
 * **两个参数各管一件事，不要混着调**（上一版按比例定位，结果调高度时位置也跟着漂）：
 * - [fadeDistance]：**从屏幕边缘往里多少距离之后完全不模糊**。这是"模糊到哪里为止"，
 *   物理尺寸，与 [edgeHeight] 无关。想让内容更早/更晚进入模糊，只动这一个。
 * - [edgeHeight]：模糊带总共多高，也就是渐变有多长（比 [fadeDistance] 大出来的部分是纯渐变尾巴）。
 *   想让过渡更绵长柔和，只动这一个。
 *
 * API 33 以下没有 RuntimeShader，退化成单纯的一块模糊（仍有过渡感，只是没有渐隐）。
 */
@Composable
fun ProgressiveBlurEdge(
    backdrop: Backdrop,
    fromTop: Boolean,
    fadeDistance: Dp,
    modifier: Modifier = Modifier,
    edgeHeight: Dp = fadeDistance + 64.dp
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
                        // 把"物理距离"换算成 shader 要的比例；夹住上界避免渐变被压没
                        val fade = (fadeDistance.toPx() / size.height).coerceIn(0.05f, 1f)
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
                            setFloatUniform("fadeEnd", fade)
                        }
                    }
                }
            )
    )
}
