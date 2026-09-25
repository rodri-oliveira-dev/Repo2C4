using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpResponseGuardTests
{
    [Fact]
    public void OversizedStructuredPayloadIsRejected()
    {
        string oversized = new('x', McpLimits.MaxResponseBytes);

        Exception exception =
            Assert.ThrowsAny<Exception>(() => McpResponseGuard.EnsureWithinLimit(oversized));

        Assert.Contains("response_limit_exceeded", exception.Message, StringComparison.Ordinal);
    }
}
