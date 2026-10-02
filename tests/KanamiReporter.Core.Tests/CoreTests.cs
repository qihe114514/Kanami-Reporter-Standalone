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
        Assert.Equal(5, details.Offsets.Count);
        Assert.Equal(0, details.BestOffsetX);
        Assert.Equal(0, details.BestOffsetY);
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
            TimeSpan.FromSeconds(30),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.DoesNotContain("event_round_start_last_5s", events);
        Assert.Equal(TimeSpan.FromSeconds(1), beforeTrigger.EstimatedPhaseRemaining);
        Assert.Equal(StateId.RoundStart, beforeTrigger.StateId);
        Assert.Equal(1, beforeTrigger.RoundNumber);

        state.ProcessFrame(
            TimeSpan.FromSeconds(31),
            Matches(StateId.RoundStart),
            Scores(StateId.RoundStart));

        Assert.Single(events, "event_round_start_last_5s");
    }

    [Fact]
    public void StateMachineTriggersRegularRoundCountdownAtOriginalTime()
    {
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





