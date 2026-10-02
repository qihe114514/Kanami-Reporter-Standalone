namespace KanamiReporter.Core;

public enum StateId
{
    GameChooseCharacter = 0,
    GameStartAttacker = 1,
    GameStartDefender = 2,
    GameSwitchSide = 3,
    RoundStart = 4,
    RoundIngame = 5,
    RoundIngameBombPlanted = 6,
    RoundEndWin = 7,
    RoundEndLose = 8,
    RoundWinFiveKill = 9,
    RoundWinAllAlive = 10,
    RoundWinAlive1 = 11,
    GameEndWin = 12,
    GameEndLose = 13,
    GameEndDraw = 14
}

public sealed record VoiceDefinition(string EventId, IReadOnlyList<string> FileNames);

public static class ReporterStates
{
    public const int Count = 15;
    public const int FrameWidth = 1920;
    public const int FrameHeight = 1080;
    public const int FrameBytes = FrameWidth * FrameHeight * 4;
    public const int MaxTemplatePixels = 262144;
    public const double DefaultThreshold = 0.90;
    public const int CaptureIntervalMilliseconds = 100;

    /// <summary>
    /// 「回合开始最后5秒」语音的触发时刻：从进入购买/准备阶段算起，对应回合正式开始前的最后 5 秒。
    /// 第一回合的准备阶段比常规回合更长，所以触发点更靠后。
    /// </summary>
    public const int FirstRoundStartCountdownSeconds = 31;

    public const int RegularRoundStartCountdownSeconds = 22;
    public const int OvertimeRoundStartCountdownSeconds = 37;

    public static readonly string[] Names =
    [
        "game_choose_character",
        "game_start_attacker",
        "game_start_defender",
        "game_switch_side",
        "round_start",
        "round_ingame",
        "round_ingame_bomb_planted",
        "round_end_win",
        "round_end_lose",
        "round_win_five_kill",
        "round_win_all_alive",
        "round_win_alive_1",
        "game_end_win",
        "game_end_lose",
        "game_end_draw"
    ];

    /// <summary>
    /// 阵营辅助模板的文件名（不含扩展名）：购买阶段横幅上的「攻方 / 守方」标签。
    /// 它们不出现在状态列表里，只用于每个回合校正攻守阵营。
    /// </summary>
    public const string AttackerSideTemplateName = "side_attacker";

    public const string DefenderSideTemplateName = "side_defender";

    /// <summary>阵营辅助模板的展示名，用于诊断信息。</summary>
    public static string GetSideTemplateDisplayName(bool attacker) => attacker ? "阵营标记：攻方" : "阵营标记：守方";

    public static readonly string[] DisplayNames =
    [
        "选择角色",
        "进攻方开局",
        "防守方开局",
        "攻守互换",
        "回合开始",
        "回合进行中",
        "炸弹已安装",
        "回合胜利",
        "回合失败",
        "五杀胜利",
        "全员存活胜利",
        "最后一人胜利",
        "对局胜利",
        "对局失败",
        "对局平局"
    ];

