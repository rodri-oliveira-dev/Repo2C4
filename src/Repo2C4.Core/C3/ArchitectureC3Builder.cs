using System.Collections.Immutable;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.C3;

/// <summary>Builds bounded, evidence-first C3 proposals for explicitly selected C2 containers.</summary>
public static class ArchitectureC3Builder
{
    private static readonly (string Prefix, string Name, string Responsibility)[] Categories =
    [
        ("dotnet.runtime.http.", "HTTP interface", "Receives HTTP traffic and exposes application entry points."),
        ("dotnet.integration.", "Integration adapter", "Connects the selected container to an external technology or service."),
        ("dotnet.project.reference", "Application dependency", "Represents a build-time dependency that may indicate an application-layer collaboration."),
    ];

    public static ArchitectureC3Model Build(
        ArchitectureModel baseModel,
        string selectedContainerId)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedContainerId);

        ImmutableArray<ContractError> baseErrors =
            ContractValidator.ValidateModel(baseModel);
        if (!baseErrors.IsEmpty)
        {
            throw new ContractValidationException(baseErrors);
        }

        ArchitectureElement? selected = baseModel.Elements.FirstOrDefault(item =>
            item.Id == selectedContainerId &&
            item.Kind == ArchitectureElementKind.Container);
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

        ArchitectureC3Workspace workspace = BuildMany(
            baseModel,
            [selectedContainerId]);

        ArchitectureC3Selection selection = workspace.Selections[0];
        return new ArchitectureC3Model(
            workspace.SchemaVersion,
            workspace.BaseModel,
            selection.SelectedContainerId,
            selection.Components,
            selection.Relations);
    }

    public static ArchitectureC3Workspace BuildMany(
        ArchitectureModel baseModel,
        IEnumerable<string> selectedContainerIds)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(selectedContainerIds);

        ImmutableArray<ContractError> baseErrors =
            ContractValidator.ValidateModel(baseModel);
        if (!baseErrors.IsEmpty)
        {
            throw new ContractValidationException(baseErrors);
        }

        string[] requested = [.. selectedContainerIds];
        if (requested.Length == 0)
        {
            return new ArchitectureC3Workspace(
                ContractSchema.Version,
                baseModel,
                []);
        }

        if (baseModel.Level != ArchitectureLevel.C2)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "c3.baseLevel",
                    "$.baseModel.level",
                    "C3 requires a C2 base model."),
            ]);
        }

        List<ContractError> selectionErrors = [];
        Dictionary<string, ArchitectureElement> elements = baseModel.Elements
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        for (int i = 0; i < requested.Length; i++)
        {
            string? id = requested[i];
            string path = "$.selectedContainerIds[" + i + "]";

            if (string.IsNullOrWhiteSpace(id))
            {
                selectionErrors.Add(new ContractError(
                    "c3.containerMissing",
                    path,
                    "Selected C3 container ID is required."));
                continue;
            }

            if (!elements.TryGetValue(id, out ArchitectureElement? element))
            {
                selectionErrors.Add(new ContractError(
                    "c3.containerMissing",
                    path,
                    "Selected C3 container must exist in the C2 base model."));
                continue;
            }

            if (element.Kind != ArchitectureElementKind.Container)
            {
                selectionErrors.Add(new ContractError(
                    "c3.containerKind",
                    path,
                    "Selected C3 element must be a C2 container."));
            }
        }

        if (selectionErrors.Count > 0)
        {
            throw new ContractValidationException([.. selectionErrors]);
        }

        string[] canonicalIds =
        [
            .. requested
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

        ArchitectureC3Selection[] selections =
        [
            .. canonicalIds.Select(id =>
            {
                ArchitectureC3Model model = BuildSingle(baseModel, elements[id]);
                return new ArchitectureC3Selection(
                    model.SelectedContainerId,
                    model.Components,
                    model.Relations);
            }),
        ];

        ArchitectureC3Workspace workspace = new(
            ContractSchema.Version,
            baseModel,
            [.. selections]);

        ImmutableArray<ContractError> errors =
            ArchitectureC3WorkspaceValidator.Validate(workspace);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return workspace;
    }

    private static ArchitectureC3Model BuildSingle(
        ArchitectureModel baseModel,
        ArchitectureElement selected)
    {
        Dictionary<string, Evidence> evidenceById = baseModel.Snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Evidence[] directEvidence =
        [
            .. selected.EvidenceIds
                .Where(evidenceById.ContainsKey)
                .Select(id => evidenceById[id]),
        ];
        HashSet<string> selectedPaths = directEvidence
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        Evidence[] selectedEvidence =
        [
            .. baseModel.Snapshot.Evidence
                .Where(item => selectedPaths.Contains(item.RelativePath))
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];

        List<ArchitectureComponent> components = [];
        foreach ((string prefix, string name, string responsibility) in Categories)
        {
            Evidence[] matching =
            [
                .. selectedEvidence.Where(item =>
                    prefix.EndsWith('.')
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

        List<ArchitectureComponentRelation> relations = [];
        foreach (ArchitectureRelation baseRelation in baseModel.Relations
                     .Where(item => item.SourceId == selected.Id && !item.EvidenceIds.IsEmpty)
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            ArchitectureComponent? component = components.FirstOrDefault(candidate =>
                candidate.EvidenceIds.Intersect(
                    baseRelation.EvidenceIds,
                    StringComparer.Ordinal).Any());
            if (component is null)
            {
                continue;
            }

            relations.Add(new ArchitectureComponentRelation(
                StableIds.ForRelation(
                    component.Id,
                    baseRelation.DestinationId,
                    "c3|" + baseRelation.Id),
                component.Id,
                baseRelation.DestinationId,
                baseRelation.Description,
                baseRelation.EvidenceIds,
                ReviewStatus.RequiresReview,
                "Repository evidence supports this candidate collaboration, but runtime communication must be reviewed."));
        }

        return new ArchitectureC3Model(
            ContractSchema.Version,
            baseModel,
            selected.Id,
            [.. components.Take(ArchitectureC3Validator.MaxComponents)],
            [.. relations.Take(ArchitectureC3Validator.MaxRelations)]);
    }
}
