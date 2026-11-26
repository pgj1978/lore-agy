using Lore.Core.Extraction;
using Xunit;

namespace Lore.Core.Tests;

public sealed class TranscriptPreFilterTests : IDisposable
{
    private readonly string _tempDir;

    public TranscriptPreFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void AgyReader_SkipsSystemMessages_AndPreFilterRetainsErrorsAndUiTraces()
    {
        var path = Path.Combine(_tempDir, "agy.jsonl");
        File.WriteAllLines(path,
        [
            """{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","content":"<USER_REQUEST>\nAlways use port 5100 for local dev\n</USER_REQUEST>"}""",
            """{"step_index":1,"source":"SYSTEM_SDK","type":"SYSTEM_MESSAGE","content":"<hindsight_memory>noisy system prompt</hindsight_memory>"}""",
            """{"step_index":2,"source":"MODEL","type":"PLANNER_RESPONSE","content":"Checking server.","tool_calls":[{"name":"run_command","args":{"CommandLine":"dotnet run"}}]}""",
            """{"step_index":3,"source":"MODEL","type":"GENERIC","content":"Command exited with code 1. Missing LORE_ENV."}""",
            """{"step_index":4,"source":"MODEL","type":"PLANNER_RESPONSE","content":"Need LORE_ENV=dev before running.","tool_calls":[{"name":"run_command","args":{"CommandLine":"LORE_ENV=dev dotnet run"}}]}""",
            """{"step_index":5,"source":"MODEL","type":"GENERIC","content":"Command exited with code 0. Listening on 5100."}""",
            """{"step_index":6,"source":"MODEL","type":"PLANNER_RESPONSE","content":"Inspecting login page.","tool_calls":[{"name":"browser_navigate","args":{"url":"http://localhost:5100/login"}}]}""",
            """{"step_index":7,"source":"MODEL","type":"PLANNER_RESPONSE","content":"Login screen renders an SSO button and redirects to /dashboard."}"""
        ]);

        var entries = new AgyTranscriptReader().Read(path);
        Assert.DoesNotContain(entries, e => e.Content.Contains("noisy system prompt"));

        var slices = TranscriptPreFilter.Filter(entries);
        Assert.Contains(slices, s => s.Category == "user_input" && s.Text.Contains("Always use port 5100"));
        Assert.Contains(slices, s => s.Category == "error_recovery" && s.Text.Contains("Missing LORE_ENV"));
        Assert.Contains(slices, s => s.Category == "ui_inspection" && s.Text.Contains("SSO button"));
        Assert.True(slices.Sum(s => s.Text.Length) <= TranscriptPreFilter.DefaultMaxTotalChars);
    }

    [Fact]
    public void ClaudeAndCodexReaders_ParseMessagesAndToolFailures()
    {
        var claudePath = Path.Combine(_tempDir, "claude.jsonl");
        File.WriteAllLines(claudePath,
        [
            """{"type":"user","message":{"role":"user","content":"<lore_memory>skip injected</lore_memory>"}}""",
            """{"type":"user","message":{"role":"user","content":"Never commit secrets to git"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"bash","input":{"cmd":"pytest"}},{"type":"text","text":"Running tests"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","is_error":true,"content":"ModuleNotFoundError: foo"}]}}"""
        ]);

        var claudeEntries = new ClaudeTranscriptReader().Read(claudePath);
        Assert.DoesNotContain(claudeEntries, e => e.Content.Contains("skip injected"));
        Assert.Contains(claudeEntries, e => e.Kind == TranscriptEntryKind.User && e.Content.Contains("Never commit secrets"));
        Assert.Contains(claudeEntries, e => e.Kind == TranscriptEntryKind.ToolResult && e.IsError);

        var codexPath = Path.Combine(_tempDir, "codex.jsonl");
        File.WriteAllLines(codexPath,
        [
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Use Cosmos Serverless"}]}}""",
            """{"type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{\"cmd\":\"az login\"}"}}""",
            """{"type":"response_item","payload":{"type":"function_call_output","output":"Logged in"}}"""
        ]);

        var codexEntries = new CodexTranscriptReader().Read(codexPath);
        Assert.Equal(3, codexEntries.Count);
        Assert.Equal(TranscriptEntryKind.User, codexEntries[0].Kind);
        Assert.Equal(TranscriptEntryKind.ToolCall, codexEntries[1].Kind);
        Assert.Equal(TranscriptEntryKind.ToolResult, codexEntries[2].Kind);
    }
}
