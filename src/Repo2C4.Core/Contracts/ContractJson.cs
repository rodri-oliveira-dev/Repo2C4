using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repo2C4.Core.Contracts;

/// <summary>Canonical, validated v1 JSON with stable object properties and sorted collections.</summary>
public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string SerializeSnapshot(RepositorySnapshot snapshot)
    {
        ThrowIfInvalid(ContractValidator.ValidateSnapshot(snapshot));
        return JsonSerializer.Serialize(CanonicalSnapshot(snapshot), Options);
    }

    public static RepositorySnapshot DeserializeSnapshot(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RepositorySnapshot? snapshot = Deserialize<RepositorySnapshot>(json);
        ThrowIfInvalid(ContractValidator.ValidateSnapshot(snapshot));
        return CanonicalSnapshot(snapshot!);
    }

    public static string SerializeModel(ArchitectureModel model)
    {
        ThrowIfInvalid(ContractValidator.ValidateModel(model));
        return JsonSerializer.Serialize(CanonicalModel(model), Options);
    }

    public static ArchitectureModel DeserializeModel(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArchitectureModel? model = Deserialize<ArchitectureModel>(json);
        ThrowIfInvalid(ContractValidator.ValidateModel(model));
        return CanonicalModel(model!);
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new ContractValidationException([new ContractError(
                "json.invalid",
                "$",
                "JSON cannot be deserialized into the requested v1 contract.")])
            {
                // Exception data never includes raw input or repository content.
                Source = nameof(ContractJson),
            };
        }
    }

    private static RepositorySnapshot CanonicalSnapshot(RepositorySnapshot snapshot) =>
        snapshot with
        {
            Files = [.. snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)],
            Evidence = [.. snapshot.Evidence.OrderBy(evidence => evidence.Id, StringComparer.Ordinal)],
            Diagnostics =
            [
                .. snapshot.Diagnostics
                    .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.RelativePath, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal),
            ],
        };

    private static ArchitectureModel CanonicalModel(ArchitectureModel model) =>
        model with
        {
            Snapshot = CanonicalSnapshot(model.Snapshot),
            Elements =
            [
                .. model.Elements
                    .OrderBy(element => element.Id, StringComparer.Ordinal)
                    .Select(element => element with { EvidenceIds = SortIds(element.EvidenceIds) }),
            ],
            Relations =
            [
                .. model.Relations
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                    .Select(relation => relation with { EvidenceIds = SortIds(relation.EvidenceIds) }),
            ],
        };

    private static ImmutableArray<string> SortIds(ImmutableArray<string> ids) =>
        [.. ids.OrderBy(id => id, StringComparer.Ordinal)];

    private static void ThrowIfInvalid(ImmutableArray<ContractError> errors)
    {
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        options.Converters.Add(new JsonStringEnumConverter<ArchitectureElementKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ArchitectureLevel>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ReviewStatus>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<EvidenceSourceType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<DiagnosticSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
