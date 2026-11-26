using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace Lore.Core.Config;

public sealed class LoreProfileConfig
{
    public string? ApiUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? CosmosEndpoint { get; set; }
    public string? CosmosDatabase { get; set; }
    public string? AzureOpenAiEndpoint { get; set; }
    public string? AzureOpenAiEmbeddingDeployment { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? AnthropicModel { get; set; }
    public List<string>? AllowedHarnesses { get; set; }
    public double? PromptRecallMinScore { get; set; }
    public int? HookTimeoutMs { get; set; }
    public int? BootstrapCacheMinutes { get; set; }
}

public sealed class LoreConfig
{
    public string DefaultProfile { get; set; } = "default";
    public Dictionary<string, LoreProfileConfig>? Profiles { get; set; }
    public Dictionary<string, string>? PathRoutes { get; set; }
    public List<string>? AllowedHarnesses { get; set; }

    public string? ApiUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? CosmosEndpoint { get; set; }
    public string CosmosDatabase { get; set; } = "lore";
    public string? AzureOpenAiEndpoint { get; set; }
    public string AzureOpenAiEmbeddingDeployment { get; set; } = "text-embedding-3-large";
    public int EmbeddingDimensions { get; set; } = 1536;
    public string? AnthropicApiKey { get; set; }
    public string AnthropicModel { get; set; } = "claude-haiku-5-5";
    public string? UserName { get; set; }
    public double PromptRecallMinScore { get; set; } = 0.45;
    public int HookTimeoutMs { get; set; } = 10000;
    public int BootstrapCacheMinutes { get; set; } = 5;

    [JsonIgnore]
    public string ActiveProfileName { get; set; } = "default";

    [JsonIgnore]
    public bool IsApiMode => !string.IsNullOrWhiteSpace(ApiUrl);

    public bool IsHarnessAllowed(string? harness)
    {
        if (AllowedHarnesses is null || AllowedHarnesses.Count == 0 || string.IsNullOrWhiteSpace(harness))
            return true;

        var normalized = NormalizeHarness(harness);
        return AllowedHarnesses.Any(h => string.Equals(NormalizeHarness(h), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public string GetProfileDataDirectory()
        => Path.Combine(GetLocalDataDirectory(), "profiles", SanitizeProfileName(ActiveProfileName));

    public static string GetLoreHomeDirectory()
    {
        var overrideHome = Environment.GetEnvironmentVariable("LORE_HOME");
        if (!string.IsNullOrWhiteSpace(overrideHome))
            return overrideHome;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".lore");
    }

    public static string GetConfigPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("LORE_CONFIG");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(GetLoreHomeDirectory(), "config.json");
    }

    public static string GetSecretsPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("LORE_SECRETS");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(GetLoreHomeDirectory(), "secrets.json");
    }

    public static string GetLocalDataDirectory()
    {
        var overrideData = Environment.GetEnvironmentVariable("LORE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideData))
            return overrideData;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(GetLoreHomeDirectory(), "data")
            : Path.Combine(localAppData, "lore");
    }

    public static LoreConfig Load() => LoadForDirectory(null, null, null);

    public static LoreConfig LoadForDirectory(
        string? cwd,
        string? explicitProfile = null,
        string? worktreeMainPath = null)
    {
        var config = new LoreConfig();
        LoreSecrets? secrets = null;

        var configPath = GetConfigPath();
        if (File.Exists(configPath))
        {
            var fileCfg = JsonSerializer.Deserialize<LoreConfig>(File.ReadAllText(configPath), JsonDefaults);
            if (fileCfg is not null) config = fileCfg;
        }

        var secretsPath = GetSecretsPath();
        if (File.Exists(secretsPath))
            secrets = JsonSerializer.Deserialize<LoreSecrets>(File.ReadAllText(secretsPath), JsonDefaults);

        if (secrets is not null)
        {
            if (!string.IsNullOrWhiteSpace(secrets.AnthropicApiKey))
                config.AnthropicApiKey = secrets.AnthropicApiKey;
            if (!string.IsNullOrWhiteSpace(secrets.ApiKey))
                config.ApiKey = secrets.ApiKey;
        }

        var profileName = ResolveProfileName(config, cwd, worktreeMainPath, explicitProfile);
        config.ActiveProfileName = profileName;

        if (config.Profiles is not null &&
            TryGetCaseInsensitive(config.Profiles, profileName, out var profileCfg) &&
            profileCfg is not null)
        {
            ApplyProfile(config, profileCfg);
        }

        if (secrets?.Profiles is not null &&
            TryGetCaseInsensitive(secrets.Profiles, profileName, out var profileSecrets) &&
            profileSecrets is not null)
        {
            if (!string.IsNullOrWhiteSpace(profileSecrets.AnthropicApiKey))
                config.AnthropicApiKey = profileSecrets.AnthropicApiKey;
            if (!string.IsNullOrWhiteSpace(profileSecrets.ApiKey))
                config.ApiKey = profileSecrets.ApiKey;
        }

        ApplyEnv(ref config);
        config.UserName ??= Environment.UserName;
        return config;
    }

    public void SaveConfig()
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonDefaults);
        File.WriteAllText(path, json);
    }

    public static TokenCredential CreateAzureCredential()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
        {
            return new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        }

