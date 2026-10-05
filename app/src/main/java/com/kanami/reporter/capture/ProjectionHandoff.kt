package com.kanami.reporter.capture

import android.content.Intent

/**
 * 录屏授权结果在 Activity → 前台服务之间的交接。
 *
 * 为什么不用 Intent extra 传：Android 13 起 `Intent.getParcelableExtra(name)` 在部分
 * 系统版本上对 `Intent` 类型的 extra 会返回 null（Bundle 懒解析时拿不到正确的 ClassLoader），
 * 服务那边就成了"没有授权数据"→ 直接 stopSelf。用户看到的现象就是
 * 「点开始识别 → 选了整个屏幕 → 什么都没发生，也没有悬浮窗」。
 *
 * 两端在同一个进程里，直接交接引用最稳；Intent extra 只作为兜底保留。
 */
object ProjectionHandoff {

    private val lock = Any()
    private var resultCode = 0
    private var resultData: Intent? = null

    fun put(code: Int, data: Intent) {
        synchronized(lock) {
            resultCode = code
            resultData = data
        }
    }

    /** 取出并清空（只应被服务消费一次）。 */
    fun take(): Pair<Int, Intent>? = synchronized(lock) {
        val data = resultData ?: return null
        val code = resultCode
        resultData = null
        resultCode = 0
        code to data
    }
}
