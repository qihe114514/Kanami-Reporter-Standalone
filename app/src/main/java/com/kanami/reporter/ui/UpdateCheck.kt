package com.kanami.reporter.ui

import android.content.Context
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

/**
 * 官网与仓库地址。
 *
 * 手机端与桌面端**同一个仓库**，手机端代码在 `mobile` 分支上，发布也用该分支的 tag
 * （形如 `v1.2.0-mobile`），所以下载页指向仓库的 releases 列表而不是某个固定 tag。
 */
const val HOME_URL = "https://app.qihe0507.top/Kanami"
const val GITHUB_URL = "https://github.com/qihe114514/Kanami-Reporter-Standalone/releases"

private const val RELEASES_API =
    "https://api.github.com/repos/qihe114514/Kanami-Reporter-Standalone/releases"

/** 手机端发布 tag 的标记，用来把手机版和桌面版的 release 区分开。 */
private const val MOBILE_TAG_MARK = "mobile"

private val updateExecutor = Executors.newSingleThreadExecutor { runnable ->
    Thread(runnable, "kanami-update-check").apply { isDaemon = true }
}

/**
 * 检查有无新版本：在仓库的 release 列表里找**手机端**的最新一个，和本机版本号比大小。
 *
 * 不能直接用 `/releases/latest` —— 那返回的是全仓库最新，桌面版发新版本时会把手机版顶掉。
 * 手机端的 tag 形如 `v1.2.0-mobile`，这里按 [MOBILE_TAG_MARK] 过滤后再比版本。
 *
 * 结果通过 [onResult] 回到主线程（只是一句给人看的中文，不做自动下载 —— 更新包由用户自己去
 * 发布页取，避免在应用里塞一套下载/校验/安装逻辑）。
 */
fun checkUpdate(context: Context, onResult: (String) -> Unit) {
    val appContext = context.applicationContext
    updateExecutor.execute {
        val message = runCatching {
            val connection = (URL(RELEASES_API).openConnection() as HttpURLConnection).apply {
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

            val releases = JSONArray(body)
            var latest: String? = null
            for (index in 0 until releases.length()) {
                val item = releases.optJSONObject(index) ?: continue
                if (item.optBoolean("draft")) continue
                val tag = item.optString("tag_name").trim()
                if (!tag.contains(MOBILE_TAG_MARK, ignoreCase = true)) continue
                val version = tag.trimStart('v', 'V').substringBefore('-').trim()
                if (latest == null || compareVersions(version, latest!!) > 0) latest = version
            }

            val current = appVersion(appContext).trim().trimStart('v', 'V')
            when {
                latest.isNullOrEmpty() -> "没能解析到手机版版本号，去发布页看看"
                compareVersions(latest!!, current) > 0 ->
                    "发现新版本 v$latest（当前 v$current），可在发布页下载"
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
