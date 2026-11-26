using Lore.Core.Models;

namespace Lore.Core.Extraction;

public enum TranscriptEntryKind
{
    User,
    Assistant,
    ToolCall,
    ToolResult
}

public sealed record TranscriptEntry(
    int StepIndex,
    TranscriptEntryKind Kind,
    string Content,
    string? ToolName = null,
    bool IsError = false);

public sealed record FilteredSlice(
    string Category,
    int StartStep,
    int EndStep,
    string Text);

public sealed record ExtractionCandidate(
    MemoryType Type,
    string Title,
    string Summary,
    string Body,
    string? Slug,
    List<string> Paths,
    List<string> Tags,
    MemoryAuthority Authority,
    string Scope = "repo");

public sealed record CaptureOutcome(
    string Action,
    string? MemoryId,
    string Title,
    string Reason);

public sealed record ExtractionReport(
    int TotalEntriesRead,
    int FilteredSliceCount,
    int CandidateCount,
    IReadOnlyList<CaptureOutcome> Outcomes);
