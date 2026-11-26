using System.Text;

namespace Lore.Core.Models;

public static class MarkdownChunker
{
    public const int DefaultMaxChunkChars = 2400;

    public static IReadOnlyList<ChunkSpec> Chunk(MemoryItem item, int maxChunkChars = DefaultMaxChunkChars)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Type is MemoryType.Rule or MemoryType.Note)
        {
            var text = BuildSearchText(item.Title, null, item.Summary, item.Body);
            return [new ChunkSpec(0, item.Title, TrimSnippet(item.Body, 500), text)];
        }

        var sections = SplitByH2(item.Body);
        if (sections.Count == 0)
        {
            var text = BuildSearchText(item.Title, null, item.Summary, item.Body);
            return [new ChunkSpec(0, item.Title, TrimSnippet(item.Body, 500), text)];
        }

        var chunks = new List<ChunkSpec>();
        var idx = 0;

        foreach (var (heading, content) in sections)
        {
            var effectiveHeading = string.IsNullOrWhiteSpace(heading) ? "Overview" : heading.Trim();
            foreach (var part in SplitOversized(content, maxChunkChars))
            {
                var summaryPrefix = idx == 0 ? item.Summary : null;
                var searchText = BuildSearchText(item.Title, effectiveHeading, summaryPrefix, part);
                var snippet = $"[{effectiveHeading}] {TrimSnippet(part, 420)}";
                chunks.Add(new ChunkSpec(idx++, effectiveHeading, snippet, searchText));
            }
        }

        return chunks;
    }

    private static List<(string Heading, string Content)> SplitByH2(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var result = new List<(string Heading, string Content)>();
        var currentHeading = string.Empty;
        var sb = new StringBuilder();
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (sb.Length > 0)
                {
                    var text = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add((currentHeading, text));
                    sb.Clear();
                }

                currentHeading = line[3..].Trim();
                continue;
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            var tail = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail))
                result.Add((currentHeading, tail));
        }

        return result;
    }

    private static IEnumerable<string> SplitOversized(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        foreach (var para in paragraphs)
        {
            if (sb.Length > 0 && sb.Length + para.Length + 2 > maxChars)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }

            if (para.Length > maxChars)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }

                for (var i = 0; i < para.Length; i += maxChars)
                    yield return para.Substring(i, Math.Min(maxChars, para.Length - i)).Trim();
            }
            else
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(para);
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }

    private static string BuildSearchText(string title, string? heading, string? summary, string body)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title.Trim());
        if (!string.IsNullOrWhiteSpace(heading))
            sb.Append("## ").AppendLine(heading.Trim());
        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine(summary.Trim());
        sb.Append(body.Trim());
        return sb.ToString();
    }

    private static string TrimSnippet(string text, int maxLen)
    {
        var singleLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= maxLen ? singleLine : string.Concat(singleLine.AsSpan(0, maxLen - 3), "...");
    }
}

public sealed record ChunkSpec(int Index, string Heading, string Snippet, string SearchText);