        return new AzureCliCredential();
    }

    internal static string ResolveProfileName(
        LoreConfig config,
        string? cwd,
        string? worktreeMainPath,
        string? explicitProfile)
    {
        var envProfile = Environment.GetEnvironmentVariable("LORE_PROFILE");
        if (!string.IsNullOrWhiteSpace(explicitProfile))
            return explicitProfile.Trim();
        if (!string.IsNullOrWhiteSpace(envProfile))
            return envProfile.Trim();

        if (config.PathRoutes is { Count: > 0 })
        {
            var matched = MatchLongestRoute(config.PathRoutes, cwd)
                          ?? MatchLongestRoute(config.PathRoutes, worktreeMainPath);
            if (!string.IsNullOrWhiteSpace(matched))
                return matched;
        }

        return string.IsNullOrWhiteSpace(config.DefaultProfile) ? "default" : config.DefaultProfile.Trim();
    }

    private static string? MatchLongestRoute(Dictionary<string, string> routes, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalizedTarget = NormalizeDirPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string? bestProfile = null;
        var bestLen = -1;

        foreach (var (rawPrefix, profile) in routes)
        {
            if (string.IsNullOrWhiteSpace(rawPrefix) || string.IsNullOrWhiteSpace(profile))
                continue;

            var prefix = NormalizeDirPath(rawPrefix);
            var isExact = string.Equals(normalizedTarget, prefix, comparison);
            var isChild = normalizedTarget.StartsWith(prefix + Path.DirectorySeparatorChar, comparison);

            if ((isExact || isChild) && prefix.Length > bestLen)
            {
                bestLen = prefix.Length;
                bestProfile = profile.Trim();
            }
        }

        return bestProfile;
    }

    private static string NormalizeDirPath(string path)
    {
        var expanded = path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

        var full = Path.GetFullPath(expanded.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ApplyProfile(LoreConfig target, LoreProfileConfig p)
    {
        if (p.ApiUrl is not null) target.ApiUrl = p.ApiUrl;
        if (p.ApiKey is not null) target.ApiKey = p.ApiKey;
        if (p.CosmosEndpoint is not null) target.CosmosEndpoint = p.CosmosEndpoint;
        if (!string.IsNullOrWhiteSpace(p.CosmosDatabase)) target.CosmosDatabase = p.CosmosDatabase;
        if (p.AzureOpenAiEndpoint is not null) target.AzureOpenAiEndpoint = p.AzureOpenAiEndpoint;
        if (!string.IsNullOrWhiteSpace(p.AzureOpenAiEmbeddingDeployment)) target.AzureOpenAiEmbeddingDeployment = p.AzureOpenAiEmbeddingDeployment;
        if (p.EmbeddingDimensions.HasValue) target.EmbeddingDimensions = p.EmbeddingDimensions.Value;
        if (p.AnthropicApiKey is not null) target.AnthropicApiKey = p.AnthropicApiKey;
        if (!string.IsNullOrWhiteSpace(p.AnthropicModel)) target.AnthropicModel = p.AnthropicModel;
        if (p.AllowedHarnesses is not null) target.AllowedHarnesses = p.AllowedHarnesses;
        if (p.PromptRecallMinScore.HasValue) target.PromptRecallMinScore = p.PromptRecallMinScore.Value;
        if (p.HookTimeoutMs.HasValue) target.HookTimeoutMs = p.HookTimeoutMs.Value;
        if (p.BootstrapCacheMinutes.HasValue) target.BootstrapCacheMinutes = p.BootstrapCacheMinutes.Value;
    }

    private static bool TryGetCaseInsensitive<T>(Dictionary<string, T> dict, string key, out T? value)
    {
        if (dict.TryGetValue(key, out value))
            return true;

        foreach (var kv in dict)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeHarness(string harness) => harness.Trim().ToLowerInvariant() switch
    {
        "claude-code" => "claude",
        var other => other
    };

    private static string SanitizeProfileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToLowerInvariant().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "default" : new string(chars);
    }

    private static void ApplyEnv(ref LoreConfig c)
    {
        c.ApiUrl = EnvOr("LORE_API_URL", c.ApiUrl);
        c.ApiKey = EnvOr("LORE_API_KEY", c.ApiKey);
        c.CosmosEndpoint = EnvOr("LORE_COSMOS_ENDPOINT", c.CosmosEndpoint);
        c.CosmosDatabase = EnvOr("LORE_COSMOS_DATABASE", c.CosmosDatabase)!;
        c.AzureOpenAiEndpoint = EnvOr("LORE_AOAI_ENDPOINT", c.AzureOpenAiEndpoint);
        c.AzureOpenAiEmbeddingDeployment = EnvOr("LORE_AOAI_DEPLOYMENT", c.AzureOpenAiEmbeddingDeployment)!;
        c.AnthropicApiKey = EnvOr("ANTHROPIC_API_KEY", c.AnthropicApiKey);
        c.AnthropicModel = EnvOr("LORE_ANTHROPIC_MODEL", c.AnthropicModel)!;
        c.UserName = EnvOr("LORE_USER", c.UserName);
    }

    private static string? EnvOr(string key, string? current)
    {
        var val = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(val) ? current : val.Trim();
    }

    private static readonly JsonSerializerOptions JsonDefaults = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class LoreSecrets
    {
        public string? AnthropicApiKey { get; set; }
        public string? ApiKey { get; set; }
        public Dictionary<string, LoreProfileSecrets>? Profiles { get; set; }
    }

    private sealed class LoreProfileSecrets
    {
        public string? AnthropicApiKey { get; set; }
        public string? ApiKey { get; set; }
    }
}
