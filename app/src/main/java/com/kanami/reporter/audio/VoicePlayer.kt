package com.kanami.reporter.audio

import android.content.Context
import android.media.AudioAttributes
import android.media.MediaPlayer
import android.util.Log
import com.kanami.reporter.core.VoiceTable
import kotlin.random.Random

/**
 * 语音播报：从 assets/voices 按事件取文件播放。同一事件 3 秒内只播一次
 * （状态闪烁时防止连播）；同事件多条语音随机选择，与 PC 端一致。
 */
class VoicePlayer(private val context: Context) {

    private var player: MediaPlayer? = null
    private var volume = 1.0f
    private val lastPlayedAt = HashMap<String, Long>()
    private val random = Random.Default

    @Volatile
    var enabled = true

    @Synchronized
    fun setVolume(value: Float) {
        volume = value.coerceIn(0f, 1f)
        player?.setVolume(volume, volume)
    }

    @Synchronized
    fun playEvent(eventId: String) {
        if (!enabled) return
        val now = System.currentTimeMillis()
        val last = lastPlayedAt[eventId] ?: 0L
        if (now - last < 3000) return

        val files = VoiceTable.fileNamesFor(eventId)
        if (files.isEmpty()) return
        lastPlayedAt[eventId] = now

        val file = if (files.size == 1) files[0] else files[random.nextInt(files.size)]
        stopInternal()
        try {
            val p = MediaPlayer()
            p.setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            p.setDataSource(context.assets.openFd("voices/$file"))
            p.setVolume(volume, volume)
            p.setOnCompletionListener {
                it.release()
                if (player === it) player = null
            }
            p.prepare()
            p.start()
            player = p
        } catch (e: Exception) {
            Log.e("VoicePlayer", "play failed: $file", e)
        }
    }

    @Synchronized
    fun stop() {
        stopInternal()
    }

    private fun stopInternal() {
        player?.let {
            try {
                it.stop()
                it.release()
            } catch (_: Exception) {
            }
        }
        player = null
    }

    @Synchronized
    fun release() {
        stopInternal()
        lastPlayedAt.clear()
    }
}