    public static readonly IReadOnlyDictionary<string, string> EventDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["event_bomber_down"] = "炸弹携带者被击倒",
        ["event_alive_players_us_1"] = "我方仅剩一人",
        ["event_alive_players_enemy_1"] = "敌方仅剩一人",
        ["event_round_ingame_last_40s"] = "回合剩余40秒",
        ["event_round_ingame_last_20s"] = "回合剩余20秒",
        ["event_round_start_last_5s"] = "回合开始最后5秒",
        ["event_game_start_attacker"] = "进攻方开局语音",
        ["event_game_start_defender"] = "防守方开局语音",
        ["event_round_ingame_attacker"] = "进攻方回合开始",
        ["event_round_ingame_defender"] = "防守方回合开始",
        ["event_last_round_of_first_half"] = "上半场最后一回合",
        ["event_decide_round"] = "决胜回合",
        ["event_overtime_round"] = "加时回合",
        ["event_game_start_second_half"] = "下半场开始",
        ["event_victory_in_last_3s"] = "最后3秒险胜",
        ["event_round_end_attacker"] = "进攻方回合胜利",
        ["event_round_end_defender"] = "防守方回合胜利",
        ["event_round_end_defeat"] = "回合战败",
        ["event_round_win_all_alive"] = "全员存活获胜",
        ["event_round_end_five_kill"] = "五杀结束回合",
        ["event_game_end_win"] = "对局胜利",
        ["event_game_end_lose"] = "对局失败",
        ["event_game_end_draw"] = "对局平局",
        ["event_game_choose_character"] = "选择角色",
        ["event_game_switch_side"] = "攻守互换",
        ["event_round_ingame"] = "回合进行中",
        ["event_round_ingame_bomb_planted"] = "炸弹安装完成"
    };

    public static readonly string[] Descriptions =
    [
        "识别角色选择界面，用于选人阶段播报。",
        "识别进攻方开局提示，并设置后续攻守阵营。",
        "识别防守方开局提示，并设置后续攻守阵营。",
        "识别攻守交换界面，用于半场切换播报。",
        "识别每回合购买/准备阶段，并用于倒计时、半场和决胜回合判断。",
        "识别战斗进行阶段，结合阵营播放进攻或防守语音。",
        "识别炸弹安装后的阶段，用于安装完成播报。",
        "识别本回合胜利结算：攻方胜利播「赢了」，守方胜利播「成功守护」，最后3秒的险胜有专属语音。",
        "识别本回合失败结算，播放战败安慰语音，不再误播胜利语音。",
        "识别五杀胜利结算。",
        "识别全员存活并获胜的结算界面。",
        "识别仅剩最后一人仍获胜的结算界面。",
        "识别整场对局胜利结算。",
        "识别整场对局失败结算。",
        "识别整场对局平局结算。"
    ];

    private static readonly IReadOnlyDictionary<StateId, IReadOnlyList<string>> TemplateVoiceEvents =
        new Dictionary<StateId, IReadOnlyList<string>>
        {
            [StateId.GameChooseCharacter] = ["event_game_choose_character"],
            [StateId.GameStartAttacker] = ["event_game_start_attacker"],
            [StateId.GameStartDefender] = ["event_game_start_defender"],
            [StateId.GameSwitchSide] = ["event_game_switch_side"],
            [StateId.RoundStart] =
            [
                "event_last_round_of_first_half",
                "event_decide_round",
                "event_overtime_round",
                "event_game_start_second_half",
                "event_round_start_last_5s"
            ],
            [StateId.RoundIngame] =
            [
                "event_round_ingame_attacker",
                "event_round_ingame_defender",
                "event_round_ingame_last_40s",
                "event_round_ingame_last_20s"
            ],
            [StateId.RoundIngameBombPlanted] = ["event_round_ingame_bomb_planted"],
            [StateId.RoundEndWin] =
            [
                "event_round_end_attacker",
                "event_round_end_defender",
                "event_victory_in_last_3s"
            ],
            [StateId.RoundEndLose] =
            [
                "event_round_end_defeat"
            ],
            [StateId.RoundWinFiveKill] = ["event_round_end_five_kill"],
            [StateId.RoundWinAllAlive] = ["event_round_win_all_alive"],
            [StateId.RoundWinAlive1] =
            [
                "event_round_end_attacker",
                "event_round_end_defender"
            ],
            [StateId.GameEndWin] = ["event_game_end_win"],
            [StateId.GameEndLose] = ["event_game_end_lose"],
            [StateId.GameEndDraw] = ["event_game_end_draw"]
        };

    public static readonly IReadOnlyList<VoiceDefinition> VoiceTable =
    [
        new("event_bomber_down", ["炸弹携带者被击倒了。.mp3"]),
        new("event_alive_players_us_1", ["这就是最后的希望了！.mp3"]),
        new("event_alive_players_enemy_1", ["对面的舞台上，只剩下独唱了呢。.mp3"]),
        new("event_round_ingame_last_40s", ["距离战斗结束还剩40秒。.mp3"]),
        new("event_round_ingame_last_20s", ["只剩20秒咯。.mp3"]),
        new("event_round_start_last_5s", ["倒计时！五、四、三、二、一，GO！.mp3"]),
        new("event_game_start_attacker", ["演出马上就要开始了，各位，准备好了吗？.mp3"]),
        new("event_game_start_defender", ["演出马上就要开始了，各位，准备好了吗？.mp3"]),
        new("event_round_ingame_attacker", ["保护好炸弹携带者，登上未知的舞台吧！.mp3"]),
        new("event_round_ingame_defender", ["各位，要守护好阵地哦。.mp3"]),
        new("event_last_round_of_first_half", ["上半场最后的表演了，稍微调整状态，继续前进吧。.mp3"]),
        new("event_decide_round", ["只差最后一步，就能走向充满光辉的未来了！.mp3", "现在是关键时刻哦.mp3"]),
        new("event_overtime_round", ["真是势均力敌的对局，就连香奈美也开始心潮澎湃了！.mp3"]),
        new("event_game_start_second_half", ["后半场的第一首歌可是很重要的。.mp3"]),
        new("event_victory_in_last_3s", ["好，好险啊，香奈美可一直为你提心吊胆呢！.mp3"]),
        new("event_round_end_attacker", ["赢了！只要聚集起大家的力量，就一定能改变很多事情。.mp3"]),
        new("event_round_end_defender", ["各位成功的守护了很多东西呢。.mp3"]),
        new("event_round_end_defeat", ["即使面对这样的局面，也战到了最后。.mp3"]),
        new("event_round_win_all_alive", ["全员留在舞台的身姿，是不是比任何时候都更加耀眼呢？.mp3"]),
        new("event_round_end_five_kill", ["哇！真是梦寐以求的完美表演呢！.mp3"]),
        new("event_game_end_win", ["演出完美谢幕，大家辛苦了！.mp3"]),
        new("event_game_end_lose", ["聚光灯关上了，表演也结束了。.mp3"]),
        new("event_game_end_draw", ["真是势均力敌的对局，就连香奈美也开始心潮澎湃了！.mp3"]),
        new("event_game_choose_character", ["选择角色，朝向舞台的最高峰发起进攻吧！.mp3", "选择角色，守护最珍贵的事物吧！.mp3"]),
        new("event_game_switch_side", ["攻守互换.mp3"]),
        new("event_round_ingame", []),
        new("event_round_ingame_bomb_planted", ["炸弹安装完毕，即将为公演增添最璀璨的色彩。.mp3"])
    ];

    public static readonly IReadOnlySet<string> UnsupportedDynamicEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "event_bomber_down",
        "event_alive_players_us_1",
        "event_alive_players_enemy_1",
        "event_decide_round",
        "event_overtime_round"
    };

    public static int GetRoundStartCountdownSeconds(int roundNumber) =>
        roundNumber switch
        {
            1 => FirstRoundStartCountdownSeconds,
            11 => OvertimeRoundStartCountdownSeconds,
            _ => RegularRoundStartCountdownSeconds
        };
    public static string GetName(StateId stateId) => Names[(int)stateId];

    public static string GetDisplayName(StateId stateId) => DisplayNames[(int)stateId];

    public static string GetDescription(StateId stateId) => Descriptions[(int)stateId];

    public static IReadOnlyList<string> GetVoiceEventIds(StateId stateId) =>
        TemplateVoiceEvents.TryGetValue(stateId, out var eventIds) ? eventIds : [];

    public static string GetEventDisplayName(string eventId) =>
        EventDisplayNames.TryGetValue(eventId, out var displayName) ? displayName : eventId;

    public static bool TryGetStateId(string name, out StateId stateId)
    {
        var index = Array.IndexOf(Names, name);
        if (index >= 0)
        {
            stateId = (StateId)index;
            return true;
        }

        stateId = default;
        return false;
    }
}
