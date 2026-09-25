using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpStdioIntegrationTests
{
    [Fact]
    public async Task ExecutableHandshakesAndListsNoPhaseFourteenToolsWithoutExtraStdout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            $"repo2c4-mcp-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryRoot);

        try
        {
            using Process process = StartServer(repositoryRoot);
            await using StreamWriter input = process.StandardInput;
            input.AutoFlush = true;
            input.NewLine = "\n";

            await input.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"Repo2C4.Mcp.Tests","version":"1.0.0"}}}""");

            string initializeLine = await ReadProtocolLineAsync(process, cancellationToken);
            using (JsonDocument initialize = JsonDocument.Parse(initializeLine))
            {
                JsonElement result = initialize.RootElement.GetProperty("result");
                Assert.Equal("repo2c4", result.GetProperty("serverInfo").GetProperty("name").GetString());
                Assert.Equal("2025-11-25", result.GetProperty("protocolVersion").GetString());
            }

            await input.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await input.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

            string toolsLine = await ReadProtocolLineAsync(process, cancellationToken);
            using (JsonDocument tools = JsonDocument.Parse(toolsLine))
            {
                JsonElement toolArray = tools.RootElement.GetProperty("result").GetProperty("tools");
                Assert.Equal(0, toolArray.GetArrayLength());
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
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
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

    private static async Task<string> ReadProtocolLineAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(line), "Expected one MCP protocol response on stdout.");
        return line;
    }
}
