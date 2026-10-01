namespace KanamiReporter.Core;

public sealed class ReporterStateMachine
{
    private const double StateSwitchHysteresis = 0.03;
    private static readonly TimeSpan DuplicateRoundStartWindow = TimeSpan.FromSeconds(10);
    private const int RoundIngameDurationSeconds = 115;
    private const int BombPlantedDurationSeconds = 45;

    /// <summary>两侧阵营标签同时命中时，需要领先这个分数才认为阵营确实是这一侧。</summary>
    private const double SideScoreMargin = 0.03;

    private const string EventBomberDown = "event_bomber_down";
    private const string EventAlivePlayersUs1 = "event_alive_players_us_1";
    private const string EventAlivePlayersEnemy1 = "event_alive_players_enemy_1";
    private const string EventRoundIngameLast40s = "event_round_ingame_last_40s";
    private const string EventRoundIngameLast20s = "event_round_ingame_last_20s";
    private const string EventRoundStartLast5s = "event_round_start_last_5s";
    private const string EventGameStartAttacker = "event_game_start_attacker";
    private const string EventGameStartDefender = "event_game_start_defender";
    private const string EventRoundIngameAttacker = "event_round_ingame_attacker";
    private const string EventRoundIngameDefender = "event_round_ingame_defender";
    private const string EventLastRoundOfFirstHalf = "event_last_round_of_first_half";
    private const string EventDecideRound = "event_decide_round";
    private const string EventOvertimeRound = "event_overtime_round";
    private const string EventGameStartSecondHalf = "event_game_start_second_half";
    private const string EventVictoryInLast3s = "event_victory_in_last_3s";
    private const string EventRoundEndAttacker = "event_round_end_attacker";
    private const string EventRoundEndDefender = "event_round_end_defender";
    private const string EventRoundEndDefeat = "event_round_end_defeat";
    private const string EventRoundWinAllAlive = "event_round_win_all_alive";
    private const string EventRoundEndFiveKill = "event_round_end_five_kill";
    private const string EventGameEndWin = "event_game_end_win";
    private const string EventGameEndLose = "event_game_end_lose";
    private const string EventGameEndDraw = "event_game_end_draw";
    private const string EventGameChooseCharacter = "event_game_choose_character";
    private const string EventGameSwitchSide = "event_game_switch_side";
    private const string EventRoundIngameBombPlanted = "event_round_ingame_bomb_planted";

    private StateId? _currentStateId;
    private string _currentState = "未知";
    private TimeSpan _stateEnteredAt;
    private TimeSpan _currentStateTime;
    private int _currentRound;
    private TimeSpan? _lastRoundStartEnteredAt;
    private int _currentSide;
    private bool _bomberDownTriggered;
    private bool _alivePlayersUs1Triggered;
    private bool _alivePlayersEnemy1Triggered;
    private bool _roundLast40sTriggered;
    private bool _roundLast20sTriggered;
    private bool _startLast5sTriggered;

    public event Action<string>? EventTriggered;

    public string CurrentState => _currentState;
    public StateId? CurrentStateId => _currentStateId;
    public TimeSpan CurrentStateDuration => _currentStateTime;

    public void Reset()
    {
        _currentState = "未知";
        _currentStateId = null;
        _stateEnteredAt = TimeSpan.Zero;
        _currentStateTime = TimeSpan.Zero;
        _currentRound = 0;
        _currentSide = 0;
        ResetRoundTriggers();
    }

