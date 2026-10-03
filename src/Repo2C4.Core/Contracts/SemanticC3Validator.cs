using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

/// <summary>Validates the additive Semantic C3 contract without changing the stable C1/C2 v1 contract.</summary>
public static class SemanticC3Validator
{
    public static ImmutableArray<ContractError> Validate(SemanticC3Proposal? proposal)
    {
        List<ContractError> errors = [];

        if (proposal is null)
        {
            errors.Add(new ContractError("semanticC3.modelMissing", "$", "Semantic C3 proposal is required."));
            return [.. errors];
        }

        if (proposal.SchemaVersion != SemanticC3ContractSchema.Version)
        {
            errors.Add(new ContractError(
                "schema.unsupported",
                "$.schemaVersion",
                "Only Semantic C3 schema version " + SemanticC3ContractSchema.Version + " is supported."));
        }

        if (proposal.SelectedContainerIds.IsDefault ||
            proposal.Components.IsDefault ||
            proposal.Relations.IsDefault)
        {
            errors.Add(new ContractError(
                "collection.missing",
                "$",
                "Semantic C3 selected containers, components and relations must be initialized collections."));
            return [.. errors];
        }

        HashSet<string> selectedContainers = ValidateSelection(proposal.SelectedContainerIds, errors);
        HashSet<string> componentIds = ValidateComponents(proposal.Components, selectedContainers, errors);
        ValidateRelations(proposal.Relations, componentIds, errors);

        return [.. errors];
    }

    private static HashSet<string> ValidateSelection(
        ImmutableArray<string> selectedContainerIds,
        List<ContractError> errors)
    {
        HashSet<string> selected = new(StringComparer.Ordinal);

        if (selectedContainerIds.IsEmpty)
        {
            errors.Add(new ContractError(
                "semanticC3.selectionMissing",
                "$.selectedContainerIds",
                "At least one C2 container must be selected for Semantic C3."));
            return selected;
        }

        for (int i = 0; i < selectedContainerIds.Length; i++)
        {
            string containerId = selectedContainerIds[i];
            string path = "$.selectedContainerIds[" + i + "]";

            if (string.IsNullOrWhiteSpace(containerId))
            {
                errors.Add(new ContractError("id.required", path, "Selected container ID is required."));
                continue;
            }

            if (!selected.Add(containerId))
            {
                errors.Add(new ContractError(
                    "semanticC3.selectionDuplicate",
                    path,
                    "Selected Semantic C3 container IDs must be unique."));
            }
        }

        return selected;
    }

