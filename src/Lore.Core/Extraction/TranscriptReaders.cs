using Newtonsoft.Json.Linq;

namespace Lore.Core.Extraction;

public interface ITranscriptReader
{
    string HarnessName { get; }
    IReadOnlyList<TranscriptEntry> Read(string path, int sinceStep = -1);
}

public sealed class AgyTranscriptReader : ITranscriptReader
{
    public string HarnessName => "agy";

    public IReadOnlyList<TranscriptEntry> Read(string path, int sinceStep = -1)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<TranscriptEntry>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject obj;
            try { obj = JObject.Parse(line); }
            catch { continue; }

            var step = obj["step_index"]?.Value<int>() ?? entries.Count;
            if (step <= sinceStep) continue;

            var source = obj["source"]?.ToString() ?? string.Empty;
            var type = obj["type"]?.ToString() ?? string.Empty;
            var content = obj["content"]?.ToString() ?? string.Empty;

            if (source == "SYSTEM_SDK" || type == "SYSTEM_MESSAGE")
                continue;

            if (type == "USER_INPUT")
            {
                var cleaned = StripTags(content, "USER_REQUEST");
                if (!string.IsNullOrWhiteSpace(cleaned))
                    entries.Add(new TranscriptEntry(step, TranscriptEntryKind.User, cleaned));
            }
            else if (type == "PLANNER_RESPONSE")
            {
                if (!string.IsNullOrWhiteSpace(content))
                    entries.Add(new TranscriptEntry(step, TranscriptEntryKind.Assistant, content));

                if (obj["tool_calls"] is JArray toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        var name = tc["name"]?.ToString() ?? "tool";
                        var args = tc["args"]?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                        entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolCall, $"{name}({Truncate(args, 400)})", name));
                    }
                }
            }
            else if (type == "GENERIC" && !string.IsNullOrWhiteSpace(content))
            {
                var isErr = LooksLikeError(content);
                entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolResult, content, null, isErr));
            }
        }

        return entries;
    }

    private static string StripTags(string input, string tagName)
    {
        var open = $"<{tagName}>";
        var close = $"</{tagName}>";
        var start = input.IndexOf(open, StringComparison.Ordinal);
        var end = input.IndexOf(close, StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return input[(start + open.Length)..end].Trim();
        return input.Trim();
    }

    internal static bool LooksLikeError(string text)
        => text.Contains("Encountered error", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Command exited with code 1", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
           || text.Contains("error CS", StringComparison.OrdinalIgnoreCase)
           || text.Contains("FAILED", StringComparison.Ordinal);

    internal static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 3)] + "...";
}

public sealed class ClaudeTranscriptReader : ITranscriptReader
{
    public string HarnessName => "claude";

    public IReadOnlyList<TranscriptEntry> Read(string path, int sinceStep = -1)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<TranscriptEntry>();
        var idx = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject obj;
            try { obj = JObject.Parse(line); }
            catch { continue; }

            var step = idx++;
            if (step <= sinceStep) continue;

            var type = obj["type"]?.ToString() ?? string.Empty;
            var msg = obj["message"] as JObject ?? obj;
            var contentToken = msg["content"];

