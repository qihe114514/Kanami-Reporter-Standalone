package com.kanami.reporter.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.FileProvider
import org.json.JSONArray
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

/**
 * 官网与仓库地址。
 *
 * 手机端与桌面端**同一个仓库**，手机端代码在 `mobile` 分支上，发布也用该分支的 tag
 * （形如 `v1.2.1-mobile`），所以下载页指向仓库的 releases 列表。
 */
const val HOME_URL = "https://app.qihe0507.top/Kanami"
const val GITHUB_URL = "https://github.com/qihe114514/Kanami-Reporter-Standalone/releases"

private const val REPO = "qihe114514/Kanami-Reporter-Standalone"
private const val RELEASES_API = "https://api.github.com/repos/$REPO/releases"
private const val MOBILE_TAG_MARK = "mobile"

/**
 * GitHub 加速镜像前缀，用法是 `前缀 + 原始 GitHub 链接`。
 *
 * **国内用户直连 github.com 基本不通**，所以下载一律先直连试一次，失败就依次换镜像 ——
 * 和桌面端 `GitHubUpdateService` 用的是同一套地址与回退策略。
 * 清单只有几十 KB 也走同一套，免得出现"能查到更新但下不动"的割裂状态。
 */
private val MIRROR_PREFIXES = listOf(
    "https://gh-proxy.com/",
    "https://ghproxy.net/",
    "https://ghfast.top/"
)

private const val DIRECT_ROUTE = "直连"

private val ioExecutor = Executors.newSingleThreadExecutor { runnable ->
    Thread(runnable, "kanami-update").apply { isDaemon = true }
}

private fun mainHandler() = Handler(Looper.getMainLooper())

/** 检测到的新版本。 */
data class UpdateInfo(
    val latestVersion: String,
    /** APK 的 GitHub 原始直链；实际下载时会被镜像前缀包装。 */
    val apkUrl: String,
    /** 发布页地址，用于"前往发布页"。 */
    val releasePageUrl: String,
)

/**
 * 全局的"待更新"状态：启动检查或手动检查发现新版本后放进来，运行页据此把主按钮换成更新入口。
 */
object UpdateHolder {
    /** 非空表示有可用更新。 */
    var info: UpdateInfo? by mutableStateOf(null)
        private set

    val hasUpdate: Boolean get() = info != null

    fun set(value: UpdateInfo?) {
        info = value
    }
}

/** 依次尝试的路由：直连优先，失败再换镜像。 */
private fun buildRoutes(url: String): List<Pair<String, String>> =
    listOf(DIRECT_ROUTE to url) + MIRROR_PREFIXES.map { prefix -> prefix to prefix + url }

private fun openConnection(url: String): HttpURLConnection =
    (URL(url).openConnection() as HttpURLConnection).apply {
        connectTimeout = 15_000
        readTimeout = 60_000
        instanceFollowRedirects = true
        setRequestProperty("User-Agent", "KanamiReporter-Android")
    }

/**
 * 查仓库 release 列表，找**手机版**里版本号最大的那个，和本机版本比较。
 *
 * 不用 `/releases/latest` —— 那返回全仓库最新（桌面版发新版就指向桌面版了）。
 * 手机版的 tag 形如 `v1.2.1-mobile`，按 [MOBILE_TAG_MARK] 过滤后再比版本。
 *
 * @param onResult 主线程回调：有更新时给出 [UpdateInfo]，否则为 null；第二个参数是给人看的文案。
 */
