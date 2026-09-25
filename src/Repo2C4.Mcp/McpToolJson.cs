using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Mcp;

internal static class McpToolJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        options.Converters.Add(
            new JsonStringEnumConverter<ArchitectureElementKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<ArchitectureLevel>(allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<ReviewStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<EvidenceSourceType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<DiagnosticSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
