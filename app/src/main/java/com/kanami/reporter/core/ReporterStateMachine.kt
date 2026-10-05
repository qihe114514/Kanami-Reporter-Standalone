package com.kanami.reporter.core

/**
 * 手机版状态机 —— 移植自 Kanami-Reporter-Standalone 的 ReporterStateMachine，
 * 并按手机端 HUD 做了适配（详见 Standalone 仓库 docs/mobile-adaptation.md 第 4 节）：
 *
 * 1. 状态判定用优先级而非纯分数竞争：
 *    回合结束横幅 > 购买横幅 > 炸弹面板 > 开局计时数字。
 *    （炸弹面板在回合刚结束时残留片刻，回合结束横幅才是更"新"的状态。）
 * 2. 手机端没有独立的开局/换边界面：开局语音在首个购买阶段 + 已知阵营时触发；
 *    「攻守互换」由购买横幅副标题的阵营翻转触发（每回合校正，仅已知阵营后翻转才播）。
 * 3. 购买菜单全屏打开会盖住顶栏，所有模板不命中——状态机保持当前状态。
 */
class ReporterStateMachine {

    companion object {
        const val StateSwitchHysteresis = 0.03
        const val DuplicateRoundStartWindowMs = 10_000L
        const val RoundIngameDurationSeconds = 115
        const val BombPlantedDurationSeconds = 45
        const val RoundIngameWarningSeconds40 = 40
        const val RoundIngameWarningSeconds20 = 20
        const val SideScoreMargin = 0.03

        // 回合准备阶段时长（秒）：沿用 PC 端实测值。
        // 手机端竞技爆破为「先胜 7 回合」赛制：6 回合上半场 + 6 回合下半场（录屏实证：
        // 副标题阵营在第 7 回合购买阶段翻转，整局 11 回合 7:4 结束），第 13 回合起加时。
        const val FirstRoundStartCountdownSeconds = 36
        const val RegularRoundStartCountdownSeconds = 22
        const val SecondHalfFirstRoundStartCountdownSeconds = 49
        const val OvertimeRoundStartCountdownSeconds = 37

        const val FirstHalfRounds = 6

        fun roundStartCountdownSeconds(roundNumber: Int): Int = when (roundNumber) {
            1 -> FirstRoundStartCountdownSeconds
            FirstHalfRounds + 1 -> SecondHalfFirstRoundStartCountdownSeconds
            FirstHalfRounds * 2 + 1 -> OvertimeRoundStartCountdownSeconds
            else -> RegularRoundStartCountdownSeconds
        }
    }

    var currentStateId: StateId? = null
        private set
    var currentStateTimeMs: Long = 0
        private set
    var currentRound: Int = 0
        private set
    /** 0=未知 1=攻方 2=守方 */
    var currentSide: Int = 0
        private set

    /** 估算比分：按回合结算累计（回合获胜 = 我方 +1，回合失败 = 对方 +1）。 */
    var estimatedScoreUs: Int = 0
        private set
    var estimatedScoreEnemy: Int = 0
        private set

    /** 最近一次触发的事件（供界面/悬浮窗显示"当前触发的事件"）。 */
    var lastEventId: String? = null
        private set
    var lastEventAtMs: Long = 0
        private set

    private var stateEnteredAtMs = 0L
    private var lastRoundStartEnteredAtMs: Long? = null
    private var gameStartAnnounced = false
    private var lastFrameAtMs = 0L
    private var lastScoredRound = -1

    private var roundLast40sTriggered = false
    private var roundLast20sTriggered = false
    private var startLast5sTriggered = false

    var eventTriggered: ((String) -> Unit)? = null

    fun reset() {
        currentStateId = null
        currentStateTimeMs = 0
        currentRound = 0
        currentSide = 0
        stateEnteredAtMs = 0
        lastRoundStartEnteredAtMs = null
        gameStartAnnounced = false
        resetRoundTriggers()
    }

