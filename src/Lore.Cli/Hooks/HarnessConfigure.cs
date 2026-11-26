using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lore.Cli.Hooks;

public static class HarnessConfigure
{
    public static string Configure(string harness, string? workspacePath, bool global, string? executablePath = null)
    {
        var loreExe = (executablePath ?? Environment.ProcessPath ?? "lore").Replace('\\', '/');
        return harness.ToLowerInvariant() switch
        {
            "agy" => ConfigureAgy(loreExe, workspacePath, global),
            "claude" => ConfigureClaude(loreExe, workspacePath, global),
            "codex" => ConfigureCodex(loreExe, workspacePath, global),
            _ => throw new ArgumentException($"Unknown harness '{harness}'. Expected agy, claude, or codex.", nameof(harness))
        };
    }

    private static string ConfigureAgy(string loreExe, string? workspacePath, bool global)
    {
        var baseDir = !global && !string.IsNullOrWhiteSpace(workspacePath)
            ? Path.Combine(workspacePath, ".agents", "plugins", "lore")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "plugins", "lore");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "rules"));

        File.WriteAllText(Path.Combine(baseDir, "plugin.json"), JsonConvert.SerializeObject(new
        {
            name = "lore",
            description = "Shared cross-session memory for coding agents"
        }, Formatting.Indented));

        File.WriteAllText(Path.Combine(baseDir, "mcp_config.json"), JsonConvert.SerializeObject(new
        {
            mcpServers = new
            {
                lore = new
                {
                    command = loreExe,
                    args = new[] { "mcp", "--harness", "agy" }
                }
            }
        }, Formatting.Indented));

        // Native Agy plugins MUST place hooks.json directly at the plugin root
        File.WriteAllText(Path.Combine(baseDir, "hooks.json"), JsonConvert.SerializeObject(new
        {
            lore = new
            {
                SessionStart = new[]
                {
                    new { type = "command", command = $"\"{loreExe}\" hook agy session-start", timeout = 12 }
                },
                PreInvocation = new[]
                {
                    new { type = "command", command = $"\"{loreExe}\" hook agy prompt-push", timeout = 12 }
                },
                Stop = new[]
                {
                    new { type = "command", command = $"\"{loreExe}\" hook agy stop", timeout = 5 }
                }
            }
        }, Formatting.Indented));

        File.WriteAllText(Path.Combine(baseDir, "rules", "AGENTS.md"), """
            # Lore Shared Memory

            Hard rules and a table of contents of known `process` / `app_ui` docs are injected automatically at session start.
            - Use `lore_get(id)` to read a full process runbook or app/UI specification before reinventing steps or guessing UI flows.
            - Use `lore_search(query)` whenever entering an unfamiliar area or checking prior decisions/gotchas.
            - Use `lore_save` when you document a reusable process, map an app/UI screen & flow, or record an explicit user rule.
            - If a memory is flagged `[possibly stale]`, verify it against the code and call `lore_verify(id)` if still accurate (or update it with `lore_save`).
            """);

        return baseDir;
    }

    private static string ConfigureClaude(string loreExe, string? workspacePath, bool global)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var mcpFile = !global && !string.IsNullOrWhiteSpace(workspacePath)
            ? Path.Combine(workspacePath, ".mcp.json")
            : Path.Combine(home, ".claude.json");

        var settingsFile = !global && !string.IsNullOrWhiteSpace(workspacePath)
            ? Path.Combine(workspacePath, ".claude", "settings.json")
            : Path.Combine(home, ".claude", "settings.json");

        MergeJsonFile(mcpFile, root =>
        {
            var servers = root["mcpServers"] as JObject ?? new JObject();
            servers["lore"] = JObject.FromObject(new { command = loreExe, args = new[] { "mcp", "--harness", "claude" } });
            root["mcpServers"] = servers;
        });

        MergeJsonFile(settingsFile, root =>
        {
            var hooks = root["hooks"] as JObject ?? new JObject();
            hooks["SessionStart"] = BuildClaudeHookGroup($"\"{loreExe}\" hook claude session-start");
            hooks["UserPromptSubmit"] = BuildClaudeHookGroup($"\"{loreExe}\" hook claude prompt-push");
            hooks["PreCompact"] = BuildClaudeHookGroup($"\"{loreExe}\" hook claude pre-compact");
            hooks["Stop"] = BuildClaudeHookGroup($"\"{loreExe}\" hook claude stop");
            root["hooks"] = hooks;
        });

        return $"{mcpFile} + {settingsFile}";
    }

    private static string ConfigureCodex(string loreExe, string? workspacePath, bool global)
    {
        var codexFile = !global && !string.IsNullOrWhiteSpace(workspacePath)
            ? Path.Combine(workspacePath, ".codex", "config.toml")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

        Directory.CreateDirectory(Path.GetDirectoryName(codexFile)!);
        var existing = File.Exists(codexFile) ? File.ReadAllText(codexFile) : string.Empty;

        const string startMarker = "# BEGIN LORE";
        const string endMarker = "# END LORE";
        var startIdx = existing.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx = existing.IndexOf(endMarker, StringComparison.Ordinal);
        if (startIdx >= 0 && endIdx > startIdx)
            existing = existing.Remove(startIdx, endIdx + endMarker.Length - startIdx).TrimEnd();

        var escapedExe = loreExe.Replace("\\", "\\\\");
        var block = $$"""

            {{startMarker}}
            [mcp_servers.lore]
            command = "{{escapedExe}}"
            args = ["mcp", "--harness", "codex"]

            [[hooks.SessionStart]]
            [[hooks.SessionStart.hooks]]
            type = "command"
            command = "\"{{escapedExe}}\" hook codex session-start"

            [[hooks.UserPromptSubmit]]
            [[hooks.UserPromptSubmit.hooks]]
            type = "command"
            command = "\"{{escapedExe}}\" hook codex prompt-push"

            [[hooks.Stop]]
            [[hooks.Stop.hooks]]
            type = "command"
            command = "\"{{escapedExe}}\" hook codex stop"
            {{endMarker}}
            """;

        File.WriteAllText(codexFile, (existing + Environment.NewLine + block).TrimStart());
        return codexFile;
    }

    private static JArray BuildClaudeHookGroup(string command) =>
        new(new JObject
        {
            ["matcher"] = "",
            ["hooks"] = new JArray(new JObject
            {
                ["type"] = "command",
                ["command"] = command
            })
        });

    private static void MergeJsonFile(string path, Action<JObject> mutator)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        mutator(root);
        File.WriteAllText(path, root.ToString(Formatting.Indented));
    }
}
