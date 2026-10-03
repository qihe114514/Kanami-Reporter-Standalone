namespace KanamiReporter.Core;

public sealed class RecognitionEngine : IRecognitionEngine, IDisposable
{
    private readonly string _templateDirectory;
    private readonly TemplateModel?[] _models = new TemplateModel?[ReporterStates.Count];
    private readonly IReadOnlyList<TemplateModel>[] _variantModels = new IReadOnlyList<TemplateModel>[ReporterStates.Count];
    private readonly TemplateModel?[] _sideModels = new TemplateModel?[2];
    private readonly IReadOnlyList<TemplateModel>[] _sideVariantModels = new IReadOnlyList<TemplateModel>[2];
    private readonly ReporterStateMachine _stateMachine = new();
    private readonly HashSet<string> _announcedEventsForState = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private StateId? _lastAnnouncementStateId;

    public RecognitionEngine(string templateDirectory)
    {
        _templateDirectory = templateDirectory;
        Directory.CreateDirectory(_templateDirectory);
        _stateMachine.EventTriggered += OnStateEventTriggered;
        ReloadTemplates();
    }

    public event EventHandler<AnnouncementRequest>? AnnouncementRequested;
    public event EventHandler<DetectionResult>? DetectionUpdated;

    public int LoadedTemplateCount => _models.Count(model => model is not null);
    public int LoadedSideTemplateCount => _sideModels.Count(model => model is not null);

    /// <summary>附加变体模板总数：按分辨率/宽高比补充的模板（如 16:10 专用版本）。</summary>
    public int LoadedVariantCount =>
        _variantModels.Sum(models => models.Count) + _sideVariantModels.Sum(models => models.Count);

    public string TemplateDirectory => _templateDirectory;

    /// <summary>某个状态实际可用的模板：主模板优先，没有主模板时取变体（例如只有 16:10 变体的攻守互换）。</summary>
    public TemplateModel? GetTemplate(StateId stateId) =>
        _models[(int)stateId] ?? _variantModels[(int)stateId].FirstOrDefault();

    public void ReloadTemplates()
    {
        lock (_gate)
        {
            for (var i = 0; i < ReporterStates.Count; i++)
            {
                _models[i] = LoadPrimaryTemplate(ReporterStates.GetName((StateId)i));
                _variantModels[i] = LoadVariantTemplates(ReporterStates.GetName((StateId)i));
            }

            _sideModels[0] = LoadPrimaryTemplate(ReporterStates.AttackerSideTemplateName);
            _sideModels[1] = LoadPrimaryTemplate(ReporterStates.DefenderSideTemplateName);
            _sideVariantModels[0] = LoadVariantTemplates(ReporterStates.AttackerSideTemplateName);
            _sideVariantModels[1] = LoadVariantTemplates(ReporterStates.DefenderSideTemplateName);

            _stateMachine.Reset();
            ResetEventDeduplication();
        }
    }

