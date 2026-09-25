using System.Collections.Immutable;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.C3;

/// <summary>Builds a bounded, evidence-first C3 proposal for one explicitly selected container.</summary>
public static class ArchitectureC3Builder
{
    private static readonly (string Prefix, string Name, string Responsibility)[] Categories =
    [
        ("dotnet.runtime.http.", "HTTP interface", "Receives HTTP traffic and exposes application entry points."),
        ("dotnet.integration.", "Integration adapter", "Connects the selected container to an external technology or service."),
        ("dotnet.project.reference", "Application dependency", "Represents a build-time dependency that may indicate an application-layer collaboration."),
    ];

    public static ArchitectureC3Model Build(ArchitectureModel baseModel, string selectedContainerId)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedContainerId);

        ImmutableArray<ContractError> baseErrors = ContractValidator.ValidateModel(baseModel);
        if (!baseErrors.IsEmpty)
        {
            throw new ContractValidationException(baseErrors);
        }

        ArchitectureElement? selected = baseModel.Elements
            .FirstOrDefault(item => item.Id == selectedContainerId && item.Kind == ArchitectureElementKind.Container);
        if (selected is null)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "c3.containerMissing",
                    "$.selectedContainerId",
                    "Selected C3 container must exist in the C2 base model."),
            ]);
        }

        Dictionary<string, Evidence> evidenceById = baseModel.Snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Evidence[] selectedEvidence =
        [
            .. selected.EvidenceIds
                .Where(evidenceById.ContainsKey)
                .Select(id => evidenceById[id])
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];

        List<ArchitectureComponent> components = [];
        foreach ((string prefix, string name, string responsibility) in Categories)
        {
            Evidence[] matching =
            [
                .. selectedEvidence.Where(item =>
                    prefix.EndsWith(".", StringComparison.Ordinal)
                        ? item.Category.StartsWith(prefix, StringComparison.Ordinal)
                        : item.Category == prefix),
            ];

            if (matching.Length == 0)
            {
                continue;
            }

            string componentId = StableIds.ForElement(
                ArchitectureElementKind.Container,
                "c3|" + selected.Id + "|" + prefix);

            components.Add(new ArchitectureComponent(
                componentId,
                selected.Id,
                name,
                responsibility,
                [.. matching.Select(item => item.Id)],
                ReviewStatus.RequiresReview,
                "Repository evidence supports this responsibility grouping, but C3 component boundaries require architectural review."));
        }

        if (components.Count == 0)
        {
            return new ArchitectureC3Model(
                ContractSchema.Version,
                baseModel,
                selected.Id,
                [],
                []);
        }

        return new ArchitectureC3Model(
            ContractSchema.Version,
            baseModel,
            selected.Id,
            [.. components.Take(ArchitectureC3Validator.MaxComponents)],
            []);
    }
}
