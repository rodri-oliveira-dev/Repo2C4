using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repo2C4.Core.Contracts;

/// <summary>Canonical deterministic JSON for the additive Semantic C3 contract.</summary>
public static class SemanticC3ContractJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(SemanticC3Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ThrowIfInvalid(SemanticC3Validator.Validate(proposal));
        return JsonSerializer.Serialize(Canonical(proposal), Options);
    }

    public static SemanticC3Proposal Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            SemanticC3Proposal? proposal = JsonSerializer.Deserialize<SemanticC3Proposal>(json, Options);
            ThrowIfInvalid(SemanticC3Validator.Validate(proposal));
            return Canonical(proposal!);
        }
        catch (JsonException)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "json.invalid",
                    "$",
                    "JSON cannot be deserialized into the Semantic C3 contract."),
            ])
            {
                Source = nameof(SemanticC3ContractJson),
            };
        }
    }

    private static SemanticC3Proposal Canonical(SemanticC3Proposal proposal) =>
        proposal with
        {
            SelectedContainerIds = SortIds(proposal.SelectedContainerIds),
            Components =
            [
                .. proposal.Components
                    .OrderBy(component => component.Id, StringComparer.Ordinal)
                    .Select(component => component with
                    {
                        EvidenceIds = SortIds(component.EvidenceIds),
                    }),
            ],
            Relations =
            [
                .. proposal.Relations
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                    .Select(relation => relation with
                    {
                        EvidenceIds = SortIds(relation.EvidenceIds),
                    }),
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

        options.Converters.Add(
            new JsonStringEnumConverter<SemanticC3ComponentCategory>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<SemanticC3RelationTargetKind>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        options.Converters.Add(
            new JsonStringEnumConverter<ReviewStatus>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));

        return options;
    }
}