    /** 当前阶段的估算剩余秒数（无阶段时长可估算时为 null）。 */
    fun estimatedRemainingSeconds(): Int? {
        val state = currentStateId ?: return null
        val elapsed = (currentStateTimeMs / 1000L).toInt()
        val total = when (state) {
            StateId.RoundStart -> roundStartCountdownSeconds(currentRound)
            StateId.RoundIngame -> RoundIngameDurationSeconds
            StateId.RoundIngameBombPlanted -> BombPlantedDurationSeconds
            else -> return null
        }
        return (total - elapsed).coerceAtLeast(0)
    }

    class SideSignal(
        val attackerHit: Boolean,
        val defenderHit: Boolean,
        val attackerScore: Double,
        val defenderScore: Double
    )

    /**
     * @param matches 15 个状态的阈值判定结果
     * @param scores  15 个状态的最高 ZNCC 分
     */
    fun processFrame(nowMs: Long, matches: BooleanArray, scores: DoubleArray, side: SideSignal?) {
        lastFrameAtMs = nowMs
        if (side != null) updateSide(side)

        currentStateTimeMs = if (currentStateId != null && nowMs >= stateEnteredAtMs) {
            nowMs - stateEnteredAtMs
        } else 0

        if (currentStateId == StateId.RoundStart && !startLast5sTriggered) {
            if (currentStateTimeMs >= roundStartCountdownSeconds(currentRound) * 1000L) {
                trigger("event_round_start_last_5s")
                startLast5sTriggered = true
            }
        }

        if (currentStateId == StateId.RoundIngame &&
            currentStateTimeMs >= (RoundIngameDurationSeconds - RoundIngameWarningSeconds40) * 1000L &&
            !roundLast40sTriggered
        ) {
            trigger("event_round_ingame_last_40s")
            roundLast40sTriggered = true
        }
        if (currentStateId == StateId.RoundIngame &&
            currentStateTimeMs >= (RoundIngameDurationSeconds - RoundIngameWarningSeconds20) * 1000L &&
            !roundLast20sTriggered
        ) {
            trigger("event_round_ingame_last_20s")
            roundLast20sTriggered = true
        }

        val detected = detectState(matches, scores)
        if (detected != null && detected != currentStateId) {
            val previous = currentStateId
            val durationBefore = currentStateTimeMs
            currentStateId = detected
            stateEnteredAtMs = nowMs
            currentStateTimeMs = 0
            onStateEntered(detected, previous, durationBefore)
        }
    }

    /**
     * 手机版状态选择：优先级判定。同优先级内取分数更高者；
     * 保留 PC 端的滞回逻辑（当前状态仍命中时，需要领先 0.03 才切换）。
     */
    private fun detectState(matches: BooleanArray, scores: DoubleArray): StateId? {
        // 优先级组（取组内最高分）：
        val terminal = intArrayOf(StateId.RoundEndWin.id, StateId.RoundEndLose.id, StateId.GameEndWin.id, StateId.GameEndLose.id, StateId.GameEndDraw.id)
        val buy = intArrayOf(StateId.RoundStart.id, StateId.GameChooseCharacter.id)
        val planted = intArrayOf(StateId.RoundIngameBombPlanted.id)
        val ingame = intArrayOf(StateId.RoundIngame.id)

        fun bestOf(ids: IntArray): Int {
            var best = -1
            var bestScore = -1.0
            for (i in ids) {
                if (matches[i] && scores[i] > bestScore) {
                    best = i; bestScore = scores[i]
                }
            }
            return best
        }

        val currentIdx = currentStateId?.id
        fun consider(candidate: Int): StateId? {
            if (candidate < 0) return null
            val candidateState = StateId.from(candidate) ?: return null
            if (candidateState == currentStateId) return null
            val currentStillMatches = currentIdx != null && matches[currentIdx]
            val currentScore = if (currentIdx != null) scores[currentIdx] else -1.0
            return if (!currentStillMatches || scores[candidate] >= currentScore + StateSwitchHysteresis) {
                candidateState
            } else null
        }

        return consider(bestOf(terminal))
            ?: consider(bestOf(buy))
            ?: consider(bestOf(planted))
            ?: consider(bestOf(ingame))
    }

