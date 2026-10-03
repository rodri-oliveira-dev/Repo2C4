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
    public void SnapshotBudgetTruncatesSemanticFactsToRemainConsumableByCli()
    {
        const string projectPath = "src/App/App.csproj";
        const string sourcePath = "src/App/Service.cs";
        SemanticC3Fact[] facts =
        [
            .. Enumerable.Range(0, 5_000).Select(index =>
            {
                string symbolId = "M:Demo.Service.Method" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "()";
                SemanticC3SourceSymbolIdentity symbol = new(
                    StableIds.ForSemanticC3SourceSymbol(projectPath, symbolId),
                    projectPath,
                    symbolId);
                return new SemanticC3Fact(
                    StableIds.ForSemanticC3Fact(
                        projectPath,
                        sourcePath,
                        symbolId,
                        SemanticC3FactKind.MethodDeclaration,
                        "semantic.test",
                        string.Empty),
                    projectPath,
                    sourcePath,
                    1,
                    symbol,
                    SemanticC3FactKind.MethodDeclaration,
                    "semantic.test",
                    new string('x', 512),
                    null);
            }),
        ];
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_budget",
            [
                new RepositoryFile(projectPath, 100, null),
                new RepositoryFile(sourcePath, 100, null),
            ],
            [],
            [])
        {
            SemanticC3Facts = new SemanticC3FactSet(
                SemanticC3ContractSchema.Version,
                [.. facts],
                []),
        };

        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(ContractJson.SerializeSnapshot(snapshot) + "\n")
            > CliApplication.MaxModelBytes);

        RepositorySnapshot bounded = CliApplication.FitSnapshotToCliInputBudget(snapshot);
        string json = ContractJson.SerializeSnapshot(bounded) + "\n";

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= CliApplication.MaxModelBytes);
        Assert.NotNull(bounded.SemanticC3Facts);
        Assert.True(bounded.SemanticC3Facts.Facts.Length < facts.Length);
        Assert.Contains(
            bounded.SemanticC3Facts.Diagnostics,
            diagnostic => diagnostic.Code == "semanticC3.snapshotByteLimit");
        Assert.Equal(json.TrimEnd('\n'), ContractJson.SerializeSnapshot(ContractJson.DeserializeSnapshot(json)));
    }

    [Fact]
    public void InspectImportsExternalEvidenceAndKeepsOutputPathOnStdout()
    {
        using TempDirectory temp = new();
        string repository = CreateIntegrationRepository(temp.Path);
        string snapshotPath = Path.Combine(temp.Path, "snapshot.json");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run(
            [
                "inspect", "--repository", repository,
                "--integration-report", Path.Combine(repository, "inspection.json"),
                "--output", snapshotPath,
            ],
            output,
            error);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(snapshotPath + Environment.NewLine, output.ToString());
        Assert.Contains("Imported external integration evidence: 1.", error.ToString(), StringComparison.Ordinal);
        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(snapshotPath));
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.http.outbound");
    }

    [Fact]
    public void InspectImportsCompleteExternalIntegrationFixtureDeterministically()
    {
        string repository = Path.Combine(AppContext.BaseDirectory, "ExternalIntegrationE2E");
        using TempDirectory temp = new();
        string first = Path.Combine(temp.Path, "first.json");
        string second = Path.Combine(temp.Path, "second.json");

        int firstExit = Run(
            ["inspect", "--repository", repository, "--integration-report", "inspection-v1.6.json", "--output", first]);
        int secondExit = Run(
            ["inspect", "--repository", repository, "--integration-report", "inspection-v1.6.json", "--output", second]);

        Assert.Equal(CliExitCodes.Success, firstExit);
        Assert.Equal(CliExitCodes.Success, secondExit);
        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(first));
        Assert.Equal(9, snapshot.Evidence.Count(item => item.Category.StartsWith("external.", StringComparison.Ordinal)));
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.messaging.publish");
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.messaging.consume");
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.database");
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.cache");
        Assert.Contains(snapshot.Evidence, item => item.Category == "external.storage");
    }

    [Fact]
    public void InspectRejectsIncompatibleExternalReportWithoutWritingSnapshot()
    {
        using TempDirectory temp = new();
        string repository = CreateIntegrationRepository(temp.Path);
        string reportPath = Path.Combine(repository, "inspection.json");
        File.WriteAllText(
            reportPath,
            File.ReadAllText(reportPath).Replace("\"1.6\"", "\"2.0\"", StringComparison.Ordinal));
        string snapshotPath = Path.Combine(temp.Path, "snapshot.json");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run(
            ["inspect", "--repository", repository, "--integration-report", reportPath, "--output", snapshotPath],
            output,
            error);

        Assert.Equal(CliExitCodes.InvalidData, exitCode);
        Assert.Contains("external.schema.incompatible", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void InspectRejectsExternalReportOutsideRepository()
    {
        using TempDirectory temp = new();
        string repository = CreateIntegrationRepository(temp.Path);
        string outsideReport = Path.Combine(temp.Path, "outside.json");
        File.Copy(Path.Combine(repository, "inspection.json"), outsideReport);
        string snapshotPath = Path.Combine(temp.Path, "snapshot.json");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run(
            ["inspect", "--repository", repository, "--integration-report", outsideReport, "--output", snapshotPath],
            output,
            error);

        Assert.Equal(CliExitCodes.IoError, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void GeneratePreviewsThenAppliesManagedFiles()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "EndToEnd", "architecture.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int previewExit = Run(["generate", "--model", model, "--output", outputDirectory]);

        Assert.Equal(CliExitCodes.Success, previewExit);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "model.c4")));

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
    public void GenerateRepeatedC3ContainerOptionsCreateOneCanonicalMultiViewWorkspace()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "multi-c3.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int exitCode = Run([
            "generate",
            "--model", model,
            "--output", outputDirectory,
            "--c3-container", "el_delta",
            "--c3-container", "el_alpha",
            "--c3-container", "el_beta",
            "--c3-container", "el_alpha",
            "--apply",
        ]);

        Assert.Equal(CliExitCodes.Success, exitCode);
        string generatedModel = File.ReadAllText(Path.Combine(outputDirectory, "model.c4"));
        Assert.Equal(3, generatedModel.Split(" = component ", StringSplitOptions.None).Length - 1);

        string views = File.ReadAllText(Path.Combine(outputDirectory, "c3.views.c4"));
        Assert.Contains("view c3_el_alpha {", views, StringComparison.Ordinal);
        Assert.Contains("view c3_el_beta {", views, StringComparison.Ordinal);
        Assert.Contains("view c3_el_delta {", views, StringComparison.Ordinal);
        Assert.DoesNotContain("c3_el_gamma", views, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateMultiC3IsDeterministicAcrossArgumentOrderAndDuplicates()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "multi-c3.c2.v1.json");
        using TempDirectory first = new();
        using TempDirectory second = new();
        string firstOutput = Path.Combine(first.Path, "likec4");
        string secondOutput = Path.Combine(second.Path, "likec4");

        Assert.Equal(
            CliExitCodes.Success,
            Run([
                "generate", "--model", model, "--output", firstOutput,
                "--c3-container", "el_alpha",
                "--c3-container", "el_beta",
                "--c3-container", "el_gamma",
                "--apply",
            ]));
        Assert.Equal(
            CliExitCodes.Success,
            Run([
                "generate", "--model", model, "--output", secondOutput,
                "--c3-container", "el_gamma",
                "--c3-container", "el_alpha",
                "--c3-container", "el_alpha",
                "--c3-container", "el_beta",
                "--apply",
            ]));

        Assert.Equal(
            File.ReadAllText(Path.Combine(firstOutput, "model.c4")),
            File.ReadAllText(Path.Combine(secondOutput, "model.c4")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(firstOutput, "c3.views.c4")),
            File.ReadAllText(Path.Combine(secondOutput, "c3.views.c4")));
    }

    [Theory]
    [InlineData("el_missing")]
    [InlineData("el_suite")]
    [InlineData("el_user")]
    public void GenerateInvalidC3SelectionFailsBeforeWriting(string selection)
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "multi-c3.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int exitCode = Run([
            "generate",
            "--model", model,
            "--output", outputDirectory,
            "--c3-container", selection,
            "--apply",
        ]);

        Assert.Equal(CliExitCodes.InvalidData, exitCode);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void GenerateRejectsC3SelectionForC1BeforeWriting()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "EndToEnd", "architecture.c1.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        int exitCode = Run([
            "generate",
            "--model", model,
            "--output", outputDirectory,
            "--c3-container", "el_library_repo",
            "--apply",
        ]);

        Assert.Equal(CliExitCodes.InvalidData, exitCode);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void ManagedRegenerationUpdatesSelectionButProtectsManualMultiC3Edits()
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "multi-c3.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        Assert.Equal(
            CliExitCodes.Success,
            Run([
                "generate", "--model", model, "--output", outputDirectory,
                "--c3-container", "el_alpha",
                "--apply",
            ]));

        Assert.Equal(
            CliExitCodes.Success,
            Run([
                "generate", "--model", model, "--output", outputDirectory,
                "--c3-container", "el_alpha",
                "--c3-container", "el_beta",
                "--apply",
            ]));

        string c3Views = Path.Combine(outputDirectory, "c3.views.c4");
        Assert.Contains("c3_el_beta", File.ReadAllText(c3Views), StringComparison.Ordinal);

        File.AppendAllText(c3Views, "// manual multi-c3 edit");
        int conflict = Run([
            "generate", "--model", model, "--output", outputDirectory,
            "--c3-container", "el_alpha",
            "--apply",
        ]);

        Assert.Equal(CliExitCodes.IoError, conflict);
        Assert.EndsWith(
            "// manual multi-c3 edit",
            File.ReadAllText(c3Views),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultiC3GenerateAndValidateUsesOfficialLikeC4WhenIntegrationIsEnabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        string model = Path.Combine(AppContext.BaseDirectory, "Models", "multi-c3.c2.v1.json");
        using TempDirectory temp = new();
        string outputDirectory = Path.Combine(temp.Path, "likec4");

        Assert.Equal(
            CliExitCodes.Success,
            Run([
                "generate", "--model", model, "--output", outputDirectory,
                "--c3-container", "el_alpha",
                "--c3-container", "el_beta",
                "--apply",
            ]));
        Assert.Equal(
            CliExitCodes.Success,
            Run(["validate", "--output", outputDirectory]));
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
            Run(["generate", "--model", model, "--output", outputDirectory, "--apply"]));
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

    private static string CreateIntegrationRepository(string root)
    {
        string repository = Path.Combine(root, "repository");
        string project = Path.Combine(repository, "src", "App");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "SerasaClient.cs"), "internal sealed class SerasaClient { }");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "ExternalFixtures", "dotnetrepoinspector-v1.6.5-canonical.json"),
            Path.Combine(repository, "inspection.json"));
        return repository;
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
