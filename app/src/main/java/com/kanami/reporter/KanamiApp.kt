package com.kanami.reporter

import android.app.Application
import com.kanami.reporter.core.RecognitionEngine
import com.kanami.reporter.debug.DebugLog
import com.kanami.reporter.settings.Settings

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
        engine = RecognitionEngine(this).also {
            it.loadTemplates()
            it.threshold = settings.threshold
        }
        DebugLog.log(
            "app",
            "应用启动：模板 ${engine.loadedTemplateCount} 个（比分 ${engine.loadedScoreTemplateCount} 个），" +
                "阈值 ${"%.2f".format(engine.threshold)}，调试模式=${settings.debugMode}"
        )
    }
}
