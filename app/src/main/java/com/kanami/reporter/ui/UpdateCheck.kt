package com.kanami.reporter.ui

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

/** 官网与仓库地址（与桌面版同一套；官网域名取自桌面端「关于」页）。 */
const val HOME_URL = "https://app.qihe0507.top/Kanami"
const val GITHUB_URL = "https://github.com/qihe114514/Kanami-Reporter-Mobile"

private const val LATEST_RELEASE_API =
    "https://api.github.com/repos/qihe114514/Kanami-Reporter-Mobile/releases/latest"

private val updateExecutor = Executors.newSingleThreadExecutor { runnable ->
    Thread(runnable, "kanami-update-check").apply { isDaemon = true }
}

/**
 * 检查有无新版本：查 GitHub Releases 的最新 tag，和本机版本号比大小。
 *
 * 结果通过 [onResult] 回到主线程（只是一句给人看的中文，不做自动下载 —— 更新包由用户自己去
 * 发布页取，避免在应用里塞一套下载/校验/安装逻辑）。
 */
fun checkUpdate(context: Context, onResult: (String) -> Unit) {
    val appContext = context.applicationContext
    updateExecutor.execute {
        val message = runCatching {
            val connection = (URL(LATEST_RELEASE_API).openConnection() as HttpURLConnection).apply {
                connectTimeout = 8_000
                readTimeout = 8_000
                setRequestProperty("Accept", "application/vnd.github+json")
                setRequestProperty("User-Agent", "KanamiReporter-Android")
            }
            val body = try {
                connection.inputStream.bufferedReader().use { it.readText() }
            } finally {
                runCatching { connection.disconnect() }
            }
            val latest = JSONObject(body).optString("tag_name").trim().trimStart('v', 'V')
            val current = appVersion(appContext).trim().trimStart('v', 'V')
            when {
                latest.isEmpty() -> "没能解析到最新版本号，去 GitHub 发布页看看"
                compareVersions(latest, current) > 0 -> "发现新版本 v$latest（当前 v$current），可在 GitHub 发布页下载"
                else -> "已是最新版本（v$current）"
            }
        }.getOrElse { error ->
            "检查更新失败：${error.javaClass.simpleName}（可能是网络不通）"
        }
        Handler(Looper.getMainLooper()).post { onResult(message) }
    }
}

/** 按 `.` 分段比数字，`1.10.0` > `1.9.0`。段数不齐的短的那边按 0 补齐。 */
internal fun compareVersions(a: String, b: String): Int {
    val left = a.split('.')
    val right = b.split('.')
    for (index in 0 until maxOf(left.size, right.size)) {
        val l = left.getOrNull(index)?.toIntOrNull() ?: 0
        val r = right.getOrNull(index)?.toIntOrNull() ?: 0
        if (l != r) return l - r
    }
    return 0
}
