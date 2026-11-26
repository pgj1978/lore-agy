using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Lore.Core.Models;

[JsonConverter(typeof(StringEnumConverter))]
public enum MemoryType
{
    [EnumMember(Value = "rule")]
    Rule,

    [EnumMember(Value = "process")]
    Process,

    [EnumMember(Value = "app_ui")]
    AppUi,

    [EnumMember(Value = "note")]
    Note
}

[JsonConverter(typeof(StringEnumConverter))]
public enum MemoryAuthority
{
    [EnumMember(Value = "user")]
    User,

    [EnumMember(Value = "verified")]
    Verified,

    [EnumMember(Value = "inferred")]
    Inferred
}

[JsonConverter(typeof(StringEnumConverter))]
public enum MemoryStatus
{
    [EnumMember(Value = "active")]
    Active,

    [EnumMember(Value = "superseded")]
    Superseded,

    [EnumMember(Value = "archived")]
    Archived
}

public sealed class MemoryItem
{
    [JsonProperty("id")]
    public string Id { get; set; } = $"mem_{Guid.NewGuid():N}"[..20];

    [JsonProperty("repoId")]
    public string RepoId { get; set; } = "_global";

    [JsonProperty("type")]
    public MemoryType Type { get; set; } = MemoryType.Note;

    [JsonProperty("slug")]
    public string? Slug { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonProperty("body")]
    public string Body { get; set; } = string.Empty;

    [JsonProperty("authority")]
    public MemoryAuthority Authority { get; set; } = MemoryAuthority.Verified;

    [JsonProperty("status")]
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    [JsonProperty("supersededBy")]
    public string? SupersededBy { get; set; }

    [JsonProperty("paths")]
    public List<string> Paths { get; set; } = [];

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonProperty("sourceHarness")]
    public string SourceHarness { get; set; } = "cli";

    [JsonProperty("createdBy")]
    public string CreatedBy { get; set; } = Environment.UserName;

    [JsonProperty("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonProperty("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonProperty("verifiedAt")]
    public DateTimeOffset? VerifiedAt { get; set; }

    [JsonProperty("verifiedCommit")]
    public string? VerifiedCommit { get; set; }
}

public sealed class MemoryChunk
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("memoryId")]
    public string MemoryId { get; set; } = string.Empty;

    [JsonProperty("repoId")]
    public string RepoId { get; set; } = "_global";

    [JsonProperty("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonProperty("type")]
    public MemoryType Type { get; set; }

    [JsonProperty("slug")]
    public string? Slug { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("sectionHeading")]
    public string SectionHeading { get; set; } = string.Empty;

    [JsonProperty("snippet")]
    public string Snippet { get; set; } = string.Empty;

    [JsonProperty("searchText")]
    public string SearchText { get; set; } = string.Empty;

    [JsonProperty("embedding")]
    public float[] Embedding { get; set; } = [];

    [JsonProperty("authority")]
    public MemoryAuthority Authority { get; set; }

    [JsonProperty("status")]
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    [JsonProperty("paths")]
    public List<string> Paths { get; set; } = [];

    [JsonProperty("verifiedCommit")]
    public string? VerifiedCommit { get; set; }

    [JsonProperty("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RetrievalLog
{
    [JsonProperty("id")]
    public string Id { get; set; } = $"log_{Guid.NewGuid():N}"[..20];

    [JsonProperty("repoId")]
    public string RepoId { get; set; } = "_global";

    [JsonProperty("eventType")]
    public string EventType { get; set; } = "search";

    [JsonProperty("harness")]
    public string Harness { get; set; } = "cli";

    [JsonProperty("latencyMs")]
    public long LatencyMs { get; set; }

    [JsonProperty("hitCount")]
    public int HitCount { get; set; }

    [JsonProperty("topScore")]
    public double? TopScore { get; set; }

    [JsonProperty("injected")]
    public bool Injected { get; set; }

    [JsonProperty("detail")]
    public string? Detail { get; set; }

    [JsonProperty("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record StalenessInfo(bool IsStale, int ChangedFileCount, string? Message);

public sealed record SearchHit(
    string MemoryId,
    string RepoId,
    MemoryType Type,
    string? Slug,
    string Title,
    string SectionHeading,
    string Snippet,
    MemoryAuthority Authority,
    double VectorScore,
    List<string> Paths,
    string? VerifiedCommit,
    DateTimeOffset UpdatedAt,
    StalenessInfo? Staleness = null);

public sealed record BootstrapTocEntry(
    string Id,
    string RepoId,
    MemoryType Type,
    string? Slug,
    string Title,
    string Summary,
    List<string> Paths,
    string? VerifiedCommit);

public sealed record BootstrapPayload(
    string RepoId,
    DateTimeOffset FetchedAt,
    IReadOnlyList<MemoryItem> Rules,
    IReadOnlyList<BootstrapTocEntry> Toc);

public sealed record SaveMemoryRequest(
    string RepoId,
    MemoryType Type,
    string Title,
    string Body,
    string? Summary = null,
    string? Slug = null,
    List<string>? Paths = null,
    List<string>? Tags = null,
    MemoryAuthority Authority = MemoryAuthority.Verified,
    string SourceHarness = "cli",
    string? CreatedBy = null,
    string? VerifiedCommit = null,
    string? Id = null);

public sealed record SearchRequest(
    string RepoId,
    string Query,
    MemoryType? Type = null,
    int Limit = 5,
    string? Cwd = null,
    string Harness = "cli");

public sealed record PromptRecallRequest(
    string RepoId,
    string Prompt,
    IReadOnlyList<string>? AlreadyInjectedIds = null,
    double? MinScore = null,
    int Limit = 3,
    string? Cwd = null,
    string Harness = "cli");

public sealed record StatsSummary(
    string RepoId,
    int TotalActiveMemories,
    Dictionary<string, int> CountsByType,
    int SearchCount,
    int PromptPushInjectedCount,
    int PromptPushSkippedCount,
    double AvgSearchLatencyMs,
    int CaptureCreatedCount,
    int CaptureMergedCount,
    int CaptureSupersededCount,
    int CaptureSkippedCount);
