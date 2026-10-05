package com.kanami.reporter.debug

import android.content.Context
import android.graphics.Bitmap
import android.os.SystemClock
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 调试用帧导出：把采集到的原始帧（RGBA）与归一化灰度帧写成 PNG，落在 `files/debug/`。
 *
 * 归一化帧与 `.krt` 模板同坐标系（1920×1080 顶对齐），是判断"模板为什么没命中"最直接的证据。
 */
object FrameDump {

    private const val NORM_WIDTH = 1920
    private const val NORM_HEIGHT = 1080

    fun dir(context: Context): File = File(context.filesDir, "debug").apply { mkdirs() }

    /** 保存原始采集帧。返回写出的文件，失败返回 null。 */
    fun saveRaw(context: Context, rgba: ByteArray, width: Int, height: Int): File? {
        if (width <= 0 || height <= 0 || rgba.size < width * height * 4) return null
        return try {
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            bitmap.copyPixelsFromBuffer(ByteBuffer.wrap(rgba, 0, width * height * 4))
            val file = File(dir(context), "frame-${stamp()}-${width}x${height}.png")
            FileOutputStream(file).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()
            file
        } catch (e: Exception) {
            DebugLog.log("dump", "保存原始帧失败：$e")
            null
        }
    }

    /** 保存归一化灰度帧（与模板同坐标系，含内容高度）。 */
    fun saveNormalized(context: Context, gray: ByteArray, contentHeight: Int): File? {
        if (gray.size < NORM_WIDTH * NORM_HEIGHT) return null
        return try {
            val pixels = IntArray(NORM_WIDTH * NORM_HEIGHT)
            for (i in pixels.indices) {
                val v = gray[i].toInt() and 0xFF
                pixels[i] = (0xFF shl 24) or (v shl 16) or (v shl 8) or v
            }
            val bitmap = Bitmap.createBitmap(pixels, NORM_WIDTH, NORM_HEIGHT, Bitmap.Config.ARGB_8888)
            val file = File(dir(context), "norm-${stamp()}-h$contentHeight.png")
            FileOutputStream(file).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()
            file
        } catch (e: Exception) {
            DebugLog.log("dump", "保存归一化帧失败：$e")
            null
        }
    }

    /** 供调试卡展示/分享：最近的 dump 文件列表（新的在前）。 */
    fun recent(context: Context, limit: Int = 10): List<File> =
        dir(context).listFiles { f -> f.isFile }
            ?.sortedByDescending { it.lastModified() }
            ?.take(limit)
            ?: emptyList()

    fun cleanupOld(context: Context, keep: Int = 20) {
        try {
            dir(context).listFiles { f -> f.isFile }
                ?.sortedByDescending { it.lastModified() }
                ?.drop(keep)
                ?.forEach { it.delete() }
        } catch (_: IOException) {
        }
    }

    private fun stamp(): String =
        SimpleDateFormat("MMdd-HHmmss", Locale.US).format(Date(System.currentTimeMillis())) +
            "-" + (SystemClock.elapsedRealtime() % 1000)
}