    private TemplateModel? LoadPrimaryTemplate(string name)
    {
        var path = Path.Combine(_templateDirectory, name + ".krt");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return KrtTemplateStore.Load(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 加载同一状态名下的附加变体模板：文件名形如「状态名.标签.krt」，
    /// 例如 round_ingame.16x10.krt。变体与主模板一起参与匹配，取最高分——
    /// 这样同一份状态模板可以同时覆盖多个宽高比/分辨率，不需要在运行时判断当前画面尺寸。
    /// </summary>
    private IReadOnlyList<TemplateModel> LoadVariantTemplates(string name)
    {
        var prefix = name + ".";
        var variants = new List<TemplateModel>();
        foreach (var path in Directory.GetFiles(_templateDirectory, "*.krt"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.Length <= prefix.Length ||
                !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                variants.Add(KrtTemplateStore.Load(path));
            }
            catch
            {
            }
        }

        return variants;
    }

    public DetectionResult Process(CapturedFrame normalizedFrame, double threshold)
    {
        if (normalizedFrame.Width != ReporterStates.FrameWidth || normalizedFrame.Height != ReporterStates.FrameHeight)
        {
            throw new ArgumentException("识别帧必须先归一化为 1920x1080。", nameof(normalizedFrame));
        }

        lock (_gate)
        {
            var gray = FrameProcessing.ToGrayscale(normalizedFrame.Bgra, normalizedFrame.Width, normalizedFrame.Height);
            var scores = new double[ReporterStates.Count];
            var matches = new bool[ReporterStates.Count];

            for (var i = 0; i < ReporterStates.Count; i++)
            {
                scores[i] = ScoreState(i, gray);
                matches[i] = scores[i] >= threshold;
            }

            var sideSignal = BuildSideSignal(gray, threshold);
            var result = _stateMachine.ProcessFrame(normalizedFrame.Timestamp, matches, scores, sideSignal);
            DetectionUpdated?.Invoke(this, result);
            return result;
        }
    }

    /// <summary>某个状态的全部模板（主模板 + 变体）中的最高分；没有可用模板时为 -1。</summary>
    private double ScoreState(int index, byte[] gray)
    {
        var best = -1.0;
        if (_models[index] is { } primary)
        {
            best = FrameProcessing.Score(primary, gray);
        }

        foreach (var variant in _variantModels[index])
        {
            var score = FrameProcessing.Score(variant, gray);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    private double ScoreSide(int index, byte[] gray)
    {
        var best = -1.0;
        if (_sideModels[index] is { } primary)
        {
            best = FrameProcessing.Score(primary, gray);
        }

        foreach (var variant in _sideVariantModels[index])
        {
            var score = FrameProcessing.Score(variant, gray);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    /// <summary>阵营标签是辅助判断，阈值允许比状态模板略低，避免缩放差异造成漏检。</summary>
    private const double SideThresholdMargin = 0.05;
    private const double SideThresholdFloor = 0.80;

    /// <summary>阵营标签模板按同一阈值判定，只有确实命中一侧时才作为阵营依据。</summary>
    private SideSignal BuildSideSignal(byte[] gray, double threshold)
    {
        if (_sideModels[0] is null && _sideModels[1] is null &&
            _sideVariantModels[0].Count == 0 && _sideVariantModels[1].Count == 0)
        {
            return SideSignal.None;
        }

        var sideThreshold = Math.Clamp(threshold - SideThresholdMargin, SideThresholdFloor, 1.0);
        var attackerScore = ScoreSide(0, gray);
        var defenderScore = ScoreSide(1, gray);
        return new SideSignal(
            attackerScore >= sideThreshold,
            defenderScore >= sideThreshold,
            attackerScore,
            defenderScore);
    }

    public void SaveTemplate(StateId stateId, CapturedFrame normalizedFrame, Roi roi)
    {
        if (normalizedFrame.Width != ReporterStates.FrameWidth || normalizedFrame.Height != ReporterStates.FrameHeight)
        {
            throw new ArgumentException("模板帧必须是 1920x1080。", nameof(normalizedFrame));
        }

        if (!KrtTemplateStore.IsValidRoi(roi))
        {
            throw new ArgumentException("识别区域超出有效范围或面积过大。", nameof(roi));
        }

        var gray = FrameProcessing.ToGrayscale(normalizedFrame.Bgra, normalizedFrame.Width, normalizedFrame.Height);
        var pixels = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            Buffer.BlockCopy(
                gray,
                (roi.Y + y) * ReporterStates.FrameWidth + roi.X,
                pixels,
                y * roi.Width,
                roi.Width);
        }

        var model = new TemplateModel { Roi = roi, Pixels = pixels };
        var path = GetTemplatePath(stateId);
        KrtTemplateStore.Save(path, model);
        lock (_gate)
        {
            _models[(int)stateId] = KrtTemplateStore.Load(path);
            _stateMachine.Reset();
            ResetEventDeduplication();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _stateMachine.Reset();
            ResetEventDeduplication();
        }
    }

    private void OnStateEventTriggered(string eventId)
    {
        var stateId = _stateMachine.CurrentStateId;
        if (_lastAnnouncementStateId != stateId)
        {
            _lastAnnouncementStateId = stateId;
            _announcedEventsForState.Clear();
        }

        if (!_announcedEventsForState.Add(eventId))
        {
            return;
        }

        var voice = ReporterStates.VoiceTable.FirstOrDefault(item => item.EventId == eventId);
        AnnouncementRequested?.Invoke(this, new AnnouncementRequest(eventId, voice?.FileNames ?? []));
    }

    private void ResetEventDeduplication()
    {
        _lastAnnouncementStateId = null;
        _announcedEventsForState.Clear();
    }

    public string GetTemplatePath(StateId stateId)
    {
        return Path.Combine(_templateDirectory, ReporterStates.GetName(stateId) + ".krt");
    }

    public void Dispose()
    {
    }
}

