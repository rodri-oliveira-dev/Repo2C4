using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class RepositoryScannerTests
{
    [Fact]
    public void UnchangedDirectoryProducesIdenticalCanonicalSnapshots()
    {
        using TemporaryRepository repository = new();
        repository.Add("src/Service/Service.csproj", "<Project />");
        repository.Add("Repo2C4.slnx", "<Solution />");
        repository.Add("src/Service/Program.cs", "class Program { }");

        RepositorySnapshot first = Scan(repository.Options());
        RepositorySnapshot second = Scan(repository.Options());

        Assert.Equal(ContractJson.SerializeSnapshot(first), ContractJson.SerializeSnapshot(second));
        Assert.Equal(
            ["Repo2C4.slnx", "src/Service/Program.cs", "src/Service/Service.csproj"],
            first.Files.Select(file => file.RelativePath).ToArray());
        Assert.All(first.Files, file => Assert.Null(file.Sha256));
        Assert.Empty(first.Evidence);
        Assert.Empty(ContractValidator.ValidateSnapshot(first));
    }

    [Fact]
    public void BuiltInExclusionsCannotBeOverriddenByInclusionPatterns()
    {
        using TemporaryRepository repository = new();
        repository.Add("src/Public.cs", "public class Public { }");
        repository.Add(".git/config", "secret");
        repository.Add("bin/Debug/Output.cs", "secret");
        repository.Add("obj/Release/Output.cs", "secret");
        repository.Add("node_modules/lib/Module.cs", "secret");
        repository.Add("artifacts/output.cs", "secret");
        repository.Add(".env", "TOP_SECRET_123");
        repository.Add(".env.local", "TOP_SECRET_123");
        repository.Add("credentials.json", "TOP_SECRET_123");
        repository.Add("private.key", "TOP_SECRET_123");
        repository.Add("appsettings.Production.json", "TOP_SECRET_123");

        RepositorySnapshot snapshot = Scan(repository.Options() with
        {
            IncludePatterns = ["**/*"]
        });
        string json = ContractJson.SerializeSnapshot(snapshot);

        Assert.Single(snapshot.Files);
        Assert.Equal("src/Public.cs", snapshot.Files[0].RelativePath);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.excluded");
        Assert.DoesNotContain("TOP_SECRET_123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials.json", json, StringComparison.Ordinal);
        Assert.DoesNotContain(repository.Root, json, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeAndExcludeGlobsApplyToRelativePaths()
    {
        using TemporaryRepository repository = new();
        repository.Add("src/Allowed/One.csproj", "<Project />");
        repository.Add("src/Allowed/Two.cs", "class Two { }");
        repository.Add("src/Private/Hidden.csproj", "<Project />");

        RepositorySnapshot snapshot = Scan(repository.Options() with
        {
            IncludePatterns = ["**/*.csproj"],
            ExcludePatterns = ["src/Private/**"],
        });

        Assert.Single(snapshot.Files);
        Assert.Equal("src/Allowed/One.csproj", snapshot.Files[0].RelativePath);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.notIncluded");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.excluded");
    }

    [Fact]
    public void ExternalFileAndDirectorySymlinksAreNeverTraversed()
    {
        using TemporaryRepository repository = new();
        using TemporaryRepository outside = new();
        outside.Add("Outside.cs", "EXTERNAL_SECRET_123");
        repository.Add("Safe.cs", "class Safe { }");
        File.CreateSymbolicLink(Path.Combine(repository.Root, "Escape.cs"), Path.Combine(outside.Root, "Outside.cs"));
        Directory.CreateSymbolicLink(Path.Combine(repository.Root, "EscapeFolder"), outside.Root);

        RepositorySnapshot snapshot = Scan(repository.Options());
        string json = ContractJson.SerializeSnapshot(snapshot);

        Assert.Single(snapshot.Files);
        Assert.Equal("Safe.cs", snapshot.Files[0].RelativePath);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.link");
        Assert.DoesNotContain("EXTERNAL_SECRET_123", json, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.Root, json, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsLinkedRootTraversalAndAbsoluteGlob()
    {
        using TemporaryRepository repository = new();
        string traversal = Path.Combine(repository.Root, "..", Path.GetFileName(repository.Root));
        Assert.Throws<ArgumentException>(() => Scan(new RepositoryScanOptions(traversal, "repo")));
        Assert.Throws<ArgumentException>(() => Scan(new RepositoryScanOptions("relative/path", "repo")));
        Assert.Throws<ArgumentException>(() => Scan(repository.Options() with
        {
            IncludePatterns = ["../**/*.cs"]
        }));
        Assert.Throws<ArgumentException>(() => Scan(repository.Options() with
        {
            ExcludePatterns = ["/etc/**"]
        }));

        string linkedRoot = Path.Combine(Path.GetTempPath(), "repo2c4_link_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, repository.Root);
            Assert.Throws<ArgumentException>(() => Scan(new RepositoryScanOptions(linkedRoot, "repo")));
        }
        finally
        {
            if (Directory.Exists(linkedRoot))
            {
                Directory.Delete(linkedRoot);
            }
        }
    }

    [Fact]
    public void BinaryFilesAreExcludedByExtensionAndPrefix()
    {
        using TemporaryRepository repository = new();
        repository.Add("Readme.md", "text only");
        repository.AddBytes("image.png", [1, 2, 3, 0]);
        repository.AddBytes("Fake.cs", [65, 66, 0, 67]);

        RepositorySnapshot snapshot = Scan(repository.Options());

        Assert.Single(snapshot.Files);
        Assert.Equal("Readme.md", snapshot.Files[0].RelativePath);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.binary"
            && diagnostic.Message.Contains("2 observed entries", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedFileIsOmittedWithExplicitDiagnostic()
    {
        using TemporaryRepository repository = new();
        repository.Add("Big.cs", new string('x', 256));
        repository.Add("Small.cs", "class Small { }");

        RepositorySnapshot snapshot = Scan(repository.Options() with
        {
            MaxBytesPerFile = 32
        });

        Assert.Single(snapshot.Files);
        Assert.Equal("Small.cs", snapshot.Files[0].RelativePath);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.fileTooLarge");
    }

    [Fact]
    public void TotalBytesAndFileCountLimitsReportObservedOmissions()
    {
        using TemporaryRepository repository = new();
        repository.Add("a.cs", "1234567890");
        repository.Add("b.cs", "1234567890");
        repository.Add("c.cs", "1234567890");

        RepositorySnapshot total = Scan(repository.Options() with
        {
            MaxTotalBytes = 15
        });
        Assert.Single(total.Files);
        Assert.Contains(total.Diagnostics, diagnostic => diagnostic.Code == "scan.totalBytesLimit"
            && diagnostic.Message.Contains("2 observed entries", StringComparison.Ordinal));

        RepositorySnapshot count = Scan(repository.Options() with
        {
            MaxFiles = 1
        });
        Assert.Single(count.Files);
        Assert.Contains(count.Diagnostics, diagnostic => diagnostic.Code == "scan.fileLimit"
            && diagnostic.Message.Contains("2 observed entries", StringComparison.Ordinal));
    }

    [Fact]
    public void EntryBudgetReportsTruncationAndDoesNotClaimFullCoverage()
    {
        using TemporaryRepository repository = new();
        repository.Add("a.cs", "class A { }");
        repository.Add("b.cs", "class B { }");
        repository.Add("c.cs", "class C { }");

        RepositorySnapshot snapshot = Scan(repository.Options() with
        {
            MaxVisitedEntries = 1,
            MaxFiles = 1
        });

        Assert.True(snapshot.Files.Length <= 1);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.entryLimit"
            && diagnostic.Message.Contains("lower bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessDeniedIsReportedWithoutLeakingPathOrErrorText()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryRepository repository = new();
        string inaccessible = repository.Add("Denied.cs", "TOP_SECRET_123");
        UnixFileMode original = File.GetUnixFileMode(inaccessible);
        try
        {
            File.SetUnixFileMode(inaccessible, UnixFileMode.None);
            RepositorySnapshot snapshot = Scan(repository.Options());
            string json = ContractJson.SerializeSnapshot(snapshot);

            Assert.Empty(snapshot.Files);
            Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "scan.accessDenied");
            Assert.DoesNotContain("TOP_SECRET_123", json, StringComparison.Ordinal);
            Assert.DoesNotContain(inaccessible, json, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(inaccessible, original);
        }
    }

    [Fact]
    public void CancellationPropagatesRatherThanReturningPartialSuccess()
    {
        using TemporaryRepository repository = new();
        repository.Add("a.cs", "class A { }");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => RepositoryScanner.Scan(repository.Options(), cancellation.Token));
    }

    [Fact]
    public void InvalidLimitsAndRepositoryIdAreRejected()
    {
        using TemporaryRepository repository = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => Scan(repository.Options() with
        {
            MaxFiles = 0
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scan(repository.Options() with
        {
            MaxFiles = 2,
            MaxVisitedEntries = 1
        }));
        Assert.Throws<ArgumentException>(() => Scan(repository.Options() with
        {
            RepositoryId = "/absolute/path"
        }));
    }

    private static RepositorySnapshot Scan(RepositoryScanOptions options) =>
        RepositoryScanner.Scan(options, TestContext.Current.CancellationToken);

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), "repo2c4_scan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root
        {
            get;
        }

        public RepositoryScanOptions Options() => new(Root, "fixture_repo");

        public string Add(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void AddBytes(string relativePath, byte[] bytes)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
