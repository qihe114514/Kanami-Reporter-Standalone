package com.kanami.reporter.debug

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.util.Log
import java.io.File
import java.io.FileWriter
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 调试日志：写入应用私有目录 `files/logs/`，并镜像到 logcat（tag=Kanami）。
 *
 * 打开调试模式后才真正写文件；logcat 始终输出，方便 adb 排查。
 * 写入量靠调用方节流（帧行 1/2s、分数行 1/2s），这里只在必要时 flush，避免影响识别节拍。
 */
object DebugLog {

    private const val LOGCAT_TAG = "Kanami"
    private const val MAX_FILES = 5
    private const val MAX_BYTES = 8L * 1024 * 1024
    private const val SHARE_MAX_BYTES = 256 * 1024
    private const val TAIL_CHARS = 192 * 1024

    private val lock = Any()
    private val throttleStamps = HashMap<String, Long>()
    private val timeFormat = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    private var logDir: File? = null
    private var writer: FileWriter? = null
    private var currentFile: File? = null
    private var bytesWritten = 0L

    @Volatile
    var enabled: Boolean = false
        set(value) {
            if (field == value) return
            field = value
            if (value) openSession() else closeSession()
        }

    /** 应用启动时调用：准备好日志目录、清理旧文件，若已开启调试模式则立刻开新会话文件。 */
    fun init(context: Context) {
        logDir = File(context.filesDir, "logs").apply { mkdirs() }
        pruneOldFiles()
        if (enabled) openSession(context)
    }

    fun log(tag: String, message: String) {
        val line = "${timeFormat.format(Date())} [${tag}] $message"
        Log.d(LOGCAT_TAG, line)
        if (!enabled) return
        synchronized(lock) {
            val w = writer ?: return
            try {
                if (bytesWritten >= MAX_BYTES) {
                    w.flush()
                    return
                }
                w.write(line)
                w.write("\n")
                bytesWritten += line.length + 1
                w.flush()
            } catch (_: IOException) {
            }
        }
    }

    /** 同一 key 在 intervalMs 内只记录一次（对付每帧都会触发的高频日志）。 */
    fun logThrottled(key: String, intervalMs: Long, tag: String, message: () -> String) {
        val now = System.currentTimeMillis()
        synchronized(throttleStamps) {
            val last = throttleStamps[key] ?: 0L
            if (now - last < intervalMs) return
            throttleStamps[key] = now
        }
        log(tag, message())
    }

    fun sessionPath(): String? = currentFile?.absolutePath

    fun sessionSizeBytes(): Long = bytesWritten

    /** 读取日志尾部若干行（给应用内查看用）。 */
    fun tail(maxLines: Int): List<String> {
        val f = currentFile ?: return listOf("（未开启调试模式，没有日志文件）")
        if (!f.exists()) return emptyList()
        return try {
            val text = f.readText()
            val trimmed = if (text.length > TAIL_CHARS) text.takeLast(TAIL_CHARS) else text
            trimmed.lines().takeLast(maxLines)
        } catch (e: IOException) {
            listOf("读取日志失败：$e")
        }
    }

    fun clear() {
        synchronized(lock) {
            try {
                writer?.flush()
            } catch (_: IOException) {
            }
            writer?.close()
            writer = null
            currentFile?.delete()
            currentFile = null
            bytesWritten = 0
        }
        logDir?.listFiles()?.forEach { it.delete() }
        log("debug", "日志已清空")
    }

    /** 导出到公共「下载」目录：Android 11+ 文件管理器进不去 Android/data，这是给用户取日志的通道。 */
    fun exportToDownloads(context: Context): String? {
        val f = currentFile ?: return null
        if (!f.exists()) return null
        val content = try {
            f.readText()
        } catch (e: IOException) {
            return null
        }
        val name = f.name
        return try {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.MIME_TYPE, "text/plain")
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS)
                }
            }
            val resolver = context.contentResolver
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: return null
            resolver.openOutputStream(uri)?.use { it.write(content.toByteArray()) }
            log("debug", "日志已导出到下载目录：$name")
            name
        } catch (e: Exception) {
            log("debug", "导出日志失败：$e")
            null
        }
    }

    /** 分享日志文本（不依赖 FileProvider，直接发文本，长度截断）。 */
    fun shareIntent(): Intent? {
        val f = currentFile ?: return null
        if (!f.exists()) return null
        val text = try {
            f.readText()
        } catch (e: IOException) {
            return null
        }
        val clipped = if (text.length > SHARE_MAX_BYTES) text.takeLast(SHARE_MAX_BYTES) else text
        return Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "香奈美x黑潮爆破 调试日志")
            putExtra(Intent.EXTRA_TEXT, clipped)
        }
    }

    /**
     * 按天续写日志。
     *
     * 原来每次启动都新建一个会话文件，于是"出问题 → 重启应用 → 导出日志"导出的正好是个
     * 只写了启动三行的新文件，真正的现场留在上一个文件里（用户看到的就是"日志里啥也没有"）。
     * 现在同一自然日内的多次启动都追加到同一个文件，超过 [MAX_BYTES] 才顺延成 -2、-3。
     */
    private fun openSession(context: Context? = null) {
        synchronized(lock) {
            closeSessionLocked()
            val dir = logDir ?: return
            dir.mkdirs()
            pruneOldFiles()
            val day = SimpleDateFormat("yyyyMMdd", Locale.US).format(Date())
            var index = 1
            var f = File(dir, "kanami-$day.log")
            while (f.length() >= MAX_BYTES) {
                index++
                f = File(dir, "kanami-$day-$index.log")
            }
            try {
                writer = FileWriter(f, true)
                currentFile = f
                bytesWritten = f.length()
            } catch (e: IOException) {
                writer = null
                currentFile = null
                return
            }
        }
        log("debug", "日志文件：${currentFile?.absolutePath}（同日续写）")
        context?.let { logDeviceInfo(it) }
    }

    private fun closeSession() {
        synchronized(lock) { closeSessionLocked() }
    }

    private fun closeSessionLocked() {
        try {
            writer?.flush()
            writer?.close()
        } catch (_: IOException) {
        }
        writer = null
    }

    private fun pruneOldFiles() {
        val dir = logDir ?: return
        val files = dir.listFiles { f -> f.isFile && f.name.endsWith(".log") }
            ?.sortedByDescending { it.lastModified() }
            ?: return
        files.drop(MAX_FILES).forEach { it.delete() }
    }

    fun logDeviceInfo(context: Context) {
        val version = try {
            context.packageManager.getPackageInfo(context.packageName, 0).versionName
        } catch (e: Exception) {
            "?"
        }
        log(
            "device",
            "型号=${Build.MANUFACTURER} ${Build.MODEL} Android=${Build.VERSION.RELEASE}(API ${Build.VERSION.SDK_INT}) " +
                "版本=$version ABI=${Build.SUPPORTED_ABIS.firstOrNull()}"
        )
    }
}
