using System.IO;
using KanamiReporter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanamiReporter.App;

/// <summary>诊断区：模板与语音映射、运行日志。默认折叠，展开时读取一次实际文件状态。</summary>
public partial class MainWindow
{
    private async void ToggleDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsExpanded = !_diagnosticsExpanded;
        SetVisible(DiagnosticsPanel, _diagnosticsExpanded);
        DiagnosticsChevron.Glyph = _diagnosticsExpanded ? "\uE70E" : "\uE70D";

        if (!_diagnosticsExpanded)
        {
            return;
        }

        await RefreshResourceStatusAsync();
        ScrollLogToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _debugMessages.Clear();
        AppendDebug("日志已清空。");
    }

    private async Task RefreshResourceStatusAsync()
    {
        try
        {
            var snapshot = await Task.Run(BuildResourceSnapshot);

            _resourceStates.Clear();
            foreach (var state in snapshot.States)
            {
                _resourceStates.Add(state);
            }

            SetText(
                VoiceStatsText,
                $"语音文件 {snapshot.VoiceFileCount} 个 · 已映射 {snapshot.MappedVoiceCount} 个");
            SetText(
                DiagnosticsSummaryText,
                $"模板 {snapshot.TemplateLoaded} 项可用 · 阵营标记 {snapshot.SideTemplateLoaded}/2 · 语音 {snapshot.MappedVoiceCount}/{snapshot.VoiceFileCount} 已映射");

            if (snapshot.UnmappedVoices.Count == 0)
            {
                SetText(UnmappedVoicesText, "语音目录中的文件都已映射到事件。");
                SetVisible(UnmappedVoicesExpander, false);
            }
            else
            {
                SetText(UnmappedVoicesText, string.Join(Environment.NewLine, snapshot.UnmappedVoices));
                SetVisible(UnmappedVoicesExpander, true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("刷新资源状态失败。", exception);
            SetText(DiagnosticsSummaryText, "资源状态读取失败，详情见日志。");
        }
    }

    private ResourceSnapshot BuildResourceSnapshot()
    {
        var voicesDirectory = Path.Combine(Path.GetDirectoryName(_runtime.TemplateDirectory)!, "voices");
        var voiceFiles = Directory.Exists(voicesDirectory)
            ? Directory.GetFiles(voicesDirectory, "*.mp3")
            : [];
        var referencedNames = ReporterStates.VoiceTable
            .SelectMany(item => item.FileNames)
            .ToHashSet(StringComparer.Ordinal);
        var mappedCount = voiceFiles.Count(path => referencedNames.Contains(Path.GetFileName(path)));
        var unmappedFiles = voiceFiles
            .Where(path => !referencedNames.Contains(Path.GetFileName(path)))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToArray();

        var states = new List<ResourceStateItem>(ReporterStates.Count);
        for (var i = 0; i < ReporterStates.Count; i++)
        {
            var stateId = (StateId)i;
            var eventIds = ReporterStates.GetVoiceEventIds(stateId);
            var activeEventCount = eventIds.Count(eventId =>
            {
                var definition = ReporterStates.VoiceTable.FirstOrDefault(item => item.EventId == eventId);
                return definition is not null &&
                       definition.FileNames.Any(name => File.Exists(Path.Combine(voicesDirectory, name)));
            });

            states.Add(new ResourceStateItem(
                stateId,
                _runtime.GetTemplate(stateId) is not null,
                activeEventCount,
                eventIds.Count));
        }

        return new ResourceSnapshot(
            _runtime.LoadedTemplateCount,
            _runtime.LoadedSideTemplateCount,
            voiceFiles.Length,
            mappedCount,
            states,
            unmappedFiles);
    }

    private sealed record ResourceSnapshot(
        int TemplateLoaded,
        int SideTemplateLoaded,
        int VoiceFileCount,
        int MappedVoiceCount,
        IReadOnlyList<ResourceStateItem> States,
        IReadOnlyList<string> UnmappedVoices);
}
