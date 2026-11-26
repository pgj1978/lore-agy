using Lore.Core.Models;
using Xunit;

namespace Lore.Core.Tests;

public sealed class MarkdownChunkerTests
{
    [Fact]
    public void RuleAndNote_ProduceSingleChunk()
    {
        var rule = new MemoryItem
        {
            Type = MemoryType.Rule,
            Title = "Never prefix RG with rg",
            Summary = "Azure resource groups must not start with rg.",
            Body = "Always name resource groups like lore-agy-dev."
        };

        var chunks = MarkdownChunker.Chunk(rule);

        Assert.Single(chunks);
        Assert.Contains("Never prefix RG with rg", chunks[0].SearchText);
        Assert.Contains("lore-agy-dev", chunks[0].SearchText);
    }

    [Fact]
    public void ProcessAndAppUi_SplitByH2Headings_WithTitlePrefix()
    {
        var doc = new MemoryItem
        {
            Type = MemoryType.AppUi,
            Title = "Invoice Approval Screen",
            Summary = "Approval modal and role gates",
            Body = """
                ## Entry Point
                Navigate to /invoices/:id.

                ## Visible UI & States
                Shows Approve button only for FinanceAdmin.

                ## Flow
                Click Approve -> confirmation modal -> POST /api/invoices/approve.
                """
        };

        var chunks = MarkdownChunker.Chunk(doc);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Entry Point", chunks[0].Heading);
        Assert.Contains("# Invoice Approval Screen", chunks[0].SearchText);
        Assert.Contains("Approval modal and role gates", chunks[0].SearchText);
        Assert.Equal("Visible UI & States", chunks[1].Heading);
        Assert.Equal("Flow", chunks[2].Heading);
        Assert.StartsWith("[Flow]", chunks[2].Snippet);
    }

    [Fact]
    public void OversizedSection_SplitsAtParagraphBoundary()
    {
        var para = new string('a', 1500);
        var doc = new MemoryItem
        {
            Type = MemoryType.Process,
            Title = "Long Deploy",
            Body = $"## Steps\n{para}\n\n{para}"
        };

        var chunks = MarkdownChunker.Chunk(doc, maxChunkChars: 2000);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal("Steps", c.Heading));
    }
}
