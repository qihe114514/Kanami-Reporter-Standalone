using KanamiReporter.Core;

namespace KanamiReporter.App;

/// <summary>
/// 诊断区「模板与语音」列表中的一行：一个识别状态对应的模板与语音映射情况。
/// 内容在刷新时整体重建，因此不需要变更通知。
/// </summary>
public sealed class ResourceStateItem
{
    public ResourceStateItem(StateId stateId, bool hasTemplate, int activeVoiceEvents, int totalVoiceEvents)
    {
        StateName = ReporterStates.GetDisplayName(stateId);
        Description = ReporterStates.GetDescription(stateId);
        HasTemplate = hasTemplate;
        IsNoTemplate = !hasTemplate;
        VoiceText = totalVoiceEvents == 0 ? "—" : $"语音 {activeVoiceEvents}/{totalVoiceEvents}";
    }

    public string StateName { get; }

    public string Description { get; }

    public bool HasTemplate { get; }

    public bool IsNoTemplate { get; }

    public string VoiceText { get; }
}
