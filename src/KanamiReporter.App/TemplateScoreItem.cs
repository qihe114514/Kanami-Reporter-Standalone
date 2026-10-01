using System.ComponentModel;
using System.Runtime.CompilerServices;
using KanamiReporter.Core;

namespace KanamiReporter.App;

/// <summary>
/// 模板匹配列表中的一行。
/// 分数、命中状态与名次由 <see cref="Apply"/> 一次性写入，只有真正变化的属性才发通知：
/// 分数每帧都在小幅波动，逐属性无条件通知会让整个列表持续重绘。
/// </summary>
public sealed class TemplateScoreItem : INotifyPropertyChanged
{
    private double _score = -1;
    private string _rankText = "--";
    private string _scoreText = "–";
    private bool _isHit;
    private bool _hasTemplate;
    private bool _isNoTemplate = true;

    public TemplateScoreItem(StateId stateId)
    {
        StateId = stateId;
        StateName = ReporterStates.GetDisplayName(stateId);
        Description = ReporterStates.GetDescription(stateId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StateId StateId { get; }

    public string StateName { get; }

    public string Description { get; }

    public double Score => _score;

    public string RankText
    {
        get => _rankText;
        private set => SetField(ref _rankText, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        private set => SetField(ref _scoreText, value);
    }

    public bool IsHit
    {
        get => _isHit;
        private set => SetField(ref _isHit, value);
    }

    /// <summary>
    /// 该状态是否存在模板文件。这是模板本身的属性，与当前分数无关：
    /// 尚未开始识别时分数同样是 -1，但那时不能说这些状态“无模板”。
    /// </summary>
    public bool HasTemplate
    {
        get => _hasTemplate;
        private set => SetField(ref _hasTemplate, value);
    }

    /// <summary>该状态没有对应模板（分数为 -1），永远不会参与识别。</summary>
    public bool IsNoTemplate
    {
        get => _isNoTemplate;
        private set => SetField(ref _isNoTemplate, value);
    }

    public void SetTemplateAvailability(bool hasTemplate)
    {
        HasTemplate = hasTemplate;
        IsNoTemplate = !hasTemplate;
    }

    public void SetScore(double score, bool isHit)
    {
        _score = score;
        _isHit = _hasTemplate && isHit;
        ScoreText = _score < 0 ? "–" : _score.ToString("0.000");
        IsHit = _isHit;
    }

    public void SetRank(int rank) => RankText = rank.ToString("00");

    /// <summary>停止识别后清空分数与命中，保留模板是否存在这一事实。</summary>
    public void Reset() => SetScore(-1, isHit: false);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
