package com.kanami.reporter

import android.app.Application
import com.kanami.reporter.core.RecognitionEngine

class KanamiApp : Application() {

    lateinit var engine: RecognitionEngine
        private set

    override fun onCreate() {
        super.onCreate()
        engine = RecognitionEngine(this).also { it.loadTemplates() }
    }
}
