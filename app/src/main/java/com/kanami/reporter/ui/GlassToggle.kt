package com.kanami.reporter.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.Shadow
import com.kyant.shapes.Capsule

/** 液态玻璃开关（自绘，避免引入 material3）。 */
@Composable
fun GlassToggle(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    title: String,
    subtitle: String? = null
) {
    Row(
        modifier
            .fillMaxWidth()
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    vibrancy()
                    blur(2f.dp.toPx())
                    lens(10f.dp.toPx(), 20f.dp.toPx())
                },
                highlight = { Highlight.Default },
                shadow = { Shadow.Default }
            )
            .clickable { onCheckedChange(!checked) }
            .padding(horizontal = 18.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(title, color = Color(0xFFE8F0FF), fontSize = 15.sp, fontWeight = FontWeight.Medium)
            }
        }
        if (subtitle != null) {
            Text(subtitle, color = Color(0xFF6E7FA0), fontSize = 11.sp)
            Spacer(Modifier.width(10.dp))
        }
        Box(
            Modifier
                .width(46.dp)
                .height(26.dp)
                .clip(Capsule())
                .background(if (checked) Color(0xFF4E8CFF) else Color(0xFF2A3B60)),
            contentAlignment = if (checked) Alignment.CenterEnd else Alignment.CenterStart
        ) {
            Box(
                Modifier
                    .padding(horizontal = 3.dp)
                    .size(20.dp)
                    .clip(Capsule())
                    .background(Color(0xFFDCE9FF))
            )
        }
    }
}
