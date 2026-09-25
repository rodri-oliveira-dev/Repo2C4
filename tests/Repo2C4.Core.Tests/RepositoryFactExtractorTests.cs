using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class RepositoryFactExtractorTests
{
    [Fact]
    public void MultiProjectFixturePreservesProvenanceAndNeverPromotesCandidateToRuntimeRelation()
    {
        using Fixture fixture = new();
        fixture.Add("Acme.slnx", """
            <Solution>
              <Project Path="src/Web/Web.csproj" />
              <Project Path="src/Shared/Shared.csproj" />
              <Project Path="src/Worker/Worker.csproj" />
            </Solution>
            """);
        fixture.Add("src/Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Shared/Shared.csproj" />
                <PackageReference Include="Npgsql" Version="10.0.0" />
                <PackageReference Include="RabbitMQ.Client" Version="7.0.0" />
                <PackageReference Include="StackExchange.Redis" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        fixture.Add("src/Web/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            app.MapGet("/health", () => "ok");
            """);
        fixture.Add("src/Shared/Shared.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        fixture.Add("src/Worker/Worker.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Worker">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        fixture.Add("src/Worker/Program.cs", "builder.Services.AddHostedService<Worker>();");
        fixture.Add("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
        fixture.Add("compose.yaml", "services: {}");

        RepositorySnapshot snapshot = Extract(fixture.Options());
        string json = ContractJson.SerializeSnapshot(snapshot);
        Assert.Equal(json, ContractJson.SerializeSnapshot(Extract(fixture.Options())));
        Assert.Empty(ContractValidator.ValidateSnapshot(snapshot));

        Assert.Equal(3, snapshot.Evidence.Count(item => item.Category == "dotnet.solution.project"));
        Assert.Equal(3, snapshot.Evidence.Count(item => item.Category == "dotnet.project"));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.kind"
            && item.RelativePath == "src/Shared/Shared.csproj" && item.Description.Contains("Library", StringComparison.Ordinal));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.kind"
            && item.RelativePath == "src/Worker/Worker.csproj" && item.Description.Contains("Executable", StringComparison.Ordinal));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.reference"
            && item.RelativePath == "src/Web/Web.csproj"
            && item.Description.Contains("src/Shared/Shared.csproj", StringComparison.Ordinal));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.targetFramework"
            && item.Description.Contains("net9.0", StringComparison.Ordinal));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.runtime.http.candidate"
            && item.RelativePath == "src/Web/Program.cs" && item.Line == 1);
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.runtime.worker.candidate"
            && item.RelativePath == "src/Worker/Program.cs" && item.Line == 1);
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.integration.postgresql.candidate"
            && item.RelativePath == "src/Web/Web.csproj");
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.integration.rabbitmq.candidate"
            && item.RelativePath == "src/Web/Web.csproj");
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.integration.redis.candidate"
            && item.RelativePath == "src/Web/Web.csproj");
        Assert.Equal(2, snapshot.Evidence.Count(item => item.Category == "deployment.docker.manifest"));
        Assert.All(snapshot.Evidence, item =>
        {
            Assert.Contains(snapshot.Files, file => file.RelativePath == item.RelativePath);
            Assert.True(item.Line is null or >= 1);
        });
        Assert.Empty(snapshot.Evidence.Where(item => item.Category == "architecture.relation"));
        Assert.DoesNotContain("PostgreSQL communication confirmed", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryWithoutRunnableProjectDoesNotInventHttpWorkerOrIntegration()
    {
        using Fixture fixture = new();
        fixture.Add("OnlyLib/OnlyLib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.kind"
            && item.Description.Contains("Library", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Evidence, item =>
            item.Category.StartsWith("dotnet.runtime.", StringComparison.Ordinal)
            || item.Category.StartsWith("dotnet.integration.", StringComparison.Ordinal));
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void LegacySolutionAndTestProjectAreRecognizedWithoutEvaluatingBuild()
    {
        using Fixture fixture = new();
        fixture.Add("Legacy.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Tests", "tests\Tests.csproj", "{00000000-0000-0000-0000-000000000000}"
            EndProject
            """);
        fixture.Add("tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.v3" Version="3.0.0" /></ItemGroup>
            </Project>
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.solution.project"
            && item.RelativePath == "Legacy.sln" && item.Line == 2);
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.kind"
            && item.Description.Contains("Test", StringComparison.Ordinal));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project.testCandidate");
    }

    [Fact]
    public void MissingProjectReferenceProducesDiagnosticWithoutInventingRelation()
    {
        using Fixture fixture = new();
        fixture.Add("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../Outside/NotHere.csproj" /></ItemGroup>
            </Project>
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.DoesNotContain(snapshot.Evidence, item => item.Category == "dotnet.project.reference");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "extract.outsideRootReference");
    }

    [Fact]
    public void UnknownFrameworkAndMissingConfigurationDoNotAbortOrInventFacts()
    {
        using Fixture fixture = new();
        fixture.Add("Library.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>$(ConfiguredExternally)</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="$(ProjectToReference)" /></ItemGroup>
            </Project>
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project");
        Assert.DoesNotContain(snapshot.Evidence, item => item.Category == "dotnet.project.targetFramework");
        Assert.DoesNotContain(snapshot.Evidence, item => item.Category == "dotnet.project.reference");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "extract.unresolvedReference");
    }

    [Fact]
    public void InvalidXmlAndProhibitedDtdAreReportedWithoutStoppingOtherFiles()
    {
        using Fixture fixture = new();
        fixture.Add("Bad.csproj", "<Project>");
        fixture.Add("Evil.csproj", """
            <!DOCTYPE Project [<!ENTITY bad SYSTEM "file:///etc/passwd">]>
            <Project><PropertyGroup><TargetFramework>&bad;</TargetFramework></PropertyGroup></Project>
            """);
        fixture.Add("Good.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "extract.invalidXml"));
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.project"
            && item.RelativePath == "Good.csproj");
        Assert.DoesNotContain(snapshot.Evidence, item => item.RelativePath == "Evil.csproj");
    }

    [Fact]
    public void InaccessibleAndOversizedFilesAreNeverUsedAsEvidence()
    {
        using Fixture fixture = new();
        fixture.Add("Huge.csproj", "<Project>" + new string('x', 600_000) + "</Project>");
        fixture.Add("Small.csproj", "<Project />");

        RepositorySnapshot snapshot = Extract(fixture.Options() with
        {
            MaxBytesPerFile = 700_000,
            MaxTotalBytes = 800_000
        });

        Assert.Contains(snapshot.Diagnostics, item => item.Code == "extract.fileTooLarge"
            && item.RelativePath == "Huge.csproj");
        Assert.Contains(snapshot.Evidence, item => item.RelativePath == "Small.csproj");
        Assert.DoesNotContain(snapshot.Evidence, item => item.RelativePath == "Huge.csproj");
    }

    [Fact]
    public void SecretAndBinaryFilesDoNotLeakIntoEvidenceOrJson()
    {
        using Fixture fixture = new();
        fixture.Add("appsettings.Production.json", "{\"password\":\"DO_NOT_EMIT_123\"}");
        fixture.Add("src/password-secret.cs", "DO_NOT_EMIT_123");
        fixture.AddBytes("Image.cs", [65, 0, 66]);
        fixture.Add("Normal.csproj", "<Project />");

        RepositorySnapshot snapshot = Extract(fixture.Options());
        string json = ContractJson.SerializeSnapshot(snapshot);

        Assert.DoesNotContain("DO_NOT_EMIT_123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret.cs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Image.cs", json, StringComparison.Ordinal);
        Assert.Contains(snapshot.Evidence, item => item.RelativePath == "Normal.csproj");
    }

    [Fact]
    public void SourceSignalIsCandidateRatherThanConfirmedRelation()
    {
        using Fixture fixture = new();
        fixture.Add("src/Program.cs", """
            // RabbitMQ is not used in this commented line
            string label = "postgresql";
            client.ConnectToRedis();
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.DoesNotContain(snapshot.Evidence, item => item.Category == "dotnet.integration.rabbitmq.candidate");
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.integration.postgresql.candidate"
            && item.Line == 2);
        Assert.Contains(snapshot.Evidence, item => item.Category == "dotnet.integration.redis.candidate"
            && item.Line == 3);
        Assert.All(snapshot.Evidence, item => Assert.EndsWith(".candidate", item.Category, StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformSpecificTargetFrameworksRemainEvidence()
    {
        using Fixture fixture = new();
        fixture.Add("Platforms.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0-windows;net10.0-android;net9.0-ios18.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        RepositorySnapshot snapshot = Extract(fixture.Options());
        string[] frameworks = snapshot.Evidence
            .Where(item => item.Category == "dotnet.project.targetFramework")
            .Select(item => item.Description)
            .ToArray();

        Assert.Contains(frameworks, item => item.Contains("net8.0-windows", StringComparison.Ordinal));
        Assert.Contains(frameworks, item => item.Contains("net10.0-android", StringComparison.Ordinal));
        Assert.Contains(frameworks, item => item.Contains("net9.0-ios18.0", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalEvidenceLimitAlwaysProducesExplicitDiagnostic()
    {
        using Fixture fixture = new();
        for (int file = 0; file < 40; file++)
        {
            string content = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, 256).Select(line => $"client.ConnectToRedis(); // {line}"));
            fixture.Add($"src/Signal{file:D2}.cs", content);
        }

        RepositorySnapshot snapshot = Extract(fixture.Options());

        Assert.Equal(10_000, snapshot.Evidence.Length);
        Assert.Single(snapshot.Diagnostics, item => item.Code == "extract.evidenceLimit");
    }

    [Fact]
    public void ExtractHonorsCancellation()
    {
        using Fixture fixture = new();
        fixture.Add("App.csproj", "<Project />");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RepositoryFactExtractor.Extract(fixture.Options(), cancellation.Token));
    }

    private static RepositorySnapshot Extract(RepositoryScanOptions options) =>
        RepositoryFactExtractor.Extract(options, TestContext.Current.CancellationToken);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "repo2c4_facts_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root
        {
            get;
        }

        public RepositoryScanOptions Options() => new(Root, "sample_repo");

        public string Add(string relative, string content)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void AddBytes(string relative, byte[] content)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
