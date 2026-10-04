using System.Text.Json;
using KanamiReporter.Core;

namespace KanamiReporter.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void StateNamesKeepStableOrder()
    {
        Assert.Equal(15, ReporterStates.Count);
        Assert.Equal("game_choose_character", ReporterStates.Names[0]);
        Assert.Equal("game_end_draw", ReporterStates.Names[^1]);
        Assert.Equal("round_ingame_bomb_planted", ReporterStates.GetName(StateId.RoundIngameBombPlanted));
    }

    [Fact]
    public void UserFacingStateAndVoiceNamesAreChinese()
    {
        foreach (var stateId in Enum.GetValues<StateId>())
        {
            Assert.False(ContainsAsciiLetter(ReporterStates.GetDisplayName(stateId)));
        }

        foreach (var voice in ReporterStates.VoiceTable)
        {
            Assert.False(ContainsAsciiLetter(ReporterStates.GetEventDisplayName(voice.EventId)));
        }

        foreach (var stateId in Enum.GetValues<StateId>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReporterStates.GetDescription(stateId)));
            var eventIds = ReporterStates.GetVoiceEventIds(stateId);
            Assert.NotEmpty(eventIds);
            Assert.All(eventIds, eventId => Assert.Contains(
                ReporterStates.VoiceTable,
                voice => voice.EventId == eventId));
        }
    }

    [Fact]
    public void KrtRoundTripPreservesRoiAndPixels()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "round_start.krt");
        var roi = new Roi(100, 80, 64, 32);
        var pixels = Enumerable.Range(0, roi.Width * roi.Height)
            .Select(i => (byte)((i * 37 + i / 3) % 256))
            .ToArray();

        KrtTemplateStore.Save(path, new TemplateModel { Roi = roi, Pixels = pixels });
        var loaded = KrtTemplateStore.Load(path);

        Assert.Equal(roi, loaded.Roi);
        Assert.Equal(pixels, loaded.Pixels);
        Assert.True(loaded.Energy > 0);
    }

    [Fact]
    public void KrtRejectsUniformTemplate()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "uniform.krt");
        var roi = new Roi(0, 0, 32, 32);
        var pixels = Enumerable.Repeat((byte)127, roi.Width * roi.Height).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            KrtTemplateStore.Save(path, new TemplateModel { Roi = roi, Pixels = pixels }));
    }

    [Fact]
    public void GrayscaleUsesOriginalBgraWeights()
    {
        byte[] bgra = [10, 20, 30, 255];
        var gray = FrameProcessing.ToGrayscale(bgra, 1, 1);
        Assert.Equal((byte)((29U * 10 + 150U * 20 + 77U * 30) >> 8), gray[0]);
    }

    [Fact]
    public void ExactTemplateProducesScoreOne()
    {
        var directory = CreateTempDirectory();
        var roi = new Roi(10, 10, 64, 64);
        var gray = CreateGrayFrame();
        var pixels = Extract(gray, roi);
        var path = Path.Combine(directory, "match.krt");
        KrtTemplateStore.Save(path, new TemplateModel { Roi = roi, Pixels = pixels });
        var model = KrtTemplateStore.Load(path);

        var details = FrameProcessing.ScoreDetailed(model, gray);
        Assert.Equal(1.0, details.BestScore, 10);
        Assert.Equal(13, details.Offsets.Count);
        Assert.Equal(0.0, details.BestOffsetX);
        Assert.Equal(0.0, details.BestOffsetY);
    }

    [Fact]
    public void HalfPixelShiftStillMatches()
    {
        // 16:10 实机里 HUD 会落在小数像素上：半像素偏移必须仍能匹配。
        var directory = CreateTempDirectory();
        var roi = new Roi(100, 100, 64, 64);
        var gray = CreateGrayFrame();
        ShiftRightHalfPixel(gray);
        var pixels = Extract(gray, roi);
        var path = Path.Combine(directory, "half.krt");
        KrtTemplateStore.Save(path, new TemplateModel { Roi = roi, Pixels = pixels });
        var model = KrtTemplateStore.Load(path);

        Assert.True(FrameProcessing.Score(model, gray) > 0.99);
    }

    [Fact]
    public void OnePixelShiftStillMatches()
    {
        var directory = CreateTempDirectory();
        var roi = new Roi(100, 100, 64, 64);
        var gray = CreateGrayFrame();
        ShiftRight(gray);
        var pixels = Extract(gray, new Roi(roi.X - 1, roi.Y, roi.Width, roi.Height));
        var path = Path.Combine(directory, "shift.krt");
        KrtTemplateStore.Save(path, new TemplateModel { Roi = roi, Pixels = pixels });
        var model = KrtTemplateStore.Load(path);

        Assert.True(FrameProcessing.Score(model, gray) > 0.999);
    }

    [Fact]
    public void NormalizerScalesSixteenByNineTo1920By1080()
    {
        const int width = 2560;
        const int height = 1440;
        var source = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            source[i * 4] = 0;
            source[i * 4 + 1] = 0;
            source[i * 4 + 2] = 255;
            source[i * 4 + 3] = 255;
        }

        var destination = FrameProcessing.Normalize(source, width, height);
        Assert.Equal(ReporterStates.FrameBytes, destination.Length);
        Assert.Equal(0, destination[0]);
        Assert.Equal(0, destination[1]);
        Assert.Equal(255, destination[2]);
        Assert.Equal(255, destination[^1]);
    }

    [Fact]
    public void NormalizerAlignsSixteenByTenTopAndCropsBottom()
    {
        // 16:10（2560x1600）按宽度缩放到 1920x1200，保留顶部、裁掉超出的 120 行。
        const int width = 2560;
        const int height = 1600;
        var source = new byte[width * height * 4];
        PaintSquare(source, width, 1200, 60, 16, 16);
        PaintSquare(source, width, 1200, 1500, 16, 16);

        var destination = FrameProcessing.Normalize(source, width, height);

        // 源 (1200,60) → 归一化 (900,45)：顶部不补黑边、不移位。
        Assert.Equal(255, destination[(((45 * ReporterStates.FrameWidth) + 900) * 4) + 2]);
        Assert.Equal(0, destination[0]);

        // 靠底的标记应当被裁掉：整帧只剩第一个标记（16×16 经 0.75 缩放后约 12×12）的亮像素。
        var brightCount = 0;
        for (var i = 0; i < ReporterStates.FrameWidth * ReporterStates.FrameHeight; i++)
        {
            if (destination[i * 4 + 2] > 128)
            {
                brightCount++;
            }
        }

        Assert.Equal(12 * 12, brightCount);
    }

    [Fact]
    public void NormalizerKeepsLetterboxingForWideSources()
    {
        // 21:9（2560x1080）比 16:9 更宽：仍按宽度缩放并垂直居中补黑边（原有行为）。
        const int width = 2560;
        const int height = 1080;
        var source = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            source[i * 4 + 2] = 255;
        }

        var destination = FrameProcessing.Normalize(source, width, height);

        // 810 行内容居中：顶部与底部是黑边，中间是内容。
        Assert.Equal(0, destination[0]);
        Assert.Equal(255, destination[(((ReporterStates.FrameHeight / 2) * ReporterStates.FrameWidth) + 960) * 4 + 2]);
        Assert.Equal(0, destination[^1]);
    }

    [Fact]
    public void RecognitionEngineLoadsVariantsAndTakesBestScore()
    {
        var directory = CreateTempDirectory();
        var gray = CreateGrayFrame();
        var primaryRoi = new Roi(100, 100, 64, 64);
        var variantRoi = new Roi(300, 100, 64, 64);

        KrtTemplateStore.Save(
            Path.Combine(directory, "round_start.krt"),
            new TemplateModel { Roi = primaryRoi, Pixels = Extract(gray, primaryRoi) });
        KrtTemplateStore.Save(
            Path.Combine(directory, "round_start.16x10.krt"),
            new TemplateModel { Roi = variantRoi, Pixels = Extract(gray, variantRoi) });

        using var engine = new RecognitionEngine(directory);
        Assert.Equal(1, engine.LoadedTemplateCount);
        Assert.Equal(1, engine.LoadedVariantCount);

        // 帧里只包含变体位置的内容 → 变体命中，主模板不该拖后腿。
        var bgra = new byte[ReporterStates.FrameBytes];
        for (var i = 0; i < ReporterStates.FrameWidth * ReporterStates.FrameHeight; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = gray[i];
            bgra[i * 4 + 3] = 255;
        }

        var result = engine.Process(
            new CapturedFrame(bgra, ReporterStates.FrameWidth, ReporterStates.FrameHeight, TimeSpan.Zero),
            ReporterStates.DefaultThreshold);

        Assert.Equal(StateId.RoundStart, result.StateId);
        Assert.True(result.BestScore > 0.99);
    }

    [Fact]
    public void StateMachineResetsRoundNumberAfterMatchEnd()
    {
        var state = new ReporterStateMachine();

        Assert.Equal(1, state.ProcessFrame(TimeSpan.Zero, Matches(StateId.RoundStart), Scores(StateId.RoundStart)).RoundNumber);
        state.ProcessFrame(TimeSpan.FromSeconds(30), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        Assert.Equal(2, state.ProcessFrame(TimeSpan.FromSeconds(140), Matches(StateId.RoundStart), Scores(StateId.RoundStart)).RoundNumber);

        var matchEnd = state.ProcessFrame(TimeSpan.FromSeconds(280), Matches(StateId.GameEndWin), Scores(StateId.GameEndWin));
        Assert.Equal(0, matchEnd.RoundNumber);

        // 下一局从第 1 回合重新数。
        Assert.Equal(1, state.ProcessFrame(TimeSpan.FromSeconds(300), Matches(StateId.RoundStart), Scores(StateId.RoundStart)).RoundNumber);
    }

    [Fact]
    public void StateMachineEmitsTimedEventsOnlyOnce()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        state.ProcessFrame(TimeSpan.Zero, Matches(StateId.RoundStart), Scores(StateId.RoundStart));
        state.ProcessFrame(TimeSpan.FromSeconds(1), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        state.ProcessFrame(TimeSpan.FromSeconds(76), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        state.ProcessFrame(TimeSpan.FromSeconds(77), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        state.ProcessFrame(TimeSpan.FromSeconds(96), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        state.ProcessFrame(TimeSpan.FromSeconds(97), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));

        Assert.Equal(1, events.Count(id => id == "event_round_ingame_last_40s"));
        Assert.Equal(1, events.Count(id => id == "event_round_ingame_last_20s"));
    }

    [Fact]
    public void StateMachineTriggersFirstRoundCountdownWhenPhaseEstimateEnds()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        state.ProcessFrame(TimeSpan.Zero, Matches(StateId.RoundStart), Scores(StateId.RoundStart));
        var beforeTrigger = state.ProcessFrame(
            TimeSpan.FromSeconds(35),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.DoesNotContain("event_round_start_last_5s", events);
        Assert.Equal(TimeSpan.FromSeconds(1), beforeTrigger.EstimatedPhaseRemaining);
        Assert.Equal(StateId.RoundStart, beforeTrigger.StateId);
        Assert.Equal(1, beforeTrigger.RoundNumber);

        state.ProcessFrame(
            TimeSpan.FromSeconds(36),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.Single(events, "event_round_start_last_5s");
    }

    [Fact]
    public void StateMachineTriggersRegularRoundCountdownAtMeasuredTime()
    {
        // 实机实测：常规回合在 22 秒触发时游戏内购买倒计时显示 00:07，
        // 00:05 出现在第 22 秒——播报要卡在游戏内剩 5 秒的时刻。
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        state.ProcessFrame(TimeSpan.Zero, Matches(StateId.RoundStart), Scores(StateId.RoundStart));
        state.ProcessFrame(TimeSpan.FromSeconds(1), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        var secondRoundStart = state.ProcessFrame(
            TimeSpan.FromSeconds(20),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));
        Assert.Equal(2, secondRoundStart.RoundNumber);

        var beforeTrigger = state.ProcessFrame(
            TimeSpan.FromSeconds(41),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));
        Assert.DoesNotContain("event_round_start_last_5s", events);
        Assert.Equal(TimeSpan.FromSeconds(1), beforeTrigger.EstimatedPhaseRemaining);

        state.ProcessFrame(
            TimeSpan.FromSeconds(42),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.Single(events, "event_round_start_last_5s");
    }

    [Fact]
    public void SecondHalfFirstRoundUsesLaterCountdown()
    {
        // 第 10 回合是下半场首回合：换边过场期间 round_start 已提前命中，
        // 触发点比上半场首回合更靠后；第 11 回合起回到常规计时。
        Assert.Equal(
            ReporterStates.SecondHalfFirstRoundStartCountdownSeconds,
            ReporterStates.GetRoundStartCountdownSeconds(10));
        Assert.Equal(
            ReporterStates.FirstRoundStartCountdownSeconds,
            ReporterStates.GetRoundStartCountdownSeconds(1));
        Assert.Equal(
            ReporterStates.RegularRoundStartCountdownSeconds,
            ReporterStates.GetRoundStartCountdownSeconds(11));
    }

    [Fact]
    public void StateMachineDoesNotIncrementRoundForRoundStartFlicker()
    {
        var state = new ReporterStateMachine();

        var firstEntry = state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));
        state.ProcessFrame(TimeSpan.FromSeconds(1), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        var flickerEntry = state.ProcessFrame(
            TimeSpan.FromSeconds(2),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));
        state.ProcessFrame(TimeSpan.FromSeconds(3), Matches(StateId.RoundIngame), Scores(StateId.RoundIngame));
        var nextRealRound = state.ProcessFrame(
            TimeSpan.FromSeconds(20),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.Equal(1, firstEntry.RoundNumber);
        Assert.Equal(1, flickerEntry.RoundNumber);
        Assert.Equal(2, nextRealRound.RoundNumber);
    }
[Fact]
    public void StateMachineRequiresClearlyBetterMatchBeforeSwitching()
    {
        var state = new ReporterStateMachine();
        var matches = new bool[ReporterStates.Count];
        var scores = Enumerable.Repeat(-1.0, ReporterStates.Count).ToArray();
        matches[(int)StateId.RoundIngame] = true;
        scores[(int)StateId.RoundIngame] = 0.96;

        state.ProcessFrame(TimeSpan.Zero, matches, scores);

        matches[(int)StateId.RoundStart] = true;
        scores[(int)StateId.RoundStart] = 0.98;
        var slightBetter = state.ProcessFrame(TimeSpan.FromSeconds(1), matches, scores);
        Assert.Equal(StateId.RoundIngame, slightBetter.StateId);

        scores[(int)StateId.RoundStart] = 1.00;
        var clearlyBetter = state.ProcessFrame(TimeSpan.FromSeconds(2), matches, scores);
        Assert.Equal(StateId.RoundStart, clearlyBetter.StateId);
    }

    [Fact]
    public void LosingRoundNeverPlaysVictoryVoice()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        // 防守方输掉回合（炸弹被引爆）：播战败语音，绝不播任何胜利语音。
        state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.42, DefenderScore: 0.97));

        var result = state.ProcessFrame(
            TimeSpan.FromSeconds(30),
            Matches(StateId.RoundEndLose),
            Scores(StateId.RoundEndLose));

        Assert.Equal(2, result.Side);
        Assert.Contains("event_round_end_defeat", events);
        Assert.DoesNotContain("event_round_end_attacker", events);
        Assert.DoesNotContain("event_round_end_defender", events);
        Assert.DoesNotContain("event_victory_in_last_3s", events);
    }

    [Fact]
    public void WinningRoundAnnouncesBySide()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: true, DefenderHit: false, AttackerScore: 0.97, DefenderScore: 0.31));
        state.ProcessFrame(TimeSpan.FromSeconds(10), Matches(StateId.RoundEndWin), Scores(StateId.RoundEndWin));

        Assert.Contains("event_round_end_attacker", events);
        Assert.DoesNotContain("event_round_end_defender", events);

        // 新一回合：守方守包点成功获胜，播守方胜利语音。
        state.ProcessFrame(TimeSpan.FromSeconds(20), Matches(StateId.RoundStart), Scores(StateId.RoundStart));
        state.ProcessFrame(
            TimeSpan.FromSeconds(30),
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.28, DefenderScore: 0.97));
        state.ProcessFrame(TimeSpan.FromSeconds(40), Matches(StateId.RoundEndWin), Scores(StateId.RoundEndWin));

        Assert.Contains("event_round_end_defender", events);
        Assert.Equal(1, events.Count(id => id == "event_round_end_attacker"));
    }

    [Fact]
    public void SideSignalCorrectsStaleSideWithinTheNextBuyPhase()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        // 旧的阵营信息（例如从别处开始的识别）先被标记为进攻方。
        state.ProcessFrame(TimeSpan.Zero, Matches(StateId.GameStartAttacker), Scores(StateId.GameStartAttacker));

        // 购买阶段横幅显示「守方」，阵营应立即纠正。
        var corrected = state.ProcessFrame(
            TimeSpan.FromSeconds(10),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.31, DefenderScore: 0.99));
        Assert.Equal(2, corrected.Side);

        // 我方防守方输掉回合，播战败语音。
        var roundEnd = state.ProcessFrame(
            TimeSpan.FromSeconds(40),
            Matches(StateId.RoundEndLose),
            Scores(StateId.RoundEndLose));
        Assert.Equal(2, roundEnd.Side);
        Assert.Contains("event_round_end_defeat", events);
        Assert.DoesNotContain("event_round_end_defender", events);
        Assert.DoesNotContain("event_round_end_attacker", events);
    }

    [Fact]
    public void SideFlipAnnouncesSwitchOnlyAfterKnownSide()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        // 第一次拿到阵营（守方）不算「攻守互换」。
        var first = state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.31, DefenderScore: 0.99));
        Assert.Equal(2, first.Side);
        Assert.DoesNotContain("event_game_switch_side", events);

        // 同一阵营再次出现不重复播报。
        state.ProcessFrame(
            TimeSpan.FromSeconds(60),
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.28, DefenderScore: 0.98));
        Assert.DoesNotContain("event_game_switch_side", events);

        // 下半场翻转到进攻方：播报一次「攻守互换」。
        var flipped = state.ProcessFrame(
            TimeSpan.FromSeconds(130),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart),
            new SideSignal(AttackerHit: true, DefenderHit: false, AttackerScore: 0.99, DefenderScore: 0.30));
        Assert.Equal(1, flipped.Side);
        Assert.Single(events, "event_game_switch_side");
    }

    [Fact]
    public void SwitchBannerDoesNotAnnounceUntilSideActuallyFlips()
    {
        var state = new ReporterStateMachine();
        var events = new List<string>();
        state.EventTriggered += events.Add;

        // 先确定阵营：进攻方。
        state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart),
            new SideSignal(AttackerHit: true, DefenderHit: false, AttackerScore: 0.99, DefenderScore: 0.30));
        Assert.DoesNotContain("event_game_switch_side", events);

        // 上半场最后一回合的购买阶段命中「下回合：切换为守方」横幅：
        // 此时离攻防转换还隔着一整个回合，只作预告，不播报，也不改动当前状态。
        var banner = Matches(StateId.RoundStart);
        banner[(int)StateId.GameSwitchSide] = true;
        var withBanner = state.ProcessFrame(TimeSpan.FromSeconds(60), banner, Scores(StateId.RoundStart));
        Assert.Equal(StateId.RoundStart, withBanner.StateId);
        Assert.DoesNotContain("event_game_switch_side", events);

        // 横幅持续显示：依旧不播报。
        state.ProcessFrame(TimeSpan.FromSeconds(61), banner, Scores(StateId.RoundStart));
        Assert.DoesNotContain("event_game_switch_side", events);

        // 下一回合阵营真正翻转（下半场开局）：这时才播报一次「攻守互换」。
        var flipped = state.ProcessFrame(
            TimeSpan.FromSeconds(130),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart),
            new SideSignal(AttackerHit: false, DefenderHit: true, AttackerScore: 0.30, DefenderScore: 0.99));
        Assert.Equal(2, flipped.Side);
        Assert.Single(events, "event_game_switch_side");
    }

    [Fact]
    public void SideSignalKeepsCurrentSideWhenBothLabelsAreClose()
    {
        var state = new ReporterStateMachine();

        var attacker = state.ProcessFrame(
            TimeSpan.Zero,
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: true, DefenderHit: false, AttackerScore: 0.95, DefenderScore: 0.40));
        Assert.Equal(1, attacker.Side);

        var ambiguous = state.ProcessFrame(
            TimeSpan.FromSeconds(5),
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            new SideSignal(AttackerHit: true, DefenderHit: true, AttackerScore: 0.95, DefenderScore: 0.94));
        Assert.Equal(1, ambiguous.Side);

        var neither = state.ProcessFrame(
            TimeSpan.FromSeconds(10),
            Matches(StateId.RoundIngame),
            Scores(StateId.RoundIngame),
            SideSignal.None);
        Assert.Equal(1, neither.Side);
    }

    [Fact]
    public void SettingsRoundTripAndNormalize()
    {
        var settings = new AppSettings
        {
            MatchThreshold = 2,
            AudioVolume = 2,
            LastCaptureTargetId = "window:123"
        };
        settings.Normalize();

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(0.999, restored.MatchThreshold);
        Assert.Equal(1f, restored.AudioVolume);
        Assert.Equal("window:123", restored.LastCaptureTargetId);
    }

    [Fact]
    public void FollowSystemDeviceUsesNullPlaybackId()
    {
        Assert.True(AudioDeviceInfo.FollowSystem.IsFollowSystem);
        Assert.Null(AudioDeviceInfo.FollowSystem.PlaybackDeviceId);

        var device = new AudioDeviceInfo("{0.0.0.00000000}.{guid}", "扬声器", true);
        Assert.False(device.IsFollowSystem);
        Assert.Equal(device.Id, device.PlaybackDeviceId);
    }

    [Fact]
    public void AudioDeviceIdNormalizesToFollowSystem()
    {
        var blank = new AppSettings { AudioDeviceId = "   " };
        blank.Normalize();
        Assert.Null(blank.AudioDeviceId);

        var pinned = new AppSettings { AudioDeviceId = "{0.0.0.00000000}.{guid}" };
        pinned.Normalize();
        Assert.Equal("{0.0.0.00000000}.{guid}", pinned.AudioDeviceId);

        var restoredBlank = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(blank))!;
        Assert.Null(restoredBlank.AudioDeviceId);
        var restoredPinned = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(pinned))!;
        Assert.Equal(pinned.AudioDeviceId, restoredPinned.AudioDeviceId);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KanamiReporterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] CreateGrayFrame()
    {
        var gray = new byte[ReporterStates.FrameWidth * ReporterStates.FrameHeight];
        for (var y = 0; y < ReporterStates.FrameHeight; y++)
        {
            for (var x = 0; x < ReporterStates.FrameWidth; x++)
            {
                gray[y * ReporterStates.FrameWidth + x] = (byte)((x * 3 + y * 5) % 256);
            }
        }

        return gray;
    }

    private static bool ContainsAsciiLetter(string value) =>
        value.Any(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static byte[] Extract(byte[] gray, Roi roi)
    {
        var pixels = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            Buffer.BlockCopy(gray, (roi.Y + y) * ReporterStates.FrameWidth + roi.X, pixels, y * roi.Width, roi.Width);
        }

        return pixels;
    }

    private static void PaintSquare(byte[] bgra, int width, int x0, int y0, int sizeX, int sizeY)
    {
        for (var y = y0; y < y0 + sizeY; y++)
        {
            for (var x = x0; x < x0 + sizeX; x++)
            {
                var pixel = ((y * width) + x) * 4;
                bgra[pixel] = 255;
                bgra[pixel + 1] = 255;
                bgra[pixel + 2] = 255;
                bgra[pixel + 3] = 255;
            }
        }
    }

    private static void ShiftRightHalfPixel(byte[] gray)
    {
        var copy = (byte[])gray.Clone();
        for (var y = 0; y < ReporterStates.FrameHeight; y++)
        {
            var row = y * ReporterStates.FrameWidth;
            for (var x = ReporterStates.FrameWidth - 1; x > 0; x--)
            {
                gray[row + x] = (byte)((copy[row + x] + copy[row + x - 1]) / 2);
            }

            gray[row] = copy[row];
        }
    }

    private static void ShiftRight(byte[] gray)
    {
        for (var y = 0; y < ReporterStates.FrameHeight; y++)
        {
            var row = y * ReporterStates.FrameWidth;
            for (var x = ReporterStates.FrameWidth - 1; x > 0; x--)
            {
                gray[row + x] = gray[row + x - 1];
            }

            gray[row] = gray[row + 1];
        }
    }

    private static bool[] Matches(StateId stateId)
    {
        var matches = new bool[ReporterStates.Count];
        matches[(int)stateId] = true;
        return matches;
    }

    private static double[] Scores(StateId stateId)
    {
        var scores = Enumerable.Repeat(-1.0, ReporterStates.Count).ToArray();
        scores[(int)stateId] = 1.0;
        return scores;
    }
}





