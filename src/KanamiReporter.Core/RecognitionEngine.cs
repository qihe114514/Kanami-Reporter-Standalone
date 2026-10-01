namespace KanamiReporter.Core;

public sealed class RecognitionEngine : IRecognitionEngine, IDisposable
{
    private readonly string _templateDirectory;
    private readonly TemplateModel?[] _models = new TemplateModel?[ReporterStates.Count];
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
    public string TemplateDirectory => _templateDirectory;

    public TemplateModel? GetTemplate(StateId stateId) => _models[(int)stateId];

    public void ReloadTemplates()
    {
        lock (_gate)
        {
            for (var i = 0; i < ReporterStates.Count; i++)
            {
                _models[i] = null;
                var path = GetTemplatePath((StateId)i);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    _models[i] = KrtTemplateStore.Load(path);
                }
                catch
                {
                    _models[i] = null;
                }
            }

            _stateMachine.Reset();
            ResetEventDeduplication();
        }
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
                var model = _models[i];
                scores[i] = model is null ? -1.0 : FrameProcessing.Score(model, gray);
                matches[i] = model is not null && scores[i] >= threshold;
            }

            var result = _stateMachine.ProcessFrame(normalizedFrame.Timestamp, matches, scores);
            DetectionUpdated?.Invoke(this, result);
            return result;
        }
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

