using System.Reflection;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class CoreDependencyTests
{
    [Fact]
    public void CoreDoesNotReferenceExecutableHosts()
    {
        Assembly core = Assembly.Load("Repo2C4.Core");
        string[] dependencies = core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Repo2C4.Cli", dependencies);
        Assert.DoesNotContain("Repo2C4.Mcp", dependencies);
    }
}
