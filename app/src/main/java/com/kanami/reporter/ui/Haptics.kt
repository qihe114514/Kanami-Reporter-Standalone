package com.kanami.reporter.ui

import android.view.HapticFeedbackConstants
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalView

/**
 * 按压震动反馈：液态玻璃的"挤压"视觉之外再给一点触觉，点按更像真的按到东西。
 * 返回一个无参函数，调用即触发一次轻震动。
 */
@Composable
fun rememberHapticTick(): () -> Unit {
    val view = LocalView.current
    return remember(view) {
        { view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY) }
    }
}
