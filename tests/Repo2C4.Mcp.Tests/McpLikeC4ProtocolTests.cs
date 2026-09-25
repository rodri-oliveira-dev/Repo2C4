using System.Diagnostics;
using System.Text.Json;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpLikeC4ProtocolTests
{
    [Fact]
    public async Task ProtocolInspectsPreviewsValidatesAndWritesProtectedLikeC4()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempRepository repository = new();
        File.WriteAllText(
            Path.Combine(repository.Path, "OnlyLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        using Process process = StartServer(repository.Path);
        await using StreamWriter input = process.StandardInput;
        input.AutoFlush = true;
        input.NewLine = "\n";
        await InitializeAsync(process, input, cancellationToken);

        await WriteToolCallAsync(
            input,
            2,
            "inspect_repository",
            new
            {
                repositoryPath = ".",
            });
        using JsonDocument inspectResponse = await ReadResponseAsync(process, cancellationToken);
        JsonElement inspect = StructuredResult(inspectResponse);
        string snapshotId = inspect.GetProperty("snapshotId").GetString()!;
        string repositoryId = inspect.GetProperty("repositoryId").GetString()!;

        RepositorySnapshot snapshot = RepositoryFactExtractor.Extract(
            new RepositoryScanOptions(repository.Path, repositoryId),
            cancellationToken);
        ArchitectureModel model = CreateModel(snapshot);
        using JsonDocument modelDocument = JsonDocument.Parse(ContractJson.SerializeModel(model));
        JsonElement modelJson = modelDocument.RootElement.Clone();

        await WriteToolCallAsync(
            input,
            3,
            "generate_likec4",
            new
            {
                snapshotId,
                model = modelJson,
                destinationPath = "generated",
            });
        using JsonDocument previewResponse = await ReadResponseAsync(process, cancellationToken);
        JsonElement preview = StructuredResult(previewResponse);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.False(preview.GetProperty("written").GetBoolean());
        Assert.Equal(3, preview.GetProperty("files").GetArrayLength());
        Assert.False(Directory.Exists(Path.Combine(repository.Path, "generated")));

        await WriteToolCallAsync(
            input,
            4,
            "generate_likec4",
            new
            {
                snapshotId,
                model = modelJson,
                dryRun = false,
                write = false,
                destinationPath = "generated",
            });
        using JsonDocument disabledWriteResponse = await ReadResponseAsync(process, cancellationToken);
        AssertToolError(disabledWriteResponse, "write_not_authorized");

        await WriteToolCallAsync(
            input,
            5,
            "generate_likec4",
            new
            {
                snapshotId,
                model = modelJson,
                dryRun = false,
                write = true,
                destinationPath = "../outside",
            });
        using JsonDocument traversalResponse = await ReadResponseAsync(process, cancellationToken);
        AssertToolError(traversalResponse, "destination_invalid");

        if (string.Equals(
                Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            await WriteToolCallAsync(
                input,
                6,
                "validate_likec4",
                new
                {
                    snapshotId,
                    model = modelJson,
                });
            using JsonDocument validateProposedResponse = await ReadResponseAsync(process, cancellationToken);
            JsonElement validateProposed = StructuredResult(validateProposedResponse);
            Assert.True(validateProposed.GetProperty("isValid").GetBoolean());
            Assert.Equal("proposed", validateProposed.GetProperty("workspace").GetString());
        }

        await WriteToolCallAsync(
            input,
            7,
            "generate_likec4",
            new
            {
                snapshotId,
                model = modelJson,
                dryRun = false,
                write = true,
                destinationPath = "generated",
            });
        using JsonDocument writeResponse = await ReadResponseAsync(process, cancellationToken);
        JsonElement written = StructuredResult(writeResponse);
        Assert.True(written.GetProperty("written").GetBoolean());
        Assert.True(File.Exists(Path.Combine(repository.Path, "generated", "specification.c4")));
        Assert.True(File.Exists(Path.Combine(repository.Path, "generated", "model.c4")));
        Assert.True(File.Exists(Path.Combine(repository.Path, "generated", "views.c4")));

        await WriteToolCallAsync(
            input,
            8,
            "generate_likec4",
            new
            {
                snapshotId,
                model = modelJson,
                dryRun = false,
                write = true,
                destinationPath = "generated",
            });
        using JsonDocument conflictResponse = await ReadResponseAsync(process, cancellationToken);
        AssertToolError(conflictResponse, "destination_exists");

        ArchitectureModel invalidSchema = model with
        {
            SchemaVersion = "9.0",
        };
        using JsonDocument invalidModelDocument =
            JsonDocument.Parse(JsonSerializer.Serialize(invalidSchema, McpToolJson.Options));
        await WriteToolCallAsync(
            input,
            9,
            "generate_likec4",
            new
            {
                snapshotId,
                model = invalidModelDocument.RootElement.Clone(),
            });
        using JsonDocument invalidSchemaResponse = await ReadResponseAsync(process, cancellationToken);
        AssertToolError(invalidSchemaResponse, "model_invalid");

        await WriteToolCallAsync(
            input,
            10,
            "generate_likec4",
            new
            {
                snapshotId = "snapshot_missing",
                model = modelJson,
            });
        using JsonDocument missingSnapshotResponse = await ReadResponseAsync(process, cancellationToken);
        AssertToolError(missingSnapshotResponse, "snapshot_not_found_or_expired");

        if (string.Equals(
                Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            File.WriteAllText(
                Path.Combine(repository.Path, "generated", "model.c4"),
                "model { broken = softwareSystem");

            await WriteToolCallAsync(
                input,
                11,
                "validate_likec4",
                new
                {
                    snapshotId,
                    model = modelJson,
                    destinationPath = "generated",
                });
            using JsonDocument invalidFileResponse = await ReadResponseAsync(process, cancellationToken);
            JsonElement invalidFile = StructuredResult(invalidFileResponse);
            Assert.False(invalidFile.GetProperty("isValid").GetBoolean());
            Assert.NotEqual(0, invalidFile.GetProperty("exitCode").GetInt32());
            Assert.True(invalidFile.GetProperty("diagnostics").GetArrayLength() > 0);
        }

        input.Close();
        await process.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        string unexpectedStdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        Assert.Equal(string.Empty, unexpectedStdout);
        Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.ExitCode);
    }

    private static ArchitectureModel CreateModel(RepositorySnapshot snapshot)
    {
        string[] evidenceIds = [.. snapshot.Evidence.Select(item => item.Id)];
        ArchitectureElement system = new(
            "el_library_system",
            ArchitectureElementKind.SoftwareSystem,
            "Library repository",
            null,
            [.. evidenceIds],
            ReviewStatus.RequiresReview,
            "Repository declarations do not confirm a deployed system boundary.");

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C1,
            snapshot,
            [system],
            []);
    }

    private static void AssertToolError(JsonDocument response, string expectedCode)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains(
            expectedCode,
            result.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    private static async Task InitializeAsync(
        Process process,
        StreamWriter input,
        CancellationToken cancellationToken)
    {
        await WriteRequestAsync(
            input,
            1,
            "initialize",
            new
            {
                protocolVersion = "2025-11-25",
                capabilities = new
                {
                },
                clientInfo = new
                {
                    name = "Repo2C4.Mcp.Tests",
                    version = "1.0.0",
                },
            });

        using JsonDocument response = await ReadResponseAsync(process, cancellationToken);
        Assert.Equal(
            "repo2c4",
            response.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        await input.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
            @params = new
            {
            },
        }));
    }

    private static Task WriteToolCallAsync(
        StreamWriter input,
        int id,
        string name,
        object arguments) =>
        WriteRequestAsync(
            input,
            id,
            "tools/call",
            new
            {
                name,
                arguments,
            });

    private static async Task WriteRequestAsync(
        StreamWriter input,
        int id,
        string method,
        object parameters)
    {
        await input.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        }));
    }

    private static Process StartServer(string repositoryRoot)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("--repository-root");
        startInfo.ArgumentList.Add(repositoryRoot);

        Process process = new()
        {
            StartInfo = startInfo,
        };
        Assert.True(process.Start(), "The Repo2C4 MCP process did not start.");
        return process;
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(line), "Expected one MCP protocol response on stdout.");
        return JsonDocument.Parse(line);
    }

    private static JsonElement StructuredResult(JsonDocument response) =>
        response.RootElement.GetProperty("result").GetProperty("structuredContent");

    private sealed class TempRepository : IDisposable
    {
        internal TempRepository()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-mcp-protocol-").FullName;
        }

        internal string Path
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
