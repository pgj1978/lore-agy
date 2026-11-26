using System.Collections.Concurrent;
using System.Diagnostics;
using Lore.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Newtonsoft.Json.Linq;

namespace Lore.Core.Git;

public sealed class GitStalenessChecker
{
    private readonly ConcurrentDictionary<(string Cwd, string Commit, string Head), IReadOnlyList<string>> _diffCache = new();

    public string ResolveScope(string? cwd, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "repo", StringComparison.OrdinalIgnoreCase))
            return ResolveRepoId(cwd);

        var trimmed = scope.Trim();
        if (string.Equals(trimmed, "global", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "_global", StringComparison.OrdinalIgnoreCase))
        {
            return "_global";
        }

        if (trimmed.StartsWith("area:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("topic:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.ToLowerInvariant();
        }

        return $"area:{trimmed.ToLowerInvariant()}";
    }

    public string? ResolveWorktreeMainPath(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return null;

        var commonDir = RunGit(cwd, "rev-parse --path-format=absolute --git-common-dir");
        if (string.IsNullOrWhiteSpace(commonDir))
            return null;

        var trimmed = commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), ".git", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(trimmed);

        return trimmed;
    }

    public string ResolveRepoId(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return "_global";

        var overrideFile = Path.Combine(cwd, ".lore.json");
        if (File.Exists(overrideFile))
        {
            try
            {
                var obj = JObject.Parse(File.ReadAllText(overrideFile));
                var explicitId = obj["repoId"]?.ToString();
                if (!string.IsNullOrWhiteSpace(explicitId))
                    return explicitId.Trim().ToLowerInvariant();
            }
            catch
            {
                // Ignore malformed optional override
            }
        }

        var originUrl = RunGit(cwd, "remote get-url origin");
        if (!string.IsNullOrWhiteSpace(originUrl))
            return NormalizeRemoteUrl(originUrl);

        var root = RunGit(cwd, "rev-parse --show-toplevel");
        var folderName = Path.GetFileName((root ?? cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folderName) ? "_global" : $"local:{folderName.ToLowerInvariant()}";
    }

    public string? GetHeadCommit(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return null;

        var sha = RunGit(cwd, "rev-parse --short HEAD");
        return string.IsNullOrWhiteSpace(sha) ? null : sha.Trim();
    }

    public StalenessInfo? CheckStaleness(string? cwd, string? verifiedCommit, IReadOnlyList<string>? paths)
    {
        if (string.IsNullOrWhiteSpace(cwd) ||
            string.IsNullOrWhiteSpace(verifiedCommit) ||
            paths is null ||
            paths.Count == 0 ||
            !Directory.Exists(cwd))
        {
            return null;
        }

        var head = GetHeadCommit(cwd);
        if (string.IsNullOrWhiteSpace(head) || string.Equals(head, verifiedCommit, StringComparison.OrdinalIgnoreCase))
            return null;

        var changedFiles = _diffCache.GetOrAdd((cwd, verifiedCommit, head), key =>
        {
            var output = RunGit(key.Cwd, $"diff --name-only {key.Commit}..{key.Head}");
            if (string.IsNullOrWhiteSpace(output))
                return [];

            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        });

        if (changedFiles.Count == 0)
            return null;

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in paths)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
                matcher.AddInclude(pattern.Replace('\\', '/').TrimStart('/'));
        }

        var matchedCount = 0;
        foreach (var file in changedFiles)
        {
            var normalized = file.Replace('\\', '/').TrimStart('/');
            if (matcher.Match(normalized).HasMatches)
                matchedCount++;
        }

        if (matchedCount == 0)
            return null;

        return new StalenessInfo(
            true,
            matchedCount,
            $"possibly stale: {matchedCount} matched file(s) changed since {verifiedCommit}");
    }

    public static string NormalizeRemoteUrl(string raw)
    {
        var s = raw.Trim();
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s[8..];
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        else if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            s = s[6..];

        var atIdx = s.IndexOf('@');
        if (atIdx >= 0)
            s = s[(atIdx + 1)..];

        var colonIdx = s.IndexOf(':');
        if (colonIdx > 0 && !s.Contains("://", StringComparison.Ordinal))
            s = string.Concat(s.AsSpan(0, colonIdx), "/", s.AsSpan(colonIdx + 1));

        return s.Trim('/').ToLowerInvariant();
    }

    private static string? RunGit(string cwd, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = cwd,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!proc.Start())
                return null;

            proc.StandardInput.Close();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(1500))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            Task.WaitAll([outTask, errTask], 500);
            return proc.ExitCode == 0 ? outTask.GetAwaiter().GetResult().Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
