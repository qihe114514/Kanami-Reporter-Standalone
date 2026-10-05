package com.kanami.reporter

import android.app.Application
import com.kanami.reporter.core.RecognitionEngine
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.settings.Settings
import com.kanami.reporter.status.StatusHub
import com.kanami.reporter.ui.GlassTuning
import com.kanami.reporter.ui.checkUpdate

class KanamiApp : Application() {

    lateinit var engine: RecognitionEngine
        private set

    lateinit var settings: Settings
        private set

    override fun onCreate() {
        super.onCreate()
        settings = Settings(this)
        DebugLog.enabled = settings.debugMode
        DebugLog.init(this)
        installCrashLogger()
        // 玻璃外观的持久化值要在任何界面绘制之前装进内存态
        GlassTuning.refraction = settings.glassRefraction
        GlassTuning.blur = settings.glassBlur
        engine = RecognitionEngine(this).also {
            it.loadTemplates()
            it.threshold = settings.threshold
        }
        DebugLog.log(
            "app",
            "应用启动：模板 ${engine.loadedTemplateCount} 个（比分 ${engine.loadedScoreTemplateCount} 个），" +
                "阈值 ${"%.2f".format(engine.threshold)}，调试模式=${settings.debugMode}"
        )
        maybeCheckUpdates()
    }

    /** 启动时后台看一眼有没有新版本（桌面端也是每次启动自动检查）。失败静默，只写日志。 */
    private fun maybeCheckUpdates() {
        if (!settings.autoCheckUpdates) return
        checkUpdate(this) { message ->
            DebugLog.log("update", "启动检查更新：$message")
            if (message.startsWith("发现新版本")) StatusHub.setNotice(message)
        }
    }

    /**
     * 把未捕获异常的栈写进日志文件，然后交回系统默认处理器。
     *
     * 没有这一步，实机上的"点一下就闪退"只能靠猜 —— 崩溃前最后一条日志只告诉我们崩在哪个区间，
     * 拿不到类型和行号。写日志本身也用 try 包住，免得崩溃处理器自己再崩。
     */
    private fun installCrashLogger() {
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, throwable ->
            try {
                DebugLog.log(
                    "crash",
                    "未捕获异常（线程 ${thread.name}）：${throwable.javaClass.name}: ${throwable.message}"
                )
                DebugLog.log("crash", throwable.stackTraceToString())
            } catch (_: Throwable) {
                // 崩溃处理器不能再抛
            }
            previous?.uncaughtException(thread, throwable)
        }
    }
}
