using System.Text.Json;
using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class OnboardingTests
{
    [Fact]
    public async Task InitNonInteractiveCreatesSafeVersionedConfigurationAndIsIdempotent()
    {
        using TempDirectory temp = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int first = await Program.RunAsync(
            ["init", "--repository", temp.Path, "--non-interactive"],
            output,
            error,
            CancellationToken.None);
        string configPath = Path.Combine(temp.Path, ".repo2c4.json");
        string before = File.ReadAllText(configPath);
        int second = await Program.RunAsync(
            ["init", "--repository", temp.Path, "--non-interactive"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, first);
        Assert.Equal(CliExitCodes.Success, second);
        Assert.Equal(before, File.ReadAllText(configPath));
        using JsonDocument json = JsonDocument.Parse(before);
        Assert.Equal("repo2c4.config/v1", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("docs/architecture", json.RootElement.GetProperty("outputDirectory").GetString());
        Assert.Equal("offline", json.RootElement.GetProperty("mode").GetString());
        Assert.DoesNotContain("token", before, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", before, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No repository analysis", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveInitCanBeCancelledWithoutWriting()
    {
        using TempDirectory temp = new();
        using StringReader input = new("\n\nn\n");
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await Program.RunAsync(
            ["init", "--repository", temp.Path],
            output,
            error,
            CancellationToken.None,
            standardInput: input);

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.False(File.Exists(Path.Combine(temp.Path, ".repo2c4.json")));
        Assert.Contains("Cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForceCreatesBackupBeforeReplacement()
    {
        using TempDirectory temp = new();
        string config = Path.Combine(temp.Path, ".repo2c4.json");
        File.WriteAllText(config, "{\"legacy\":true}\n");

        int exit = await Run(["init", "--repository", temp.Path, "--non-interactive", "--force"]);

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.Equal("{\"legacy\":true}\n", File.ReadAllText(config + ".bak"));
        Assert.Contains("repo2c4.config/v1", File.ReadAllText(config), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/outside")]
    public async Task InitRejectsUnsafeOutputPaths(string outputDirectory)
    {
        using TempDirectory temp = new();
        int exit = await Run(
            ["init", "--repository", temp.Path, "--output-directory", outputDirectory, "--non-interactive"]);
        Assert.Equal(CliExitCodes.UsageError, exit);
        Assert.False(File.Exists(Path.Combine(temp.Path, ".repo2c4.json")));
    }

    [Fact]
    public async Task DoctorRejectsConfigurationContainingSecretField()
    {
        using TempDirectory temp = new();
        File.WriteAllText(
            Path.Combine(temp.Path, ".repo2c4.json"),
            "{\"schemaVersion\":\"repo2c4.config/v1\",\"repositoryRoot\":\"x\",\"outputDirectory\":\"docs\",\"mode\":\"offline\",\"provider\":null,\"apiKey\":\"do-not-store\"}");

        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await Program.RunAsync(
            ["doctor", "--repository", temp.Path],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.ValidationFailed, exit);
        Assert.Contains("FAIL configuration", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-store", output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorMissingConfigurationHasCoherentValidationExit()
    {
        using TempDirectory temp = new();
        int exit = await Run(["doctor", "--repository", temp.Path]);
        Assert.Equal(CliExitCodes.ValidationFailed, exit);
    }

    private static async Task<int> Run(string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        return await Program.RunAsync(args, output, error, CancellationToken.None);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() =>
            Path = Directory.CreateTempSubdirectory("repo2c4-onboarding-").FullName;

        public string Path\n        {\n            get;\n        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
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
