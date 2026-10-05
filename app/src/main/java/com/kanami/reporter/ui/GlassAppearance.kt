package com.kanami.reporter.ui

import android.graphics.Bitmap
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * 液态玻璃的可调外观（设置页「个性化」里调）。
 *
 * 值都是**倍率**，1f = 设计默认。读取发生在绘制阶段（backdrop 的 effects / layerBlock 里），
 * 所以拖动滑块时只会重绘、不会重组。
 */
object GlassTuning {

    /** 折射/反射强度（lens 的位移量）。 */
    var refraction: Float by mutableFloatStateOf(1f)

    /** 模糊强度。 */
    var blur: Float by mutableFloatStateOf(1f)

    const val MIN = 0f
    const val MAX = 2.2f
    const val DEFAULT = 1f

    fun reset() {
        refraction = DEFAULT
        blur = DEFAULT
    }
}

/**
 * 自适应对比度：按背景亮度决定文字用浅色还是深色，亮背景上不再糊成一团。
 *
 * 背景是**静态壁纸**，所以只在加载壁纸时算一次，运行时零开销。
 * 官方示例（`destinations/AdaptiveLuminanceGlassContent.kt`）是每帧把 GPU 图层
 * `toImageBitmap()` 读回 CPU 取平均亮度 —— 那是 demo 写法，1272×2772 的图层一秒读几十次
 * 在真机上会直接拖垮帧率，不能照搬。
 */
object AdaptiveGlass {

    /** 背景是否偏亮。 */
    var isLightBackground: Boolean by mutableStateOf(false)
        private set

    /** 从壁纸取上/中/下三段亮度，用中段决定配色（内容主要压在屏幕中部）。 */
    fun updateFromWallpaper(bitmap: Bitmap) {
        isLightBackground = runCatching {
            val tiny = Bitmap.createScaledBitmap(bitmap, 1, 3, true)
            val px = IntArray(3)
            tiny.getPixels(px, 0, 1, 0, 0, 1, 3)
            tiny.recycle()
            luminanceOf(px[1]) > 0.55f
        }.getOrDefault(false)
    }

    private fun luminanceOf(argb: Int): Float {
        val r = (argb shr 16 and 0xFF) / 255f
        val g = (argb shr 8 and 0xFF) / 255f
        val b = (argb and 0xFF) / 255f
        return 0.2126f * r + 0.7152f * g + 0.0722f * b
    }
}
