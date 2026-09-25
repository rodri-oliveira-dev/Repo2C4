using System.Text.Json;
using ModelContextProtocol;

namespace Repo2C4.Mcp;

internal static class McpResponseGuard
{
    internal static T EnsureWithinLimit<T>(T response)
    {
        int payloadBytes = JsonSerializer.SerializeToUtf8Bytes(response, McpToolJson.Options).Length;

        // The SDK emits a JSON text fallback alongside structuredContent for object results.
        // Budget conservatively for both copies plus the JSON-RPC envelope.
        long estimatedWireBytes = (payloadBytes * 2L) + McpLimits.ProtocolEnvelopeReserveBytes;
        if (estimatedWireBytes > McpLimits.MaxResponseBytes)
        {
            throw new McpException(
                "response_limit_exceeded: narrow the filters or request a smaller page.");
        }

        return response;
    }
}
