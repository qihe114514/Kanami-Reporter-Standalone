// 来源：Kyant0/AndroidLiquidGlass（tag 2.0.1）app/.../catalog/components/LiquidBottomTabs.kt
// 许可：Apache-2.0。改动：包名；去掉 isSystemInDarkTheme；**修掉索引同步的 bug**（见下）。
//
// 官方这份动画是完整的：胶囊玻璃 + 滑动透镜指示器 + 拖动时整条底栏跟着轻微平移（panelOffset）
// + 按住时指示器按触摸点挤压并泛径向高光。之前我把它简化过一版，动画就丢了，现在恢复官方实现。
//
// 唯一改动是索引同步：
//   官方写的是 `remember(selectedTabIndex)` —— 那个 lambda 每次重组都是新实例，会把 currentIndex
//   连同它和指示器动画的同步一起重建。症状是「页面切了、底栏指示器不动」，而且停在旧位置的
//   指示器还盖住了那一格的点击，看起来像"点不动"。
//   现在：currentIndex 用稳定 key，外部索引变化时主动驱动指示器动画；
//   拖动结束时直接回调 onTabSelected（不再绕一层快照流，避免 drop(1) 吃掉第一次变化）。
package com.kanami.reporter.ui.liquid

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.spring
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.fastRoundToInt
import androidx.compose.ui.util.lerp
import com.kanami.reporter.ui.GlassTuning
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberCombinedBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.InnerShadow
import com.kyant.backdrop.shadow.Shadow
import com.kyant.shapes.Capsule
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sign

/**
 * 液态玻璃底栏（官方示例组件）。
 *
 * @param selectedTabIndex 选中的下标。**注意这里是值、不是 lambda**：官方签名是 `() -> Int`，
 *   但 Compose 会把"只捕获稳定值的 lambda"记忆化 —— `{ tab }` 每次重组都是同一个实例，
 *   于是本组件被判为"参数没变"而跳过重组，外部切页时指示器就再也不会动。
 *   传值则会真正触发重组，[LaunchedEffect] 才会被重新拉起。
 * @param tabsCount 必须等于 [LiquidBottomTab] 子项数量。
 */