    public DetectionResult ProcessFrame(
        TimeSpan now,
        IReadOnlyList<bool> matches,
        IReadOnlyList<double> scores,
        SideSignal? side = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(scores);
        if (matches.Count != ReporterStates.Count || scores.Count != ReporterStates.Count)
        {
            throw new ArgumentException("状态判断结果数量不正确。");
        }

        if (side is { } sideSignal)
        {
            UpdateSide(sideSignal);
        }

        _currentStateTime = _currentStateId is not null && now >= _stateEnteredAt
            ? now - _stateEnteredAt
            : TimeSpan.Zero;

        const bool isBomberAlive = true;
        const int alivePlayersUs = 2;
        const int alivePlayersEnemy = 2;
        const int gameScoreUs = 3;
        const int gameScoreEnemy = 3;
        const int gameScoreTarget = 10;

        Trigger(EventBomberDown, !isBomberAlive && !_bomberDownTriggered, () => _bomberDownTriggered = true);
        Trigger(EventAlivePlayersUs1, alivePlayersUs == 1 && !_alivePlayersUs1Triggered, () => _alivePlayersUs1Triggered = true);
        Trigger(EventAlivePlayersEnemy1, alivePlayersEnemy == 1 && !_alivePlayersEnemy1Triggered, () => _alivePlayersEnemy1Triggered = true);

        if (_currentStateId == StateId.RoundStart && !_startLast5sTriggered)
        {
            if ((_currentRound == 1 && _currentStateTime >= TimeSpan.FromSeconds(ReporterStates.FirstRoundStartCountdownSeconds)) ||
                (_currentRound == 11 && _currentStateTime >= TimeSpan.FromSeconds(ReporterStates.OvertimeRoundStartCountdownSeconds)) ||
                (_currentRound != 1 && _currentRound != 11 && _currentStateTime >= TimeSpan.FromSeconds(ReporterStates.RegularRoundStartCountdownSeconds)))
            {
                Trigger(EventRoundStartLast5s, true, () => _startLast5sTriggered = true);
            }
        }

        if (_currentStateId == StateId.RoundIngame && _currentStateTime >= TimeSpan.FromSeconds(75) && !_roundLast40sTriggered)
        {
            Trigger(EventRoundIngameLast40s, true, () => _roundLast40sTriggered = true);
        }

        if (_currentStateId == StateId.RoundIngame && _currentStateTime >= TimeSpan.FromSeconds(95) && !_roundLast20sTriggered)
        {
            Trigger(EventRoundIngameLast20s, true, () => _roundLast20sTriggered = true);
        }

        var lastStateId = _currentStateId;
        var durationBeforeTransition = _currentStateTime;
        var bestMatchingIndex = -1;
        var bestMatchingScore = -1.0;

        for (var i = 0; i < ReporterStates.Count; i++)
        {
            if (!matches[i] || scores[i] <= bestMatchingScore)
            {
                continue;
            }

            bestMatchingIndex = i;
            bestMatchingScore = scores[i];
        }

        if (bestMatchingIndex >= 0)
        {
            var detectedStateId = (StateId)bestMatchingIndex;
            var currentIndex = (int?)_currentStateId;
            var currentStillMatches = currentIndex is not null && matches[currentIndex.Value];
            var currentScore = currentIndex is null ? -1.0 : scores[currentIndex.Value];

            if (_currentStateId != detectedStateId &&
                (!currentStillMatches || bestMatchingScore >= currentScore + StateSwitchHysteresis))
            {
                _currentStateId = detectedStateId;
                _currentState = ReporterStates.GetName(detectedStateId);
                TriggerForState(detectedStateId, lastStateId, durationBeforeTransition, now, gameScoreUs, gameScoreEnemy, gameScoreTarget);
                _stateEnteredAt = now;
                _currentStateTime = TimeSpan.Zero;
            }
        }

        var bestScore = _currentStateId is not null
            ? scores[(int)_currentStateId.Value]
            : bestMatchingScore;

        var timing = GetPhaseTiming(_currentStateId, _currentStateTime, _currentRound);
        return new DetectionResult(
            _currentStateId,
            _currentState,
            bestScore,
            _currentStateTime,
            scores,
            _currentRound,
            _currentSide,
            timing.Remaining,
            timing.Label);
    }

    /// <summary>
    /// 用购买阶段横幅上的「攻方 / 守方」标签校正阵营。
    /// 只有一侧明显命中才改阵营，标签淡入淡出或两侧接近时保持原值。
    /// </summary>
    private void UpdateSide(SideSignal signal)
    {
        var attacker = signal.AttackerHit && signal.AttackerScore + SideScoreMargin >= signal.DefenderScore;
        var defender = signal.DefenderHit && signal.DefenderScore + SideScoreMargin >= signal.AttackerScore;
        if (attacker == defender)
        {
            return;
        }

        _currentSide = attacker ? 1 : 2;
    }