    private fun onStateEntered(stateId: StateId, lastStateId: StateId?, durationBeforeTransitionMs: Long) {
        when (stateId) {
            StateId.GameChooseCharacter -> {
                trigger("event_game_choose_character")
                resetMatch()
            }
            StateId.RoundStart -> {
                val now = stateEnteredAtMs
                val last = lastRoundStartEnteredAtMs
                if (last == null || now - last >= DuplicateRoundStartWindowMs) {
                    lastRoundStartEnteredAtMs = now
                    currentRound += 1
                    if (!gameStartAnnounced) {
                        gameStartAnnounced = true
                        trigger(
                            if (currentSide == 2) "event_game_start_defender" else "event_game_start_attacker"
                        )
                    }
                    if (currentRound == FirstHalfRounds) {
                        trigger("event_last_round_of_first_half")
                    } else if (currentRound == FirstHalfRounds + 1) {
                        trigger("event_game_start_second_half")
                    }
                }
            }
            StateId.RoundEndWin -> {
                if (lastStateId == StateId.RoundIngame &&
                    durationBeforeTransitionMs > 112_000L
                ) {
                    trigger("event_victory_in_last_3s")
                } else if (lastStateId == StateId.RoundIngameBombPlanted &&
                    durationBeforeTransitionMs > 42_000L && currentSide == 2
                ) {
                    trigger("event_victory_in_last_3s")
                } else if (currentSide == 1) {
                    trigger("event_round_end_attacker")
                } else if (currentSide == 2) {
                    trigger("event_round_end_defender")
                } else {
                    trigger("event_round_end_attacker")
                }
                countRoundForScore(won = true)
                resetRoundTriggers()
            }
            StateId.RoundEndLose -> {
                trigger("event_round_end_defeat")
                countRoundForScore(won = false)
                resetRoundTriggers()
            }
            StateId.GameEndWin -> {
                trigger("event_game_end_win")
                resetRoundTriggers()
                resetMatch()
            }
            StateId.GameEndLose -> {
                trigger("event_game_end_lose")
                resetRoundTriggers()
                resetMatch()
            }
            StateId.GameEndDraw -> {
                trigger("event_game_end_draw")
                resetRoundTriggers()
                resetMatch()
            }
            StateId.RoundIngame -> {
                when (currentSide) {
                    1 -> trigger("event_round_ingame_attacker")
                    2 -> trigger("event_round_ingame_defender")
                    else -> trigger("event_round_ingame_attacker")
                }
            }
            StateId.RoundIngameBombPlanted -> trigger("event_round_ingame_bomb_planted")
            else -> {}
        }
    }

    private fun updateSide(signal: SideSignal) {
        val attacker = signal.attackerHit && signal.attackerScore + SideScoreMargin >= signal.defenderScore
        val defender = signal.defenderHit && signal.defenderScore + SideScoreMargin >= signal.attackerScore
        if (attacker == defender) return
        val side = if (attacker) 1 else 2
        if (currentSide == side) return
        val hadKnownSide = currentSide != 0
        currentSide = side
        if (hadKnownSide) trigger("event_game_switch_side")
    }

    private fun resetRoundTriggers() {
        roundLast40sTriggered = false
        roundLast20sTriggered = false
        startLast5sTriggered = false
    }

    private fun resetMatch() {
        currentRound = 0
        lastRoundStartEnteredAtMs = null
        gameStartAnnounced = false
        estimatedScoreUs = 0
        estimatedScoreEnemy = 0
        lastScoredRound = -1
    }

    /**
     * 估算比分：回合获胜/失败各记一次。同一回合重复进入结算横幅只记一次
     * （靠 currentRound 去重；若中途漏掉购买横幅，该回合可能不计入，属估算的已知限制）。
     */
    private fun countRoundForScore(won: Boolean) {
        if (currentRound == lastScoredRound) return
        lastScoredRound = currentRound
        if (won) estimatedScoreUs++ else estimatedScoreEnemy++
    }

    private fun trigger(eventId: String) {
        lastEventId = eventId
        lastEventAtMs = lastFrameAtMs
        eventTriggered?.invoke(eventId)
    }
}

/** 事件 → 语音文件（与 PC 端 VoiceTable 一致，assets 内文件名）。 */
object VoiceTable {
    class VoiceDefinition(val eventId: String, val fileNames: List<String>)

