using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpClientEndToEndTests
{
    [Fact]
    public async Task ProtocolClientReproducesVersionedC1AndC2Flow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string repositoryRoot = FindRepositoryRoot();
        using TempFixture fixture = TempFixture.Create(
            Path.Combine(repositoryRoot, "examples", "fixtures", "library-only"));

        using ProtocolTestClient client = new(fixture.Path);
        await client.InitializeAsync(cancellationToken);

        JsonElement inspect = await client.CallToolAsync(
            "inspect_repository",
            new
            {
                repositoryPath = ".",
            },
            cancellationToken);

        string snapshotId = inspect.GetProperty("snapshotId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(snapshotId));

        await VerifyModelAsync(
            client,
            snapshotId,
            repositoryRoot,
            fixture.Path,
            "c1",
            "architecture.c1.v1.json",
            cancellationToken);
        await VerifyModelAsync(
            client,
            snapshotId,
            repositoryRoot,
            fixture.Path,
            "c2",
            "architecture.c2.v1.json",
            cancellationToken);

        await client.CompleteAsync(cancellationToken);
    }

    private static async Task VerifyModelAsync(
        ProtocolTestClient client,
        string snapshotId,
        string repositoryRoot,
        string fixtureRoot,
        string level,
        string modelFile,
        CancellationToken cancellationToken)
    {
        string modelPath = Path.Combine(repositoryRoot, "examples", "end-to-end", modelFile);
        using JsonDocument modelDocument = JsonDocument.Parse(await File.ReadAllTextAsync(modelPath, cancellationToken));
        JsonElement model = modelDocument.RootElement.Clone();
        string destination = "generated/" + level;

        JsonElement preview = await client.CallToolAsync(
            "generate_likec4",
            new
            {
                snapshotId,
                model,
                destinationPath = destination,
            },
            cancellationToken);

        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.False(preview.GetProperty("written").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(fixtureRoot, "generated", level)));
        Assert.Equal(3, preview.GetProperty("files").GetArrayLength());

        AssertPreviewMatchesGolden(preview, repositoryRoot, level);

        if (LikeC4IntegrationEnabled())
        {
            JsonElement validation = await client.CallToolAsync(
                "validate_likec4",
                new
                {
                    snapshotId,
                    model,
                },
                cancellationToken);

            Assert.True(validation.GetProperty("isValid").GetBoolean());
            Assert.Equal(0, validation.GetProperty("exitCode").GetInt32());
            Assert.Equal("proposed", validation.GetProperty("workspace").GetString());
        }

        JsonElement write = await client.CallToolAsync(
            "generate_likec4",
            new
            {
                snapshotId,
                model,
                dryRun = false,
                write = true,
                destinationPath = destination,
            },
            cancellationToken);

        Assert.True(write.GetProperty("written").GetBoolean());
        Assert.False(write.GetProperty("dryRun").GetBoolean());
        AssertWrittenFilesMatchGolden(fixtureRoot, repositoryRoot, level);

        if (LikeC4IntegrationEnabled())
        {
            JsonElement validation = await client.CallToolAsync(
                "validate_likec4",
                new
                {
                    snapshotId,
                    model,
                    destinationPath = destination,
                },
                cancellationToken);

            Assert.True(validation.GetProperty("isValid").GetBoolean());
            Assert.Equal(0, validation.GetProperty("exitCode").GetInt32());
            Assert.Equal(destination, validation.GetProperty("workspace").GetString());
        }
    }

    private static void AssertPreviewMatchesGolden(
        JsonElement preview,
        string repositoryRoot,
        string level)
    {
        Dictionary<string, string> files = preview
            .GetProperty("files")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("fileName").GetString()!,
                item => item.GetProperty("content").GetString()!,
                StringComparer.Ordinal);

        foreach (string fileName in new[] { "specification.c4", "model.c4", "views.c4" })
        {
            string expected = File.ReadAllText(
                Path.Combine(repositoryRoot, "examples", "end-to-end", "generated", level, fileName));
            Assert.Equal(expected, files[fileName]);
        }
    }

    private static void AssertWrittenFilesMatchGolden(
        string fixtureRoot,
        string repositoryRoot,
        string level)
    {
        foreach (string fileName in new[] { "specification.c4", "model.c4", "views.c4" })
        {
            string expected = File.ReadAllText(
                Path.Combine(repositoryRoot, "examples", "end-to-end", "generated", level, fileName));
            string actual = File.ReadAllText(
                Path.Combine(fixtureRoot, "generated", level, fileName));
            Assert.Equal(expected, actual);
        }
    }

    private static bool LikeC4IntegrationEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
            "1",
            StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Repo2C4.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Repo2C4 repository root.");
    }

    private sealed class ProtocolTestClient : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private int _requestId;
        private bool _completed;

        internal ProtocolTestClient(string repositoryRoot)
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

            _process = new Process
            {
                StartInfo = startInfo,
            };
            Assert.True(_process.Start(), "The Repo2C4 MCP process did not start.");
            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _input.NewLine = "\n";
        }

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            JsonElement response = await RequestAsync(
                "initialize",
                new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new
                    {
                    },
                    clientInfo = new
                    {
                        name = "Repo2C4.ProtocolTestClient",
                        version = "1.0.0",
                    },
                },
                cancellationToken);

            Assert.Equal(
                "repo2c4",
                response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

            await _input.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new
                {
                },
            }));
        }

        internal async Task<JsonElement> CallToolAsync(
            string name,
            object arguments,
            CancellationToken cancellationToken)
        {
            JsonElement response = await RequestAsync(
                "tools/call",
                new
                {
                    name,
                    arguments,
                },
                cancellationToken);

            JsonElement result = response.GetProperty("result");
            Assert.False(
                result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(),
                result.TryGetProperty("content", out JsonElement content)
                    ? content.ToString()
                    : "MCP tool call failed.");

            return result.GetProperty("structuredContent").Clone();
        }

        internal async Task CompleteAsync(CancellationToken cancellationToken)
        {
            _input.Close();
            await _process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            string unexpectedStdout = await _process.StandardOutput.ReadToEndAsync(cancellationToken);
            string standardError = await _process.StandardError.ReadToEndAsync(cancellationToken);
            Assert.Equal(string.Empty, unexpectedStdout);
            Assert.DoesNotContain("Unhandled exception", standardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, _process.ExitCode);
            _completed = true;
        }

        private async Task<JsonElement> RequestAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            int id = ++_requestId;
            await _input.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            }));

            string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(line), "Expected one MCP protocol response on stdout.");

            using JsonDocument response = JsonDocument.Parse(line);
            Assert.Equal(id, response.RootElement.GetProperty("id").GetInt32());
            return response.RootElement.Clone();
        }

        public void Dispose()
        {
            _input.Dispose();

            if (!_completed && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            _process.Dispose();
        }
    }

    private sealed class TempFixture : IDisposable
    {
        private readonly string _parent;

        private TempFixture(string parent, string path)
        {
            _parent = parent;
            Path = path;
        }

        internal string Path
        {
            get;
        }

        internal static TempFixture Create(string source)
        {
            string parent = Directory.CreateTempSubdirectory("repo2c4-mcp-client-e2e-").FullName;
            string destination = Path.Combine(parent, "library-only");
            Directory.CreateDirectory(destination);

            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, sourceFile);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target);
            }

            return new TempFixture(parent, destination);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_parent, recursive: true);
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