            if (type == "user" || msg["role"]?.ToString() == "user")
            {
                ExtractMessageParts(step, contentToken, TranscriptEntryKind.User, entries);
            }
            else if (type == "assistant" || msg["role"]?.ToString() == "assistant")
            {
                ExtractMessageParts(step, contentToken, TranscriptEntryKind.Assistant, entries);
            }
        }

        return entries;
    }

    private static void ExtractMessageParts(int step, JToken? contentToken, TranscriptEntryKind defaultKind, List<TranscriptEntry> entries)
    {
        if (contentToken is null) return;
        if (contentToken.Type == JTokenType.String)
        {
            var text = contentToken.ToString();
            if (!IsInjectedNoise(text))
                entries.Add(new TranscriptEntry(step, defaultKind, text));
            return;
        }

        if (contentToken is not JArray arr) return;
        foreach (var item in arr.OfType<JObject>())
        {
            var partType = item["type"]?.ToString();
            if (partType == "text")
            {
                var text = item["text"]?.ToString() ?? string.Empty;
                if (!IsInjectedNoise(text) && !string.IsNullOrWhiteSpace(text))
                    entries.Add(new TranscriptEntry(step, defaultKind, text));
            }
            else if (partType == "tool_use")
            {
                var name = item["name"]?.ToString() ?? "tool";
                var input = item["input"]?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty;
                entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolCall, $"{name}({AgyTranscriptReader.Truncate(input, 400)})", name));
            }
            else if (partType == "tool_result")
            {
                var text = item["content"]?.ToString() ?? string.Empty;
                var isErr = item["is_error"]?.Value<bool>() == true || AgyTranscriptReader.LooksLikeError(text);
                if (!string.IsNullOrWhiteSpace(text))
                    entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolResult, text, null, isErr));
            }
        }
    }

    internal static bool IsInjectedNoise(string text)
        => text.Contains("<lore_memory>", StringComparison.OrdinalIgnoreCase)
           || text.Contains("<hindsight_memory>", StringComparison.OrdinalIgnoreCase)
           || text.Contains("<SYSTEM_MESSAGE>", StringComparison.OrdinalIgnoreCase);
}

public sealed class CodexTranscriptReader : ITranscriptReader
{
    public string HarnessName => "codex";

    public IReadOnlyList<TranscriptEntry> Read(string path, int sinceStep = -1)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<TranscriptEntry>();
        var idx = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject obj;
            try { obj = JObject.Parse(line); }
            catch { continue; }

            var step = idx++;
            if (step <= sinceStep) continue;

            var type = obj["type"]?.ToString() ?? string.Empty;
            var payload = obj["payload"] as JObject ?? obj;
            var payloadType = payload["type"]?.ToString() ?? type;

            if (payloadType == "message" || type is "user" or "assistant")
            {
                var role = payload["role"]?.ToString() ?? type;
                var text = ExtractText(payload["content"]);
                if (string.IsNullOrWhiteSpace(text) || ClaudeTranscriptReader.IsInjectedNoise(text))
                    continue;

                var kind = role == "user" ? TranscriptEntryKind.User : TranscriptEntryKind.Assistant;
                entries.Add(new TranscriptEntry(step, kind, text));
            }
            else if (payloadType is "function_call" or "local_shell_call")
            {
                var name = payload["name"]?.ToString() ?? payloadType;
                var args = payload["arguments"]?.ToString() ?? payload["action"]?.ToString() ?? string.Empty;
                entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolCall, $"{name}({AgyTranscriptReader.Truncate(args, 400)})", name));
            }
            else if (payloadType is "function_call_output" or "local_shell_call_output")
            {
                var output = payload["output"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(output))
                    entries.Add(new TranscriptEntry(step, TranscriptEntryKind.ToolResult, output, null, AgyTranscriptReader.LooksLikeError(output)));
            }
        }

        return entries;
    }

    private static string ExtractText(JToken? token)
    {
        if (token is null) return string.Empty;
        if (token.Type == JTokenType.String) return token.ToString();
        if (token is JArray arr)
        {
            return string.Join("\n", arr
                .Select(x => x["text"]?.ToString() ?? x["input_text"]?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return token.ToString();
    }
}

public static class TranscriptReaderFactory
{
    public static ITranscriptReader ForHarness(string harness) => harness.ToLowerInvariant() switch
    {
        "agy" => new AgyTranscriptReader(),
        "claude" or "claude-code" => new ClaudeTranscriptReader(),
        "codex" => new CodexTranscriptReader(),
        _ => throw new ArgumentException($"Unsupported harness: {harness}", nameof(harness))
    };
}
