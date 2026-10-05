package com.kanami.reporter.ui.liquid

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke

/** 底栏图标（自绘，避免引入 material-icons 依赖）。 */
enum class TabIconKind { Run, Settings }

@Composable
fun TabIcon(kind: TabIconKind, color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val s = size.minDimension
        when (kind) {
            TabIconKind.Run -> {
                val path = Path().apply {
                    moveTo(s * 0.24f, s * 0.12f)
                    lineTo(s * 0.88f, s * 0.5f)
                    lineTo(s * 0.24f, s * 0.88f)
                    close()
                }
                drawPath(path, color)
            }

            TabIconKind.Settings -> {
                val rows = listOf(0.22f to 0.66f, 0.5f to 0.36f, 0.78f to 0.7f)
                rows.forEach { (y, knobX) ->
                    drawLine(
                        color = color,
                        start = Offset(s * 0.12f, s * y),
                        end = Offset(s * 0.88f, s * y),
                        strokeWidth = s * 0.1f,
                        cap = StrokeCap.Round
                    )
                    drawCircle(
                        color = color,
                        radius = s * 0.19f,
                        center = Offset(s * knobX, s * y),
                        style = Stroke(width = s * 0.1f)
                    )
                    drawCircle(
                        color = Color(0x00000000),
                        radius = s * 0.12f,
                        center = Offset(s * knobX, s * y)
                    )
                }
            }
        }
    }
}