@Composable
fun LiquidBottomTabs(
    selectedTabIndex: Int,
    onTabSelected: (index: Int) -> Unit,
    backdrop: Backdrop,
    tabsCount: Int,
    modifier: Modifier = Modifier,
    accentColor: Color = Color(0xFF7FB0FF),
    containerColor: Color = Color(0xFF101A2E).copy(alpha = 0.42f),
    content: @Composable RowScope.() -> Unit
) {
    val tabsBackdrop = rememberLayerBackdrop()

    BoxWithConstraints(
        modifier,
        contentAlignment = Alignment.CenterStart
    ) {
        val density = LocalDensity.current
        val tabWidth = with(density) {
            (constraints.maxWidth.toFloat() - 8f.dp.toPx()) / tabsCount
        }

        val offsetAnimation = remember { Animatable(0f) }
        // 拖动时整条底栏跟着轻微平移。只在绘制阶段读 .value，别让它触发重组。
        val panelOffset = remember(density) {
            derivedStateOf {
                val fraction = (offsetAnimation.value / constraints.maxWidth).fastCoerceIn(-1f, 1f)
                with(density) {
                    4f.dp.toPx() * fraction.sign * EaseOut.transform(abs(fraction))
                }
            }
        }

        val isLtr = LocalLayoutDirection.current == LayoutDirection.Ltr
        val animationScope = rememberCoroutineScope()
        val externalIndex = selectedTabIndex.fastCoerceIn(0, tabsCount - 1)
        var currentIndex by remember { mutableIntStateOf(externalIndex) }
        val dampedDragAnimation = remember(animationScope) {
            DampedDragAnimation(
                animationScope = animationScope,
                initialValue = currentIndex.toFloat(),
                valueRange = 0f..(tabsCount - 1).toFloat(),
                visibilityThreshold = 0.001f,
                initialScale = 1f,
                pressedScale = 78f / 56f,
                onDragStarted = {},
                onDragStopped = {
                    val targetIndex = targetValue.fastRoundToInt().fastCoerceIn(0, tabsCount - 1)
                    val changed = targetIndex != currentIndex
                    currentIndex = targetIndex
                    animateToValue(targetIndex.toFloat())
                    animationScope.launch {
                        offsetAnimation.animateTo(0f, spring(1f, 300f, 0.5f))
                    }
                    // 拖到别的格子就直接通知外部；点（零位移的拖动）落到原格时 changed 为 false，不回调
                    if (changed) onTabSelected(targetIndex)
                },
                onDrag = { _, dragAmount ->
                    updateValue(
                        (targetValue + dragAmount.x / tabWidth * if (isLtr) 1f else -1f)
                            .fastCoerceIn(0f, (tabsCount - 1).toFloat())
                    )
                    animationScope.launch {
                        offsetAnimation.snapTo(offsetAnimation.value + dragAmount.x)
                    }
                }
            )
        }
        // 外部索引（点 tab 项、或代码切换）→ 指示器跟着滑过去。
        // 用"值当 key 的 LaunchedEffect"，值一变 effect 必定重启、必定驱动动画
        //（之前用 snapshotFlow 包一层时，点 tab 后页面切了、指示器却纹丝不动）。
        LaunchedEffect(externalIndex) {
            currentIndex = externalIndex
            // 首次组合时指示器本来就在这个位置，不用动画（否则它会"啵"一下）
            if (abs(dampedDragAnimation.value - externalIndex) > 0.001f) {
                dampedDragAnimation.animateToValue(externalIndex.toFloat())
            }
        }
        val interactiveHighlight = remember(animationScope) {
            InteractiveHighlight(
                animationScope = animationScope,
                position = { size, offset ->
                    // 这个 lambda 在绘制时执行，所以在这里读 panelOffset.value 不会引起重组
                    val shifted = panelOffset.value
                    Offset(
                        if (isLtr) (dampedDragAnimation.value + 0.5f) * tabWidth + shifted
                        else size.width - (dampedDragAnimation.value + 0.5f) * tabWidth + shifted,
                        size.height / 2f
                    )
                }
            )
        }

        Row(
            Modifier
                .graphicsLayer {
                    translationX = panelOffset.value
                }
                .drawBackdrop(
                    backdrop = backdrop,
                    shape = { Capsule() },
                    effects = {
                        vibrancy()
                        blur(8f.dp.toPx() * GlassTuning.blur)
                        lens(
                            24f.dp.toPx() * GlassTuning.refraction,
                            24f.dp.toPx() * GlassTuning.refraction
                        )
                    },
                    layerBlock = {
                        val progress = dampedDragAnimation.pressProgress
                        val scale = lerp(1f, 1f + 16f.dp.toPx() / size.width, progress)
                        scaleX = scale
                        scaleY = scale
                    },
                    onDrawSurface = { drawRect(containerColor) }
                )
                .then(interactiveHighlight.modifier)
                .height(64f.dp)
                .fillMaxWidth()
                .padding(4f.dp),
            verticalAlignment = Alignment.CenterVertically,
            content = content
        )

        CompositionLocalProvider(
            LocalLiquidBottomTabScale provides {
                lerp(1f, 1.2f, dampedDragAnimation.pressProgress)
            }
        ) {
            Row(
                Modifier
                    .clearAndSetSemantics {}
                    .alpha(0f)
                    .layerBackdrop(tabsBackdrop)
                    .graphicsLayer {
                        translationX = panelOffset.value
                    }
                    .drawBackdrop(
                        backdrop = backdrop,
                        shape = { Capsule() },
                        effects = {
                            val progress = dampedDragAnimation.pressProgress
                            vibrancy()
                            blur(8f.dp.toPx() * GlassTuning.blur)
                            lens(
                                24f.dp.toPx() * progress * GlassTuning.refraction,
                                24f.dp.toPx() * progress * GlassTuning.refraction
                            )
                        },
                        highlight = {
                            val progress = dampedDragAnimation.pressProgress
                            Highlight.Default.copy(alpha = progress)
                        },
                        onDrawSurface = { drawRect(containerColor) }
                    )
                    .then(interactiveHighlight.modifier)
                    .height(56f.dp)
                    .fillMaxWidth()
                    .padding(horizontal = 4f.dp)
                    .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                verticalAlignment = Alignment.CenterVertically,
                content = content
            )
        }

        Box(
            Modifier
                .padding(horizontal = 4f.dp)
                .graphicsLayer {
                    translationX =
                        if (isLtr) dampedDragAnimation.value * tabWidth + panelOffset.value
                        else size.width - (dampedDragAnimation.value + 1f) * tabWidth + panelOffset.value
                }
                .then(interactiveHighlight.gestureModifier)
                .then(dampedDragAnimation.modifier)
                .drawBackdrop(
                    backdrop = rememberCombinedBackdrop(backdrop, tabsBackdrop),
                    shape = { Capsule() },
                    effects = {
                        val progress = dampedDragAnimation.pressProgress
                        lens(
                            10f.dp.toPx() * progress * GlassTuning.refraction,
                            14f.dp.toPx() * progress * GlassTuning.refraction,
                            chromaticAberration = true
                        )
                    },
                    highlight = {
                        val progress = dampedDragAnimation.pressProgress
                        Highlight.Default.copy(alpha = progress)
                    },
                    shadow = {
                        val progress = dampedDragAnimation.pressProgress
                        Shadow(alpha = progress)
                    },
                    innerShadow = {
                        val progress = dampedDragAnimation.pressProgress
                        InnerShadow(
                            radius = 8f.dp * progress,
                            alpha = progress
                        )
                    },
                    layerBlock = {
                        scaleX = dampedDragAnimation.scaleX
                        scaleY = dampedDragAnimation.scaleY
                        val velocity = dampedDragAnimation.velocity / 10f
                        scaleX /= 1f - (velocity * 0.75f).fastCoerceIn(-0.2f, 0.2f)
                        scaleY *= 1f - (velocity * 0.25f).fastCoerceIn(-0.2f, 0.2f)
                    },
                    onDrawSurface = {
                        val progress = dampedDragAnimation.pressProgress
                        drawRect(
                            Color.White.copy(0.1f),
                            alpha = 1f - progress
                        )
                        drawRect(Color.Black.copy(alpha = 0.03f * progress))
                    }
                )
                .height(56f.dp)
                .fillMaxWidth(1f / tabsCount)
        )
    }
}
