using System.Text;

namespace Lore.Core.Extraction;

public static class TranscriptPreFilter
{
    public const int DefaultMaxTotalChars = 12000;

    public static IReadOnlyList<FilteredSlice> Filter(
        IReadOnlyList<TranscriptEntry> entries,
        int maxTotalChars = DefaultMaxTotalChars)
    {
        if (entries.Count == 0)
            return [];

        var slices = new List<FilteredSlice>();
        var totalChars = 0;

        void TryAdd(string category, int startStep, int endStep, string text)
        {
            if (totalChars >= maxTotalChars || string.IsNullOrWhiteSpace(text))
                return;

            var trimmed = text.Trim();
            if (trimmed.Length > 2600)
                trimmed = trimmed[..2597] + "...";

            if (totalChars + trimmed.Length > maxTotalChars)
                trimmed = trimmed[..Math.Max(200, maxTotalChars - totalChars)];

            slices.Add(new FilteredSlice(category, startStep, endStep, trimmed));
            totalChars += trimmed.Length;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];

            if (e.Kind == TranscriptEntryKind.User)
            {
                if (LooksLikeRuleOrCorrection(e.Content) || e.Content.Length >= 40)
                    TryAdd("user_input", e.StepIndex, e.StepIndex, $"USER: {Compact(e.Content, 1200)}");
                continue;
            }

            if (e.Kind == TranscriptEntryKind.ToolResult && e.IsError)
            {
                var sb = new StringBuilder();
                if (i > 0 && entries[i - 1].Kind == TranscriptEntryKind.ToolCall)
                    sb.AppendLine($"FAILED CALL: {Compact(entries[i - 1].Content, 400)}");
                sb.AppendLine($"ERROR: {Compact(e.Content, 700)}");

                var endStep = e.StepIndex;
                for (var j = i + 1; j < Math.Min(entries.Count, i + 6); j++)
                {
                    endStep = entries[j].StepIndex;
                    if (entries[j].Kind == TranscriptEntryKind.ToolCall)
                        sb.AppendLine($"NEXT CALL: {Compact(entries[j].Content, 400)}");
                    else if (entries[j].Kind == TranscriptEntryKind.ToolResult && !entries[j].IsError)
                    {
                        sb.AppendLine($"RECOVERED: {Compact(entries[j].Content, 400)}");
                        break;
                    }
                    else if (entries[j].Kind == TranscriptEntryKind.Assistant)
                    {
                        sb.AppendLine($"DIAGNOSIS: {Compact(entries[j].Content, 600)}");
                    }
                }

                TryAdd("error_recovery", e.StepIndex, endStep, sb.ToString());
                continue;
            }

            if (e.Kind == TranscriptEntryKind.ToolCall && IsUiInspectionTool(e.ToolName))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"UI TOOL: {Compact(e.Content, 500)}");
                var endStep = e.StepIndex;
                for (var j = i + 1; j < Math.Min(entries.Count, i + 4); j++)
                {
                    if (entries[j].Kind == TranscriptEntryKind.Assistant)
                    {
                        sb.AppendLine($"UI OBSERVATION: {Compact(entries[j].Content, 1000)}");
                        endStep = entries[j].StepIndex;
                        break;
                    }
                }

                TryAdd("ui_inspection", e.StepIndex, endStep, sb.ToString());
            }
        }

        // Always keep final assistant conclusion(s) so decisions & completed workflows are captured
        var lastAssistant = entries.LastOrDefault(e => e.Kind == TranscriptEntryKind.Assistant);
        if (lastAssistant is not null)
            TryAdd("session_outcome", lastAssistant.StepIndex, lastAssistant.StepIndex, $"FINAL RESPONSE: {Compact(lastAssistant.Content, 1800)}");

        return slices;
    }

    private static bool LooksLikeRuleOrCorrection(string text)
        => text.Contains("never ", StringComparison.OrdinalIgnoreCase)
           || text.Contains("always ", StringComparison.OrdinalIgnoreCase)
           || text.Contains("don't ", StringComparison.OrdinalIgnoreCase)
           || text.Contains("do not ", StringComparison.OrdinalIgnoreCase)
           || text.Contains("instead", StringComparison.OrdinalIgnoreCase)
           || text.Contains("actually", StringComparison.OrdinalIgnoreCase)
           || text.Contains("wrong", StringComparison.OrdinalIgnoreCase)
           || text.Contains("rule", StringComparison.OrdinalIgnoreCase);

    private static bool IsUiInspectionTool(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) &&
           (toolName.StartsWith("browser_", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("playwright", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("snapshot", StringComparison.OrdinalIgnoreCase));

    private static string Compact(string text, int maxLen)
    {
        var cleaned = text.Replace("\r\n", "\n").Trim();
        return cleaned.Length <= maxLen ? cleaned : cleaned[..(maxLen - 3)] + "...";
    }
}