fun checkUpdate(context: Context, onResult: (UpdateInfo?, String) -> Unit) {
    val appContext = context.applicationContext
    ioExecutor.execute {
        var info: UpdateInfo? = null
        val message = runCatching {
            val body = readTextWithFallback(RELEASES_API)
            val releases = JSONArray(body)
            val current = appVersion(appContext).trim().trimStart('v', 'V')

            var bestVersion: String? = null
            var bestItem: org.json.JSONObject? = null
            for (index in 0 until releases.length()) {
                val item = releases.optJSONObject(index) ?: continue
                if (item.optBoolean("draft")) continue
                val tag = item.optString("tag_name").trim()
                if (!tag.contains(MOBILE_TAG_MARK, ignoreCase = true)) continue
                val version = tag.trimStart('v', 'V').substringBefore('-').trim()
                if (version.isEmpty()) continue
                if (bestVersion == null || compareVersions(version, bestVersion!!) > 0) {
                    bestVersion = version
                    bestItem = item
                }
            }

            val latest = bestVersion
            if (latest == null) {
                "没能解析到手机版版本号，去发布页看看"
            } else if (compareVersions(latest, current) > 0) {
                val apkUrl = bestItem?.optJSONArray("assets")?.let { assets ->
                    (0 until assets.length())
                        .mapNotNull { assets.optJSONObject(it) }
                        .firstOrNull { it.optString("name").endsWith(".apk", ignoreCase = true) }
                        ?.optString("browser_download_url")
                }
                val pageUrl = bestItem?.optString("html_url").orEmpty().ifEmpty { GITHUB_URL }
                if (apkUrl.isNullOrEmpty()) {
                    "发现新版本 v$latest，但没找到安装包，去发布页看看"
                } else {
                    info = UpdateInfo(latest, apkUrl, pageUrl)
                    "发现新版本 v$latest（当前 v$current）"
                }
            } else {
                "已是最新版本（v$current）"
            }
        }.getOrElse { error ->
            "检查更新失败：${error.javaClass.simpleName}（可能是网络不通）"
        }
        mainHandler().post {
            UpdateHolder.set(info)
            onResult(info, message)
        }
    }
}

/** 读一段文本，直连不通就换镜像。 */
private fun readTextWithFallback(url: String): String {
    var lastError: Exception? = null
    for ((_, fullUrl) in buildRoutes(url)) {
        try {
            val connection = openConnection(fullUrl)
            return try {
                connection.inputStream.bufferedReader().use { it.readText() }
            } finally {
                runCatching { connection.disconnect() }
            }
        } catch (e: Exception) {
            lastError = e
        }
    }
    throw lastError ?: IllegalStateException("无法访问更新服务器")
}

/**
 * 下载更新包（**先直连、失败依次走镜像**），返回落地的 APK 文件。
 *
 * @param onProgress 0..100；拿不到总长度时回调 -1（表示进度未知）。
 */
fun downloadUpdateApk(context: Context, apkUrl: String, onProgress: (Int) -> Unit): File {
    val dir = File(context.cacheDir, "update").apply { mkdirs() }
    // 每次覆盖同一个文件，避免 cache 里堆一堆旧包
    val target = File(dir, "KanamiReporter-update.apk")
    var lastError: Exception? = null

    for ((label, fullUrl) in buildRoutes(apkUrl)) {
        try {
            val connection = openConnection(fullUrl)
            val total = connection.contentLengthLong
            connection.inputStream.use { input ->
                FileOutputStream(target).use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        output.write(buffer, 0, read)
                        done += read
                        onProgress(if (total > 0) ((done * 100) / total).toInt() else -1)
                    }
                }
            }
            runCatching { connection.disconnect() }
            if (target.length() <= 0) throw IllegalStateException("下载到空文件")
            return target
        } catch (e: Exception) {
            lastError = e
            runCatching { target.delete() }
        }
    }
    throw lastError ?: IllegalStateException("所有下载通道都失败")
}

/** 应用是否有权安装未知来源的应用。 */
fun canInstallPackages(context: Context): Boolean =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        context.packageManager.canRequestPackageInstalls()
    } else {
        true
    }

/** 跳到系统的「安装未知应用」授权页。 */
fun requestInstallPermission(context: Context) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
    runCatching {
        context.startActivity(
            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}

/** 让系统安装器打开已下载好的 APK。 */
fun installDownloadedApk(context: Context, file: File) {
    val uri: Uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
    context.startActivity(
        Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    )
}

/** 在浏览器里打开网址。 */
fun openUrl(context: Context, url: String) {
    runCatching {
        context.startActivity(
            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
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