    val entries: List<VoiceDefinition> = listOf(
        VoiceDefinition("event_round_ingame_last_40s", listOf("距离战斗结束还剩40秒。.mp3")),
        VoiceDefinition("event_round_ingame_last_20s", listOf("只剩20秒咯。.mp3")),
        VoiceDefinition("event_round_start_last_5s", listOf("倒计时！五、四、三、二、一，GO！.mp3")),
        VoiceDefinition("event_game_start_attacker", listOf("演出马上就要开始了，各位，准备好了吗？.mp3")),
        VoiceDefinition("event_game_start_defender", listOf("演出马上就要开始了，各位，准备好了吗？.mp3")),
        VoiceDefinition("event_round_ingame_attacker", listOf("保护好炸弹携带者，登上未知的舞台吧！.mp3")),
        VoiceDefinition("event_round_ingame_defender", listOf("各位，要守护好阵地哦。.mp3")),
        VoiceDefinition("event_last_round_of_first_half", listOf("上半场最后的表演了，稍微调整状态，继续前进吧。.mp3")),
        VoiceDefinition("event_game_start_second_half", listOf("后半场的第一首歌可是很重要的。.mp3")),
        VoiceDefinition("event_victory_in_last_3s", listOf("好，好险啊，香奈美可一直为你提心吊胆呢！.mp3")),
        VoiceDefinition("event_round_end_attacker", listOf("赢了！只要聚集起大家的力量，就一定能改变很多事情。.mp3")),
        VoiceDefinition("event_round_end_defender", listOf("各位成功的守护了很多东西呢。.mp3")),
        VoiceDefinition("event_round_end_defeat", listOf("即使面对这样的局面，也战到了最后。.mp3")),
        VoiceDefinition("event_game_end_win", listOf("演出完美谢幕，大家辛苦了！.mp3")),
        VoiceDefinition("event_game_end_lose", listOf("聚光灯关上了，表演也结束了。.mp3")),
        VoiceDefinition("event_game_end_draw", listOf("真是势均力敌的对局，就连香奈美也开始心潮澎湃了！.mp3")),
        VoiceDefinition("event_game_choose_character", listOf("选择角色，朝向舞台的最高峰发起进攻吧！.mp3")),
        VoiceDefinition("event_game_switch_side", listOf("攻守互换.mp3")),
        VoiceDefinition("event_round_ingame_bomb_planted", listOf("炸弹安装完毕，即将为公演增添最璀璨的色彩。.mp3")),
    )

    fun fileNamesFor(eventId: String): List<String> =
        entries.firstOrNull { it.eventId == eventId }?.fileNames ?: emptyList()

    /** 事件的中文短标签（界面与悬浮窗显示"当前触发的事件"用）。 */
    val DisplayNames: Map<String, String> = mapOf(
        "event_round_ingame_last_40s" to "剩余 40 秒",
        "event_round_ingame_last_20s" to "剩余 20 秒",
        "event_round_start_last_5s" to "开局倒计时 5 秒",
        "event_game_start_attacker" to "比赛开始 · 攻方",
        "event_game_start_defender" to "比赛开始 · 守方",
        "event_round_ingame_attacker" to "回合进行中 · 攻方",
        "event_round_ingame_defender" to "回合进行中 · 守方",
        "event_last_round_of_first_half" to "上半场最后一回合",
        "event_game_start_second_half" to "下半场开始",
        "event_victory_in_last_3s" to "最后 3 秒险胜",
        "event_round_end_attacker" to "回合获胜 · 攻方",
        "event_round_end_defender" to "回合获胜 · 守方",
        "event_round_end_defeat" to "回合失败",
        "event_game_end_win" to "对局胜利",
        "event_game_end_lose" to "对局失败",
        "event_game_end_draw" to "对局平局",
        "event_game_choose_character" to "选择角色",
        "event_game_switch_side" to "攻守互换",
        "event_round_ingame_bomb_planted" to "炸弹已安装"
    )

    fun displayName(eventId: String): String = DisplayNames[eventId] ?: eventId
}
