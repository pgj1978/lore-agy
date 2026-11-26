using System.Diagnostics;
using Lore.Core.Git;
using Xunit;

namespace Lore.Core.Tests;

public sealed class GitStalenessCheckerTests : IDisposable
{
    private readonly string _repoDir;

    public GitStalenessCheckerTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"lore-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoDir))
        {
            foreach (var f in Directory.GetFiles(_repoDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repoDir, true);
        }
    }

    [Theory]
    [InlineData("https://github.com/e-Path/Lore.git", "github.com/e-path/lore")]
    [InlineData("git@github.com:e-Path/Lore.git", "github.com/e-path/lore")]
    [InlineData("ssh://git@ssh.dev.azure.com:v3/org/proj/repo", "ssh.dev.azure.com/v3/org/proj/repo")]
    public void NormalizeRemoteUrl_ProducesConsistentRepoId(string input, string expected)
    {
        Assert.Equal(expected, GitStalenessChecker.NormalizeRemoteUrl(input));
    }

    [Fact]
    public void CheckStaleness_DetectsChangedMatchedFiles_AndIgnoresUnmatched()
    {
        RunGit("init");
        RunGit("config user.email test@example.com");
        RunGit("config user.name Test");
        RunGit("remote add origin https://github.com/example/sample-app.git");

        Directory.CreateDirectory(Path.Combine(_repoDir, "src", "ui"));
        Directory.CreateDirectory(Path.Combine(_repoDir, "docs"));
        File.WriteAllText(Path.Combine(_repoDir, "src", "ui", "Login.tsx"), "v1");
        File.WriteAllText(Path.Combine(_repoDir, "docs", "readme.md"), "v1");
        RunGit("add .");
        RunGit("commit -m init");

        var checker = new GitStalenessChecker();
        Assert.Equal("github.com/example/sample-app", checker.ResolveRepoId(_repoDir));
        var commit1 = checker.GetHeadCommit(_repoDir)!;

        // Change only docs -> ui glob should not be stale
        File.WriteAllText(Path.Combine(_repoDir, "docs", "readme.md"), "v2");
        RunGit("add .");
        RunGit("commit -m docs");

        var uiStaleAfterDocs = checker.CheckStaleness(_repoDir, commit1, ["src/ui/**"]);
        Assert.Null(uiStaleAfterDocs);

        // Change ui file -> ui glob should be flagged stale
        File.WriteAllText(Path.Combine(_repoDir, "src", "ui", "Login.tsx"), "v2");
        RunGit("add .");
        RunGit("commit -m ui");

        var uiStaleAfterUi = checker.CheckStaleness(_repoDir, commit1, ["src/ui/**"]);
        Assert.NotNull(uiStaleAfterUi);
        Assert.True(uiStaleAfterUi!.IsStale);
        Assert.Equal(1, uiStaleAfterUi.ChangedFileCount);
    }

    private void RunGit(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }
}
