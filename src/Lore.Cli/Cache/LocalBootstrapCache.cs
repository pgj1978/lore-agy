using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lore.Core.Config;
using Lore.Core.Models;

namespace Lore.Cli.Cache;

public sealed class LocalBootstrapCache
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _rootDir;

    public LocalBootstrapCache(string? rootDir = null)
    {
        _rootDir = rootDir ?? LoreConfig.GetLocalDataDirectory();
    }

    public BootstrapPayload? TryGetBootstrap(string repoId, TimeSpan ttl)
    {
        var path = GetBootstrapPath(repoId);
        if (!File.Exists(path)) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<BootstrapPayload>(File.ReadAllText(path), JsonOpts);
            if (payload is null) return null;
            if (DateTimeOffset.UtcNow - payload.FetchedAt > ttl) return null;
            return payload;
        }
        catch
        {
            return null;
        }
    }

    public void SaveBootstrap(BootstrapPayload payload)
    {
        var path = GetBootstrapPath(payload.RepoId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts));
    }

    public void InvalidateBootstrap(string repoId)
    {
        var path = GetBootstrapPath(repoId);
        if (File.Exists(path))
            try { File.Delete(path); } catch { }
    }

    public SessionState LoadSessionState(string sessionId)
    {
        var path = GetSessionStatePath(sessionId);
        if (!File.Exists(path)) return new SessionState { SessionId = sessionId };

        try
        {
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), JsonOpts)
                   ?? new SessionState { SessionId = sessionId };
        }
        catch
        {
            return new SessionState { SessionId = sessionId };
        }
    }

    public void SaveSessionState(SessionState state)
    {
        var path = GetSessionStatePath(state.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
    }

    public static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16];
    }

    private string GetBootstrapPath(string repoId)
        => Path.Combine(_rootDir, "cache", $"{ComputeHash(repoId)}.json");

    private string GetSessionStatePath(string sessionId)
        => Path.Combine(_rootDir, "state", $"{ComputeHash(sessionId)}.json");
}

public sealed class SessionState
{
    public string SessionId { get; set; } = string.Empty;
    public string? LastPromptHash { get; set; }
    public HashSet<string> InjectedMemoryIds { get; set; } = [];
    public int LastCapturedStep { get; set; } = -1;
}