    private static HashSet<string> ValidateComponents(
        ImmutableArray<SemanticC3ComponentCandidate> components,
        HashSet<string> selectedContainers,
        List<ContractError> errors)
    {
        HashSet<string> componentIds = new(StringComparer.Ordinal);

        for (int i = 0; i < components.Length; i++)
        {
            SemanticC3ComponentCandidate? component = components[i];
            string path = "$.components[" + i + "]";

            if (component is null)
            {
                errors.Add(new ContractError(
                    "semanticC3.componentMissing",
                    path,
                    "Semantic C3 component candidate must not be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(component.Id))
            {
                errors.Add(new ContractError("id.required", path + ".id", "Component ID is required."));
            }
            else if (!componentIds.Add(component.Id))
            {
                errors.Add(new ContractError("id.duplicate", path + ".id", "Component IDs must be unique."));
            }

            if (string.IsNullOrWhiteSpace(component.ContainerId) ||
                !selectedContainers.Contains(component.ContainerId))
            {
                errors.Add(new ContractError(
                    "semanticC3.containerScope",
                    path + ".containerId",
                    "Every component must belong to one of the explicitly selected containers."));
            }

            if (!Enum.IsDefined(component.Category))
            {
                errors.Add(new ContractError(
                    "semanticC3.category",
                    path + ".category",
                    "Unknown Semantic C3 component category."));
            }

            if (string.IsNullOrWhiteSpace(component.Name) || string.IsNullOrWhiteSpace(component.Responsibility))
            {
                errors.Add(new ContractError(
                    "text.required",
                    path,
                    "Component name and responsibility are required."));
            }

            ValidateSourceSymbol(component.SourceSymbol, path + ".sourceSymbol", errors);
            ValidateProvenance(
                component.EvidenceIds,
                component.Status,
                component.ReviewReason,
                path,
                errors);
        }

        return componentIds;
    }

    private static void ValidateRelations(
        ImmutableArray<SemanticC3RelationCandidate> relations,
        HashSet<string> componentIds,
        List<ContractError> errors)
    {
        HashSet<string> relationIds = new(StringComparer.Ordinal);

        for (int i = 0; i < relations.Length; i++)
        {
            SemanticC3RelationCandidate? relation = relations[i];
            string path = "$.relations[" + i + "]";

            if (relation is null)
            {
                errors.Add(new ContractError(
                    "semanticC3.relationMissing",
                    path,
                    "Semantic C3 relation candidate must not be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(relation.Id))
            {
                errors.Add(new ContractError("id.required", path + ".id", "Relation ID is required."));
            }
            else if (!relationIds.Add(relation.Id))
            {
                errors.Add(new ContractError("id.duplicate", path + ".id", "Relation IDs must be unique."));
            }

            if (string.IsNullOrWhiteSpace(relation.SourceComponentId) ||
                !componentIds.Contains(relation.SourceComponentId))
            {
                errors.Add(new ContractError(
                    "relation.sourceMissing",
                    path + ".sourceComponentId",
                    "Semantic C3 relation source must be an existing component candidate."));
            }

            if (!Enum.IsDefined(relation.DestinationKind))
            {
                errors.Add(new ContractError(
                    "semanticC3.destinationKind",
                    path + ".destinationKind",
                    "Unknown Semantic C3 relation destination kind."));
            }
            else if (relation.DestinationKind == SemanticC3RelationTargetKind.Component &&
                     (string.IsNullOrWhiteSpace(relation.DestinationId) ||
                      !componentIds.Contains(relation.DestinationId)))
            {
                errors.Add(new ContractError(
                    "relation.destinationMissing",
                    path + ".destinationId",
                    "Component relation destination must be an existing component candidate."));
            }
            else if (relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement &&
                     string.IsNullOrWhiteSpace(relation.DestinationId))
            {
                errors.Add(new ContractError(
                    "relation.destinationMissing",
                    path + ".destinationId",
                    "External relation destination must identify an existing C1/C2 element."));
            }

            if (string.IsNullOrWhiteSpace(relation.Description))
            {
                errors.Add(new ContractError(
                    "text.required",
                    path + ".description",
                    "Relation description is required."));
            }

            ValidateProvenance(
                relation.EvidenceIds,
                relation.Status,
                relation.ReviewReason,
                path,
                errors);
        }
    }

    private static void ValidateSourceSymbol(
        SemanticC3SourceSymbolIdentity? sourceSymbol,
        string path,
        List<ContractError> errors)
    {
        if (sourceSymbol is null)
        {
            return;
        }

        bool validPath = ContractValidator.IsNormalizedRelativePath(sourceSymbol.ProjectPath);
        if (!validPath)
        {
            errors.Add(new ContractError(
                "semanticC3.sourceSymbolPath",
                path + ".projectPath",
                "Source symbol project path must be a normalized repository-relative path."));
        }

        if (string.IsNullOrWhiteSpace(sourceSymbol.SymbolId))
        {
            errors.Add(new ContractError(
                "semanticC3.sourceSymbolIdentity",
                path + ".symbolId",
                "Source symbol semantic identity is required."));
        }

        if (string.IsNullOrWhiteSpace(sourceSymbol.Id))
        {
            errors.Add(new ContractError("id.required", path + ".id", "Source symbol ID is required."));
        }
        else if (validPath && !string.IsNullOrWhiteSpace(sourceSymbol.SymbolId))
        {
            string expectedId = StableIds.ForSemanticC3SourceSymbol(
                sourceSymbol.ProjectPath,
                sourceSymbol.SymbolId);

            if (!string.Equals(sourceSymbol.Id, expectedId, StringComparison.Ordinal))
            {
                errors.Add(new ContractError(
                    "semanticC3.sourceSymbolId",
                    path + ".id",
                    "Source symbol ID must be derived from its stable project path and semantic symbol identity."));
            }
        }
    }

    private static void ValidateProvenance(
        ImmutableArray<string> evidenceIds,
        ReviewStatus status,
        string? reviewReason,
        string path,
        List<ContractError> errors)
    {
        if (evidenceIds.IsDefault)
        {
            errors.Add(new ContractError(
                "collection.missing",
                path + ".evidenceIds",
                "Evidence IDs must be initialized."));
            return;
        }

        if (!Enum.IsDefined(status))
        {
            errors.Add(new ContractError("review.status", path + ".status", "Unknown review status."));
        }

        if (status == ReviewStatus.Confirmed && evidenceIds.IsEmpty)
        {
            errors.Add(new ContractError(
                "review.unsubstantiated",
                path + ".status",
                "Confirmed Semantic C3 assertions must reference supporting evidence."));
        }

        if (status == ReviewStatus.RequiresReview && string.IsNullOrWhiteSpace(reviewReason))
        {
            errors.Add(new ContractError(
                "review.reason",
                path + ".reviewReason",
                "Semantic C3 assertions requiring review must explain why."));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < evidenceIds.Length; i++)
        {
            string evidenceId = evidenceIds[i];
            string evidencePath = path + ".evidenceIds[" + i + "]";

            if (string.IsNullOrWhiteSpace(evidenceId))
            {
                errors.Add(new ContractError(
                    "id.required",
                    evidencePath,
                    "Evidence ID is required."));
            }
            else if (!seen.Add(evidenceId))
            {
                errors.Add(new ContractError(
                    "evidence.duplicateReference",
                    evidencePath,
                    "Evidence must not be referenced twice by one Semantic C3 assertion."));
            }
        }
    }
}