    private void TriggerForState(
        StateId stateId,
        StateId? lastStateId,
        TimeSpan durationBeforeTransition,
        TimeSpan now,
        int gameScoreUs,
        int gameScoreEnemy,
        int gameScoreTarget)
    {
        switch (stateId)
        {
            case StateId.GameStartAttacker:
                Trigger(EventGameStartAttacker);
                _currentSide = 1;
                break;
            case StateId.GameStartDefender:
                Trigger(EventGameStartDefender);
                _currentSide = 2;
                break;
            case StateId.RoundStart:
                if (_lastRoundStartEnteredAt is { } lastRoundStart &&
                    now >= lastRoundStart &&
                    now - lastRoundStart < DuplicateRoundStartWindow)
                {
                    break;
                }

                _lastRoundStartEnteredAt = now;
                var currentRoundNumber = _currentRound + 1;
                if (currentRoundNumber == 9)
                {
                    Trigger(EventLastRoundOfFirstHalf);
                }
                else if (gameScoreUs + 1 == gameScoreTarget || gameScoreEnemy + 1 == gameScoreTarget)
                {
                    Trigger(EventDecideRound);
                }
                else if ((gameScoreUs + 2 == gameScoreTarget || gameScoreEnemy + 2 == gameScoreTarget) && gameScoreTarget > 10)
                {
                    Trigger(EventOvertimeRound);
                }
                else if (currentRoundNumber == 10)
                {
                    Trigger(EventGameStartSecondHalf);
                }

                _currentRound = currentRoundNumber;
                break;
            case StateId.RoundEndWin:
            case StateId.RoundEndLose:
                // 语音播报围绕玩家自己的回合结果：赢了才播胜利语音，战败一律播战败语音。
                if (stateId == StateId.RoundEndWin)
                {
                    if (lastStateId == StateId.RoundIngame && durationBeforeTransition > TimeSpan.FromSeconds(112))
                    {
                        Trigger(EventVictoryInLast3s);
                    }
                    else if (lastStateId == StateId.RoundIngameBombPlanted &&
                             durationBeforeTransition > TimeSpan.FromSeconds(42) &&
                             _currentSide == 2)
                    {
                        Trigger(EventVictoryInLast3s);
                    }
                    else if (_currentSide == 1)
                    {
                        Trigger(EventRoundEndAttacker);
                    }
                    else if (_currentSide == 2)
                    {
                        Trigger(EventRoundEndDefender);
                    }
                }
                else
                {
                    Trigger(EventRoundEndDefeat);
                }

                ResetRoundTriggers();
                break;
            case StateId.RoundWinAllAlive:
                Trigger(EventRoundWinAllAlive);
                ResetRoundTriggers();
                break;
            case StateId.RoundWinFiveKill:
                Trigger(EventRoundEndFiveKill);
                ResetRoundTriggers();
                break;
            case StateId.RoundWinAlive1:
                if (_currentSide == 1)
                {
                    Trigger(EventRoundEndAttacker);
                }
                else if (_currentSide == 2)
                {
                    Trigger(EventRoundEndDefender);
                }

                ResetRoundTriggers();
                break;
            case StateId.GameEndWin:
                Trigger(EventGameEndWin);
                ResetRoundTriggers();
                break;
            case StateId.GameEndLose:
                Trigger(EventGameEndLose);
                ResetRoundTriggers();
                break;
            case StateId.GameEndDraw:
                Trigger(EventGameEndDraw);
                ResetRoundTriggers();
                break;
            case StateId.GameChooseCharacter:
                Trigger(EventGameChooseCharacter);
                break;
            case StateId.GameSwitchSide:
                Trigger(EventGameSwitchSide);
                break;
            case StateId.RoundIngame:
                if (_currentSide == 1)
                {
                    Trigger(EventRoundIngameAttacker);
                }
                else if (_currentSide == 2)
                {
                    Trigger(EventRoundIngameDefender);
                }
                break;
            case StateId.RoundIngameBombPlanted:
                Trigger(EventRoundIngameBombPlanted);
                break;
            default:
                Trigger(ReporterStates.GetName(stateId));
                break;
        }
    }

    private void Trigger(string eventId, bool condition, Action markTriggered)
    {
        if (!condition)
        {
            return;
        }

        Trigger(eventId);
        markTriggered();
    }

    private void Trigger(string eventId)
    {
        if (ReporterStates.UnsupportedDynamicEvents.Contains(eventId))
        {
            return;
        }

        EventTriggered?.Invoke(eventId);
    }

    private static (TimeSpan? Remaining, string Label) GetPhaseTiming(
        StateId? stateId,
        TimeSpan stateDuration,
        int roundNumber)
    {
        var totalSeconds = stateId switch
        {
            StateId.RoundStart => ReporterStates.GetRoundStartCountdownSeconds(roundNumber),
            StateId.RoundIngame => RoundIngameDurationSeconds,
            StateId.RoundIngameBombPlanted => BombPlantedDurationSeconds,
            _ => 0
        };

        if (totalSeconds == 0)
        {
            return (null, string.Empty);
        }

        var remaining = TimeSpan.FromSeconds(Math.Max(0, totalSeconds - stateDuration.TotalSeconds));
        var label = stateId switch
        {
            StateId.RoundStart => "准备阶段估算",
            StateId.RoundIngame => "战斗阶段估算",
            StateId.RoundIngameBombPlanted => "安装后估算",
            _ => string.Empty
        };
        return (remaining, label);
    }
    private void ResetRoundTriggers()
    {
        _bomberDownTriggered = false;
        _alivePlayersUs1Triggered = false;
        _alivePlayersEnemy1Triggered = false;
        _roundLast40sTriggered = false;
        _roundLast20sTriggered = false;
        _startLast5sTriggered = false;
    }
}







