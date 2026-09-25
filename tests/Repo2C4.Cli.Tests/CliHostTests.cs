using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class CliHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpUsesStandardOutput(string argument)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run([argument], output, error);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Repo2C4 CLI", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("inspect", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("generate", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("validate", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void MissingCommandReturnsUsageError()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run([], output, error);

        Assert.Equal(CliExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("command is required", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectProducesDeterministicCanonicalSnapshotForFixture()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "library-only");
        using TempDirectory temp = new();
        string first = Path.Combine(temp.Path, "first.json");
        string second = Path.Combine(temp.Path, "second.json");

        int firstExit = Run(["inspect", "--repository", fixture, "--output", first]);
        int secondExit = Run(["inspect", "--repository", fixture, "--output", second]);

        Assert.Equal(CliExitCodes.Success, firstExit);
        Assert.Equal(CliExitCodes.Success, secondExit);
        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));

        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(first));
        Assert.Equal("repo_dc7f1e8bfb906a72d6b45f77", snapshot.RepositoryId);
        Assert.Single(snapshot.Files);
        Assert.Equal(3, snapshot.Evidence.Length);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void GeneratePreviewsThenAppliesManagedFiles()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "EndToEnd", "architecture.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int previewExit = Run(["generate", "--model", model, "--output", outputDirectory]);

        Assert.Equal(CliExitCodes.Success, previewExit);
        Assert.False(Directory.Exists(outputDirectory));

        int applyExit = Run(["generate", "--model", model, "--output", outputDirectory, "--apply"]);

        Assert.Equal(CliExitCodes.Success, applyExit);
        string specification = Path.Combine(outputDirectory, "specification.c4");
        string generatedModel = Path.Combine(outputDirectory, "model.c4");
        string views = Path.Combine(outputDirectory, "views.c4");
        string report = Path.Combine(outputDirectory, "evidence-report.md");
        string manifest = Path.Combine(outputDirectory, ".repo2c4-manifest.json");
        Assert.True(File.Exists(specification));
        Assert.True(File.Exists(generatedModel));
        Assert.True(File.Exists(views));
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(manifest));

        string before = File.ReadAllText(generatedModel);
        int idempotent = Run(["generate", "--model", model, "--output", outputDirectory]);
        Assert.Equal(CliExitCodes.Success, idempotent);
        Assert.Equal(before, File.ReadAllText(generatedModel));

        File.AppendAllText(generatedModel, "// manual edit");
        int conflict = Run(["generate", "--model", model, "--output", outputDirectory, "--apply"]);
        Assert.Equal(CliExitCodes.IoError, conflict);
        Assert.EndsWith("// manual edit", File.ReadAllText(generatedModel), StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateSelectedC3WritesOnlyRequestedComponentView()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "acme.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int exitCode = Run([
            "generate",
            "--model", model,
            "--output", outputDirectory,
            "--c3-container", "el_web",
            "--apply",
        ]);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("component", File.ReadAllText(Path.Combine(outputDirectory, "model.c4")), StringComparison.Ordinal);
        string c3View = File.ReadAllText(Path.Combine(outputDirectory, "c3.views.c4"));
        Assert.Contains("C3 - Web API", c3View, StringComparison.Ordinal);
        Assert.DoesNotContain("el_worker", c3View, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateInvalidModelUsesDistinctInvalidDataExitCode()
    {
        using TempDirectory temp = new();
        string model = Path.Combine(temp.Path, "invalid.json");
        File.WriteAllText(model, "{}");

        int exitCode = Run(["generate", "--model", model, "--output", Path.Combine(temp.Path, "out")]);

        Assert.Equal(CliExitCodes.InvalidData, exitCode);
        Assert.NotEqual(CliExitCodes.UsageError, exitCode);
        Assert.NotEqual(CliExitCodes.ValidationFailed, exitCode);
    }

    [Fact]
    public void ValidateMissingRequiredOptionReturnsUsageError()
    {
        int exitCode = Run(["validate"]);

        Assert.Equal(CliExitCodes.UsageError, exitCode);
    }

    [Fact]
    public void FullGenerateAndValidateCycleUsesOfficialCliWhenIntegrationIsEnabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        string model = Path.Combine(AppContext.BaseDirectory, "EndToEnd", "architecture.c1.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        Assert.Equal(
            CliExitCodes.Success,
            Run(["generate", "--model", model, "--output", outputDirectory]));
        Assert.Equal(
            CliExitCodes.Success,
            Run(["validate", "--output", outputDirectory]));
    }

    private static int Run(string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        return Program.Run(args, output, error);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-cli-").FullName;
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
