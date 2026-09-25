using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

public static class ArchitectureC3Validator
{
    public const int MaxComponents = 64;
    public const int MaxRelations = 128;

    public static ImmutableArray<ContractError> Validate(ArchitectureC3Model? model)
    {
        List<ContractError> errors = [];

        if (model is null)
        {
            errors.Add(new ContractError("c3.modelMissing", "$", "C3 model is required."));
            return [.. errors];
        }

        if (model.SchemaVersion != ContractSchema.Version)
        {
            errors.Add(new ContractError(
                "schema.unsupported",
                "$.schemaVersion",
                "Only schema version " + ContractSchema.Version + " is supported."));
        }

        ImmutableArray<ContractError> baseErrors = ContractValidator.ValidateModel(model.BaseModel);
        errors.AddRange(baseErrors.Select(error => error with { Path = "$.baseModel" + error.Path.TrimStart('$') }));

        if (model.BaseModel.Level != ArchitectureLevel.C2)
        {
            errors.Add(new ContractError("c3.baseLevel", "$.baseModel.level", "C3 requires a C2 base model."));
        }

        if (model.Components.IsDefault || model.Relations.IsDefault)
        {
            errors.Add(new ContractError("collection.missing", "$", "C3 components and relations must be initialized collections."));
            return [.. errors];
        }

        if (model.Components.Length > MaxComponents)
        {
            errors.Add(new ContractError(
                "c3.componentLimit",
                "$.components",
                "C3 component count exceeds the supported limit of " + MaxComponents + "."));
        }

        if (model.Relations.Length > MaxRelations)
        {
            errors.Add(new ContractError(
                "c3.relationLimit",
                "$.relations",
                "C3 relation count exceeds the supported limit of " + MaxRelations + "."));
        }

        Dictionary<string, ArchitectureElement> baseElements = model.BaseModel.Elements
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        if (!baseElements.TryGetValue(model.SelectedContainerId, out ArchitectureElement? selected) ||
            selected.Kind != ArchitectureElementKind.Container)
        {
            errors.Add(new ContractError(
                "c3.containerMissing",
                "$.selectedContainerId",
                "Selected C3 container must exist in the C2 base model."));
        }

        HashSet<string> evidenceIds = model.BaseModel.Snapshot.Evidence
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> componentIds = new(StringComparer.Ordinal);
        for (int i = 0; i < model.Components.Length; i++)
        {
            ArchitectureComponent? component = model.Components[i];
            string path = "$.components[" + i + "]";
            if (component is null)
            {
                errors.Add(new ContractError("c3.componentMissing", path, "Component must not be null."));
                continue;
            }

            if (!string.Equals(component.ContainerId, model.SelectedContainerId, StringComparison.Ordinal))
            {
                errors.Add(new ContractError(
                    "c3.containerScope",
                    path + ".containerId",
                    "Every C3 component must belong to the explicitly selected container."));
            }

            if (!componentIds.Add(component.Id))
            {
                errors.Add(new ContractError("id.duplicate", path + ".id", "Component IDs must be unique."));
            }

            if (string.IsNullOrWhiteSpace(component.Name) || string.IsNullOrWhiteSpace(component.Responsibility))
            {
                errors.Add(new ContractError("text.required", path, "Component name and responsibility are required."));
            }

            ValidateProvenance(component.EvidenceIds, component.Status, component.ReviewReason, evidenceIds, path, errors);
        }

        HashSet<string> relationIds = new(StringComparer.Ordinal);
        for (int i = 0; i < model.Relations.Length; i++)
        {
            ArchitectureComponentRelation? relation = model.Relations[i];
            string path = "$.relations[" + i + "]";
            if (relation is null)
            {
                errors.Add(new ContractError("c3.relationMissing", path, "C3 relation must not be null."));
                continue;
            }

            if (!relationIds.Add(relation.Id))
            {
                errors.Add(new ContractError("id.duplicate", path + ".id", "C3 relation IDs must be unique."));
            }

            bool sourceExists = componentIds.Contains(relation.SourceId) || baseElements.ContainsKey(relation.SourceId);
            bool destinationExists = componentIds.Contains(relation.DestinationId) || baseElements.ContainsKey(relation.DestinationId);
            if (!sourceExists)
            {
                errors.Add(new ContractError("relation.sourceMissing", path + ".sourceId", "Relation source must exist."));
            }

            if (!destinationExists)
            {
                errors.Add(new ContractError("relation.destinationMissing", path + ".destinationId", "Relation destination must exist."));
            }

            if (!componentIds.Contains(relation.SourceId) && !componentIds.Contains(relation.DestinationId))
            {
                errors.Add(new ContractError(
                    "c3.relationOutsideScope",
                    path,
                    "A C3 relation must involve at least one selected-container component."));
            }

            ValidateProvenance(relation.EvidenceIds, relation.Status, relation.ReviewReason, evidenceIds, path, errors);
        }

        return [.. errors];
    }

    private static void ValidateProvenance(
        ImmutableArray<string> ids,
        ReviewStatus status,
        string? reviewReason,
        HashSet<string> evidenceIds,
        string path,
        List<ContractError> errors)
    {
        if (ids.IsDefault)
        {
            errors.Add(new ContractError("collection.missing", path + ".evidenceIds", "Evidence IDs must be initialized."));
            return;
        }

        if (!Enum.IsDefined(status))
        {
            errors.Add(new ContractError("review.status", path + ".status", "Unknown review status."));
        }

        if (status == ReviewStatus.Confirmed && ids.IsEmpty)
        {
            errors.Add(new ContractError(
                "review.unsubstantiated",
                path + ".status",
                "Confirmed C3 assertions must reference supporting evidence."));
        }

        if (status == ReviewStatus.RequiresReview && string.IsNullOrWhiteSpace(reviewReason))
        {
            errors.Add(new ContractError(
                "review.reason",
                path + ".reviewReason",
                "C3 assertions requiring review must explain why."));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            if (!evidenceIds.Contains(id))
            {
                errors.Add(new ContractError(
                    "evidence.referenceMissing",
                    path + ".evidenceIds[" + i + "]",
                    "Referenced C3 evidence must exist in the base snapshot."));
            }
            else if (!seen.Add(id))
            {
                errors.Add(new ContractError(
                    "evidence.duplicateReference",
                    path + ".evidenceIds[" + i + "]",
                    "Evidence must not be referenced twice by one C3 assertion."));
            }
        }
    }
}
