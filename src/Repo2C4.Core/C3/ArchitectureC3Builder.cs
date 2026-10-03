using System.Collections.Immutable;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;

namespace Repo2C4.Core.C3;

public sealed record ArchitectureC3BuildResult(
    ArchitectureC3Workspace Workspace,
    SemanticC3RelationBuildResult? SemanticResult);

/// <summary>Builds bounded, evidence-first C3 proposals for explicitly selected C2 containers.</summary>
public static class ArchitectureC3Builder
{
    private static readonly (string Prefix, string Name, string Responsibility)[] LegacyCategories =
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
        IEnumerable<string> selectedContainerIds) =>
        BuildManyDetailed(baseModel, selectedContainerIds).Workspace;

    public static ArchitectureC3BuildResult BuildManyDetailed(
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
            return new ArchitectureC3BuildResult(
                new ArchitectureC3Workspace(
                    ContractSchema.Version,
                    baseModel,
                    []),
                null);
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

        List<ArchitectureC3Selection> selections = [];
        List<SemanticC3RelationBuildResult> semanticResults = [];
        foreach (string id in canonicalIds)
        {
            if (baseModel.Snapshot.SemanticC3Facts is null)
            {
                ArchitectureC3Model legacy = BuildLegacySingle(baseModel, elements[id]);
                selections.Add(new ArchitectureC3Selection(
                    legacy.SelectedContainerId,
                    legacy.Components,
                    legacy.Relations));
                continue;
            }

            SemanticC3RelationBuildResult semantic = BuildSemanticSingle(
                baseModel,
                id,
                baseModel.Snapshot.SemanticC3Facts,
                baseModel.Snapshot.ExternalIntegrationEvidence);
            semanticResults.Add(semantic);
            selections.Add(ToSelection(id, semantic.Proposal));
        }

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

        SemanticC3RelationBuildResult? combined = semanticResults.Count == 0
            ? null
            : CombineSemanticResults(canonicalIds, semanticResults);

        return new ArchitectureC3BuildResult(workspace, combined);
    }

    private static SemanticC3RelationBuildResult BuildSemanticSingle(
        ArchitectureModel baseModel,
        string selectedContainerId,
        SemanticC3FactSet factSet,
        ExternalIntegrationEvidenceResult? externalEvidence)
    {
        ExternalIntegrationEvidenceResult integrations = externalEvidence ??
            new ExternalIntegrationEvidenceResult(
                ExternalIntegrationReportSchema.MinimumVersion,
                null,
                null,
                false,
                false,
                [],
                []);

        SemanticC3Proposal http = SemanticC3HttpApplicationProposer.Propose(
            baseModel,
            selectedContainerId,
            factSet);
        SemanticC3Proposal workers = SemanticC3WorkerIntegrationProposer.Propose(
            baseModel,
            selectedContainerId,
            factSet,
            integrations);
        SemanticC3Proposal adapters = SemanticC3IntegrationProposer.Propose(
            baseModel,
            selectedContainerId,
            factSet,
            integrations);

        SemanticC3Proposal seed = MergeSemanticProposals(
            selectedContainerId,
            http,
            workers,
            adapters);

        return SemanticC3RelationBuilder.Build(
            baseModel,
            seed,
            factSet,
            new SemanticC3RelationBuildOptions
            {
                MaxComponents = ArchitectureC3Validator.MaxComponents,
                MaxRelations = ArchitectureC3Validator.MaxRelations,
            });
    }

    private static SemanticC3Proposal MergeSemanticProposals(
        string selectedContainerId,
        params SemanticC3Proposal[] proposals)
    {
        SemanticC3ComponentCandidate[] components =
        [
            .. proposals
                .SelectMany(proposal => proposal.Components)
                .GroupBy(component => component.Id, StringComparer.Ordinal)
                .Select(group =>
                {
                    SemanticC3ComponentCandidate first = group.First();
                    return first with
                    {
                        EvidenceIds =
                        [
                            .. group.SelectMany(component => component.EvidenceIds)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(id => id, StringComparer.Ordinal),
                        ],
                    };
                })
                .OrderBy(component => component.Id, StringComparer.Ordinal),
        ];

        SemanticC3RelationCandidate[] relations =
        [
            .. proposals
                .SelectMany(proposal => proposal.Relations)
                .GroupBy(relation => relation.Id, StringComparer.Ordinal)
                .Select(group =>
                {
                    SemanticC3RelationCandidate first = group.First();
                    return first with
                    {
                        EvidenceIds =
                        [
                            .. group.SelectMany(relation => relation.EvidenceIds)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(id => id, StringComparer.Ordinal),
                        ],
                    };
                })
                .OrderBy(relation => relation.Id, StringComparer.Ordinal),
        ];

        return new SemanticC3Proposal(
            SemanticC3ContractSchema.Version,
            [selectedContainerId],
            components,
            relations);
    }

    private static ArchitectureC3Selection ToSelection(
        string selectedContainerId,
        SemanticC3Proposal proposal)
    {
        ArchitectureComponent[] components =
        [
            .. proposal.Components
                .Select(component => new ArchitectureComponent(
                    component.Id,
                    component.ContainerId,
                    component.Name,
                    component.Responsibility,
                    component.EvidenceIds,
                    component.Status,
                    component.ReviewReason))
                .OrderBy(component => component.Id, StringComparer.Ordinal),
        ];

        ArchitectureComponentRelation[] relations =
        [
            .. proposal.Relations
                .Select(relation => new ArchitectureComponentRelation(
                    relation.Id,
                    relation.SourceComponentId,
                    relation.DestinationId,
                    relation.Description,
                    relation.EvidenceIds,
                    relation.Status,
                    relation.ReviewReason))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal),
        ];

        return new ArchitectureC3Selection(
            selectedContainerId,
            [.. components],
            [.. relations]);
    }

    private static SemanticC3RelationBuildResult CombineSemanticResults(
        string[] selectedContainerIds,
        List<SemanticC3RelationBuildResult> results)
    {
        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            [.. selectedContainerIds],
            [
                .. results
                    .SelectMany(result => result.Proposal.Components)
                    .OrderBy(component => component.Id, StringComparer.Ordinal),
            ],
            [
                .. results
                    .SelectMany(result => result.Proposal.Relations)
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal),
            ]);

        return new SemanticC3RelationBuildResult(
            proposal,
            [
                .. results
                    .SelectMany(result => result.Diagnostics)
                    .Distinct()
                    .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal),
            ]);
    }

    private static ArchitectureC3Model BuildLegacySingle(
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
        foreach ((string prefix, string name, string responsibility) in LegacyCategories)
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
