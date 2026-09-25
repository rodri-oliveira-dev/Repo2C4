using ModelContextProtocol;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpResponseGuardTests
{
    [Fact]
    public void OversizedStructuredPayloadIsRejected()
    {
        string oversized = new('x', McpLimits.MaxResponseBytes);

        McpException exception =
            Assert.Throws<McpException>(() => McpResponseGuard.EnsureWithinLimit(oversized));

        Assert.Contains("response_limit_exceeded", exception.Message, StringComparison.Ordinal);
    }
}
