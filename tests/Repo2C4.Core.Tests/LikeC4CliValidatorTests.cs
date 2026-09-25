using Repo2C4.Core.LikeC4;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class LikeC4CliValidatorTests
{
    [Fact]
    public async Task MissingWorkspaceReturnsControlledFailure()
    {
        string missing = Path.Combine(Path.GetTempPath(), "repo2c4-missing-" + Guid.NewGuid().ToString("N"));

        LikeC4ValidationResult result = await LikeC4CliValidator.ValidateAsync(\n            missing,\n            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.False(result.TimedOut);
        Assert.Equal(LikeC4CliValidator.WorkspaceErrorExitCode, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "likec4.workspaceMissing");
    }

    [Fact]
    public async Task MissingCliReturnsControlledActionableFailureWithoutNode()
    {
        using TempWorkspace workspace = new();
        LikeC4CliValidationOptions options = new(
            "repo2c4-likec4-missing-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromSeconds(2));

        LikeC4ValidationResult result = await LikeC4CliValidator.ValidateAsync(\n            workspace.Path,\n            options,\n            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.False(result.TimedOut);
        Assert.Equal(LikeC4CliValidator.CliUnavailableExitCode, result.ExitCode);
        LikeC4ValidationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("likec4.cliUnavailable", diagnostic.Code);
        Assert.Contains("Install the documented pinned LikeC4 version", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfficialCliValidatesGeneratedFixturesAndRejectsInvalidSourcesWhenIntegrationIsEnabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        string goldenRoot = Path.Combine(AppContext.BaseDirectory, "LikeC4Golden");
        LikeC4ValidationResult c1 = await LikeC4CliValidator.ValidateAsync(\n            Path.Combine(goldenRoot, "acme-c1"),\n            cancellationToken: TestContext.Current.CancellationToken);
        LikeC4ValidationResult c2 = await LikeC4CliValidator.ValidateAsync(\n            Path.Combine(goldenRoot, "acme-c2"),\n            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(c1.IsValid, DiagnosticSummary(c1));
        Assert.Equal(0, c1.ExitCode);
        Assert.True(c2.IsValid, DiagnosticSummary(c2));
        Assert.Equal(0, c2.ExitCode);

        using TempWorkspace syntaxWorkspace = new();
        const string sentinel = "repo2c4-sensitive-sentinel";
        File.WriteAllText(
            Path.Combine(syntaxWorkspace.Path, "syntax-error.c4"),
            "specification {\n"
            + "  element softwareSystem\n"
            + "}\n"
            + "model {\n"
            + "  broken = softwareSystem \"" + sentinel + "\n"
            + "}\n");

        LikeC4ValidationResult syntaxResult = await LikeC4CliValidator.ValidateAsync(\n            syntaxWorkspace.Path,\n            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(syntaxResult.IsValid);
        Assert.NotEqual(0, syntaxResult.ExitCode);
        Assert.DoesNotContain(
            syntaxResult.Diagnostics,
            diagnostic => diagnostic.Message.Contains(sentinel, StringComparison.Ordinal));
        Assert.Contains(
            syntaxResult.Diagnostics,
            diagnostic => diagnostic.RelativePath == "syntax-error.c4"
                || diagnostic.Code == "likec4.validationFailed");

        using TempWorkspace referenceWorkspace = new();
        File.WriteAllText(
            Path.Combine(referenceWorkspace.Path, "invalid-reference.c4"),
            "specification {\n"
            + "  element softwareSystem\n"
            + "}\n"
            + "model {\n"
            + "  source = softwareSystem \"Source\"\n"
            + "  source -> missing \"Uses\"\n"
            + "}\n"
            + "views {\n"
            + "  view index {\n"
            + "    include *\n"
            + "  }\n"
            + "}\n");

        LikeC4ValidationResult referenceResult = await LikeC4CliValidator.ValidateAsync(\n            referenceWorkspace.Path,\n            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(referenceResult.IsValid);
        Assert.NotEqual(0, referenceResult.ExitCode);
        Assert.Contains(
            referenceResult.Diagnostics,
            diagnostic => diagnostic.RelativePath == "invalid-reference.c4"
                || diagnostic.Code == "likec4.validationFailed");
    }

    private static string DiagnosticSummary(LikeC4ValidationResult result) =>
        string.Join(
            "; ",
            result.Diagnostics.Select(
                diagnostic =>
                    diagnostic.Code + ": " + (diagnostic.RelativePath ?? "<workspace>") + " - " + diagnostic.Message));

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-likec4-").FullName;
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup for test-only temporary files.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup for test-only temporary files.
            }
        }
    }
}
