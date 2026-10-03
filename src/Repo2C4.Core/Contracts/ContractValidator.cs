using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

public sealed record ContractError(string Code, string Path, string Message);

/// <summary>Actionable validation failure, with no source text or secret values in diagnostics.</summary>
public sealed class ContractValidationException : Exception
{
    public ContractValidationException(ImmutableArray<ContractError> errors)
        : base("Contract validation failed: " + string.Join("; ", errors.Select(error => error.Code + " at " + error.Path)))
    {
        Errors = errors;
    }

    public ImmutableArray<ContractError> Errors
    {
        get;
    }
}

/// <summary>Validates schema, identifiers, provenance, C1/C2 containment and referential integrity.</summary>
public static class ContractValidator
{
    public static ImmutableArray<ContractError> ValidateSnapshot(RepositorySnapshot? snapshot)
    {
        List<ContractError> errors = new();
        ValidateSnapshotInto(snapshot, "$.snapshot", errors);
        return [.. errors];
    }

    public static ImmutableArray<ContractError> ValidateModel(ArchitectureModel? model)
    {
        List<ContractError> errors = new();

        if (model is null)
        {
            Add(errors, "model.missing", "$", "Architecture model is required.");
            return [.. errors];
        }

        ValidateVersion(model.SchemaVersion, "$.schemaVersion", errors);
        if (!Enum.IsDefined(model.Level))
        {
            Add(errors, "level.invalid", "$.level", "Level must be C1 or C2.");
        }

        ValidateSnapshotInto(model.Snapshot, "$.snapshot", errors);
        if (model.Elements.IsDefault || model.Relations.IsDefault)
        {
            Add(errors, "collection.missing", "$", "Elements and relations must be initialized collections.");
            return [.. errors];
        }

        Dictionary<string, ArchitectureElement> elements = new(StringComparer.Ordinal);
        HashSet<string> evidenceIds = new(StringComparer.Ordinal);
        if (model.Snapshot is not null && !model.Snapshot.Evidence.IsDefault)
        {
            foreach (Evidence evidence in model.Snapshot.Evidence)
            {
                if (evidence is not null && IsValidId(evidence.Id))
                {
                    evidenceIds.Add(evidence.Id);
                }
            }
        }

        for (int index = 0; index < model.Elements.Length; index++)
        {
            ArchitectureElement? element = model.Elements[index];
            string path = "$.elements[" + index + "]";
            if (element is null)
            {
                Add(errors, "element.missing", path, "Element must not be null.");
                continue;
            }

            CheckId(element.Id, path + ".id", errors);
            CheckText(element.Name, path + ".name", errors);
            if (!Enum.IsDefined(element.Kind))
            {
                Add(errors, "kind.invalid", path + ".kind", "Unknown architectural element kind.");
            }

            if (model.Level == ArchitectureLevel.C1 && element.Kind == ArchitectureElementKind.Container)
            {
                Add(errors, "level.container", path + ".kind", "Containers belong to C2, not C1.");
            }

            if (IsValidId(element.Id) && !elements.TryAdd(element.Id, element))
            {
                Add(errors, "id.duplicate", path + ".id", "Element ID must be unique.");
            }

            CheckProvenance(element.EvidenceIds, element.Status, element.ReviewReason, evidenceIds, path, errors);
        }

        foreach ((string id, ArchitectureElement element) in elements)
        {
            string path = "$.elements[" + id + "].parentId";
            if (element.Kind == ArchitectureElementKind.Container)
            {
                if (string.IsNullOrWhiteSpace(element.ParentId))
                {
                    Add(errors, "containment.parentRequired", path, "A C2 container must belong to a software system.");
                }
                else if (!elements.TryGetValue(element.ParentId, out ArchitectureElement? parent))
                {
                    Add(errors, "containment.parentMissing", path, "Container parent must exist.");
                }
                else if (parent.Kind != ArchitectureElementKind.SoftwareSystem)
                {
                    Add(errors, "containment.parentKind", path, "Container parent must be a software system.");
                }
            }
            else if (element.ParentId is not null)
            {
                Add(errors, "containment.root", path, "Actors and software systems must be root elements.");
            }

            HashSet<string> visited = new(StringComparer.Ordinal);
            string? cursor = id;
            while (cursor is not null && elements.TryGetValue(cursor, out ArchitectureElement? ancestor))
            {
                if (!visited.Add(cursor))
                {
                    Add(errors, "containment.cycle", path, "Containment must not form a cycle.");
                    break;
                }

                cursor = ancestor.ParentId;
            }
        }

        HashSet<string> relationIds = new(StringComparer.Ordinal);
        for (int index = 0; index < model.Relations.Length; index++)
        {
            ArchitectureRelation? relation = model.Relations[index];
            string path = "$.relations[" + index + "]";
            if (relation is null)
            {
                Add(errors, "relation.missing", path, "Relation must not be null.");
                continue;
            }

            CheckId(relation.Id, path + ".id", errors);
            CheckText(relation.Description, path + ".description", errors);
            if (IsValidId(relation.Id) && !relationIds.Add(relation.Id))
            {
                Add(errors, "id.duplicate", path + ".id", "Relation ID must be unique.");
            }

            if (!IsValidId(relation.SourceId) || !elements.ContainsKey(relation.SourceId))
            {
                Add(errors, "relation.sourceMissing", path + ".sourceId", "Relation source must be an existing element.");
            }

            if (!IsValidId(relation.DestinationId) || !elements.ContainsKey(relation.DestinationId))
            {
                Add(errors, "relation.destinationMissing", path + ".destinationId", "Relation destination must be an existing element.");
            }

            CheckProvenance(relation.EvidenceIds, relation.Status, relation.ReviewReason, evidenceIds, path, errors);
        }

        return [.. errors];
    }

    public static bool IsNormalizedRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':'))
        {
            return false;
        }

        if (path.Any(char.IsControl))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void ValidateSnapshotInto(RepositorySnapshot? snapshot, string path, List<ContractError> errors)
    {
        if (snapshot is null)
        {
            Add(errors, "snapshot.missing", path, "Repository snapshot is required.");
            return;
        }

        ValidateVersion(snapshot.SchemaVersion, path + ".schemaVersion", errors);
        CheckId(snapshot.RepositoryId, path + ".repositoryId", errors);
        if (snapshot.Files.IsDefault || snapshot.Evidence.IsDefault || snapshot.Diagnostics.IsDefault)
        {
            Add(errors, "collection.missing", path, "Files, evidence and diagnostics must be initialized collections.");
            return;
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        for (int index = 0; index < snapshot.Files.Length; index++)
        {
            RepositoryFile? file = snapshot.Files[index];
            string filePath = path + ".files[" + index + "]";
            if (file is null)
            {
                Add(errors, "file.missing", filePath, "File metadata must not be null.");
                continue;
            }

            CheckPath(file.RelativePath, filePath + ".relativePath", errors);
            if (IsNormalizedRelativePath(file.RelativePath) && !paths.Add(file.RelativePath))
            {
                Add(errors, "path.duplicate", filePath + ".relativePath", "File paths must be unique.");
            }

            if (file.SizeBytes < 0)
            {
                Add(errors, "file.size", filePath + ".sizeBytes", "File size cannot be negative.");
            }

            if (file.Sha256 is not null && (file.Sha256.Length != 64 || !file.Sha256.All(IsLowerHex)))
            {
                Add(errors, "file.hash", filePath + ".sha256", "SHA-256 must be 64 lowercase hexadecimal characters.");
            }
        }

        HashSet<string> evidenceIds = new(StringComparer.Ordinal);
        for (int index = 0; index < snapshot.Evidence.Length; index++)
        {
            Evidence? evidence = snapshot.Evidence[index];
            string evidencePath = path + ".evidence[" + index + "]";
            if (evidence is null)
            {
                Add(errors, "evidence.missing", evidencePath, "Evidence must not be null.");
                continue;
            }

            CheckId(evidence.Id, evidencePath + ".id", errors);
            if (IsValidId(evidence.Id) && !evidenceIds.Add(evidence.Id))
            {
                Add(errors, "id.duplicate", evidencePath + ".id", "Evidence ID must be unique.");
            }

            CheckText(evidence.Category, evidencePath + ".category", errors);
            CheckText(evidence.Description, evidencePath + ".description", errors);
            CheckPath(evidence.RelativePath, evidencePath + ".relativePath", errors);
            if (IsNormalizedRelativePath(evidence.RelativePath) && !paths.Contains(evidence.RelativePath))
            {
                Add(errors, "evidence.fileMissing", evidencePath + ".relativePath", "Evidence must reference an inventoried file.");
            }

            if (evidence.Line is <= 0)
            {
                Add(errors, "evidence.line", evidencePath + ".line", "Line must be a positive 1-based index.");
            }

            if (!Enum.IsDefined(evidence.SourceType))
            {
                Add(errors, "evidence.sourceType", evidencePath + ".sourceType", "Unknown evidence source type.");
            }
        }

        for (int index = 0; index < snapshot.Diagnostics.Length; index++)
        {
            RepositoryDiagnostic? diagnostic = snapshot.Diagnostics[index];
            string diagnosticPath = path + ".diagnostics[" + index + "]";
            if (diagnostic is null)
            {
                Add(errors, "diagnostic.missing", diagnosticPath, "Diagnostic must not be null.");
                continue;
            }

            CheckText(diagnostic.Code, diagnosticPath + ".code", errors);
            CheckText(diagnostic.Message, diagnosticPath + ".message", errors);
            if (diagnostic.RelativePath is not null)
            {
                CheckPath(diagnostic.RelativePath, diagnosticPath + ".relativePath", errors);
            }

            if (!Enum.IsDefined(diagnostic.Severity))
            {
                Add(errors, "diagnostic.severity", diagnosticPath + ".severity", "Unknown diagnostic severity.");
            }
        }

        ValidateSemanticC3Facts(snapshot.SemanticC3Facts, paths, path, errors);
        ValidateExternalIntegrationEvidence(
            snapshot.ExternalIntegrationEvidence,
            snapshot.Evidence,
            paths,
            path,
            errors);
    }

    private static void ValidateSemanticC3Facts(
        SemanticC3FactSet? factSet,
        HashSet<string> inventoriedPaths,
        string snapshotPath,
        List<ContractError> errors)
    {
        if (factSet is null)
        {
            return;
        }

        string path = snapshotPath + ".semanticC3Facts";
        if (factSet.SchemaVersion != SemanticC3ContractSchema.Version)
        {
            Add(errors, "semanticC3.schema", path + ".schemaVersion", "Unsupported Semantic C3 fact schema.");
        }

        if (factSet.Facts.IsDefault || factSet.Diagnostics.IsDefault)
        {
            Add(errors, "collection.missing", path, "Semantic C3 facts and diagnostics must be initialized.");
            return;
        }

        if (factSet.Facts.Length > 20_000)
        {
            Add(errors, "semanticC3.factLimit", path + ".facts", "Semantic C3 fact count exceeds the persisted limit.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int index = 0; index < factSet.Facts.Length; index++)
        {
            SemanticC3Fact? fact = factSet.Facts[index];
            string factPath = path + ".facts[" + index + "]";
            if (fact is null)
            {
                Add(errors, "semanticC3.factMissing", factPath, "Semantic C3 fact must not be null.");
                continue;
            }

            CheckId(fact.Id, factPath + ".id", errors);
            if (IsValidId(fact.Id) && !ids.Add(fact.Id))
            {
                Add(errors, "id.duplicate", factPath + ".id", "Semantic C3 fact IDs must be unique.");
            }

            CheckPath(fact.ProjectPath, factPath + ".projectPath", errors);
            CheckPath(fact.SourcePath, factPath + ".sourcePath", errors);
            if (IsNormalizedRelativePath(fact.ProjectPath) &&
                !inventoriedPaths.Contains(fact.ProjectPath))
            {
                Add(errors, "semanticC3.projectMissing", factPath + ".projectPath", "Semantic C3 project must exist in the inventory.");
            }

            if (IsNormalizedRelativePath(fact.SourcePath) &&
                !inventoriedPaths.Contains(fact.SourcePath))
            {
                Add(errors, "semanticC3.sourceMissing", factPath + ".sourcePath", "Semantic C3 source must exist in the inventory.");
            }

            if (fact.Line is <= 0)
            {
                Add(errors, "semanticC3.line", factPath + ".line", "Semantic C3 line must be a positive 1-based index.");
            }

            if (!Enum.IsDefined(fact.Kind))
            {
                Add(errors, "semanticC3.kind", factPath + ".kind", "Unknown Semantic C3 fact kind.");
            }

            CheckBoundedMetadataText(fact.Category, factPath + ".category", 256, errors);
            CheckBoundedMetadataText(fact.Description, factPath + ".description", 512, errors);

            if (fact.SourceSymbol is null)
            {
                Add(errors, "semanticC3.symbolMissing", factPath + ".sourceSymbol", "Semantic C3 source symbol is required.");
            }
            else
            {
                CheckId(fact.SourceSymbol.Id, factPath + ".sourceSymbol.id", errors);
                CheckPath(fact.SourceSymbol.ProjectPath, factPath + ".sourceSymbol.projectPath", errors);
                CheckBoundedMetadataText(fact.SourceSymbol.SymbolId, factPath + ".sourceSymbol.symbolId", 1_024, errors);
                if (!string.Equals(fact.SourceSymbol.ProjectPath, fact.ProjectPath, StringComparison.Ordinal))
                {
                    Add(errors, "semanticC3.symbolProject", factPath + ".sourceSymbol.projectPath", "Source symbol project must match the fact project.");
                }

                if (IsNormalizedRelativePath(fact.ProjectPath) &&
                    !string.IsNullOrWhiteSpace(fact.SourceSymbol.SymbolId))
                {
                    string stable = StableIds.ForSemanticC3SourceSymbol(
                        fact.ProjectPath,
                        fact.SourceSymbol.SymbolId);
                    if (!string.Equals(stable, fact.SourceSymbol.Id, StringComparison.Ordinal))
                    {
                        Add(errors, "semanticC3.symbolId", factPath + ".sourceSymbol.id", "Semantic C3 source symbol ID is not stable for its project/symbol identity.");
                    }
                }
            }

            if (fact.RelatedSymbolId is not null)
            {
                CheckBoundedMetadataText(fact.RelatedSymbolId, factPath + ".relatedSymbolId", 1_024, errors);
            }
        }

        for (int index = 0; index < factSet.Diagnostics.Length; index++)
        {
            RepositoryDiagnostic? diagnostic = factSet.Diagnostics[index];
            string diagnosticPath = path + ".diagnostics[" + index + "]";
            if (diagnostic is null)
            {
                Add(errors, "diagnostic.missing", diagnosticPath, "Semantic C3 diagnostic must not be null.");
                continue;
            }

            CheckBoundedMetadataText(diagnostic.Code, diagnosticPath + ".code", 256, errors);
            CheckBoundedMetadataText(diagnostic.Message, diagnosticPath + ".message", 512, errors);
            if (diagnostic.RelativePath is not null)
            {
                CheckPath(diagnostic.RelativePath, diagnosticPath + ".relativePath", errors);
            }

            if (!Enum.IsDefined(diagnostic.Severity))
            {
                Add(errors, "diagnostic.severity", diagnosticPath + ".severity", "Unknown diagnostic severity.");
            }
        }
    }

    private static void ValidateExternalIntegrationEvidence(
        Repo2C4.Core.ExternalIntegrations.ExternalIntegrationEvidenceResult? result,
        ImmutableArray<Evidence> repositoryEvidence,
        HashSet<string> inventoriedPaths,
        string snapshotPath,
        List<ContractError> errors)
    {
        if (result is null)
        {
            return;
        }

        string path = snapshotPath + ".externalIntegrationEvidence";
        if (result.Evidence.IsDefault || result.Diagnostics.IsDefault)
        {
            Add(errors, "collection.missing", path, "External integration evidence and diagnostics must be initialized.");
            return;
        }

        if (result.Evidence.Length > 5_000)
        {
            Add(errors, "external.findings.limit", path + ".evidence", "Persisted external integration evidence exceeds the bounded limit.");
        }

        Dictionary<string, Evidence> evidenceById = repositoryEvidence
            .Where(item => item is not null)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);

        for (int index = 0; index < result.Evidence.Length; index++)
        {
            Repo2C4.Core.ExternalIntegrations.ExternalIntegrationEvidence? evidence = result.Evidence[index];
            string evidencePath = path + ".evidence[" + index + "]";
            if (evidence is null)
            {
                Add(errors, "external.evidence.missing", evidencePath, "External integration evidence must not be null.");
                continue;
            }

            CheckId(evidence.Id, evidencePath + ".id", errors);
            if (IsValidId(evidence.Id) && !ids.Add(evidence.Id))
            {
                Add(errors, "id.duplicate", evidencePath + ".id", "External integration evidence IDs must be unique.");
            }

            CheckPath(evidence.ProjectPath, evidencePath + ".projectPath", errors);
            CheckPath(evidence.SourcePath, evidencePath + ".sourcePath", errors);
            if (IsNormalizedRelativePath(evidence.ProjectPath) &&
                !inventoriedPaths.Contains(evidence.ProjectPath))
            {
                Add(errors, "external.projectMissing", evidencePath + ".projectPath", "External integration project must exist in the inventory.");
            }

            if (IsNormalizedRelativePath(evidence.SourcePath) &&
                !inventoriedPaths.Contains(evidence.SourcePath))
            {
                Add(errors, "external.sourceMissing", evidencePath + ".sourcePath", "External integration source must exist in the inventory.");
            }

            if (evidence.SourceLine <= 0)
            {
                Add(errors, "external.line", evidencePath + ".sourceLine", "External integration source line must be positive.");
            }

            if (!Enum.IsDefined(evidence.Kind) ||
                !Enum.IsDefined(evidence.Direction) ||
                !Enum.IsDefined(evidence.Confidence))
            {
                Add(errors, "external.vocabulary", evidencePath, "External integration kind, direction and confidence must be supported enum values.");
            }

            CheckBoundedMetadataText(evidence.Category, evidencePath + ".category", 256, errors);
            CheckBoundedMetadataText(evidence.Description, evidencePath + ".description", 512, errors);
            CheckBoundedMetadataText(evidence.Technology, evidencePath + ".technology", 256, errors);
            CheckOptionalBoundedMetadataText(evidence.Target, evidencePath + ".target", 512, errors);
            CheckOptionalBoundedMetadataText(evidence.ResourceType, evidencePath + ".resourceType", 256, errors);
            CheckOptionalBoundedMetadataText(evidence.ConfigurationKey, evidencePath + ".configurationKey", 512, errors);
            CheckOptionalBoundedMetadataText(evidence.Contract, evidencePath + ".contract", 512, errors);

            if (evidence.Signals.IsDefault || evidence.Signals.Length is 0 or > 32)
            {
                Add(errors, "external.signals", evidencePath + ".signals", "External integration signals must be a non-empty bounded collection.");
            }
            else
            {
                for (int signalIndex = 0; signalIndex < evidence.Signals.Length; signalIndex++)
                {
                    CheckBoundedMetadataText(
                        evidence.Signals[signalIndex],
                        evidencePath + ".signals[" + signalIndex + "]",
                        512,
                        errors);
                }
            }

            if (!evidenceById.TryGetValue(evidence.Id, out Evidence? persisted) ||
                persisted != evidence.ToRepositoryEvidence())
            {
                Add(errors, "external.repositoryEvidenceMismatch", evidencePath + ".id", "Persisted external integration evidence must match repository evidence.");
            }
        }

        for (int index = 0; index < result.Diagnostics.Length; index++)
        {
            Repo2C4.Core.ExternalIntegrations.ExternalIntegrationDiagnostic? diagnostic = result.Diagnostics[index];
            string diagnosticPath = path + ".diagnostics[" + index + "]";
            if (diagnostic is null)
            {
                Add(errors, "external.diagnosticMissing", diagnosticPath, "External integration diagnostic must not be null.");
                continue;
            }

            CheckBoundedMetadataText(diagnostic.Code, diagnosticPath + ".code", 256, errors);
            CheckBoundedMetadataText(diagnostic.Path, diagnosticPath + ".path", 512, errors);
            CheckBoundedMetadataText(diagnostic.Message, diagnosticPath + ".message", 512, errors);
            if (!Enum.IsDefined(diagnostic.Severity))
            {
                Add(errors, "diagnostic.severity", diagnosticPath + ".severity", "Unknown diagnostic severity.");
            }
        }
    }

    private static void CheckBoundedMetadataText(
        string? value,
        string path,
        int maxLength,
        List<ContractError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maxLength ||
            value.Any(char.IsControl))
        {
            Add(errors, "text.invalid", path, "Metadata text must be nonempty, bounded and free of control characters.");
        }
    }

    private static void CheckOptionalBoundedMetadataText(
        string? value,
        string path,
        int maxLength,
        List<ContractError> errors)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) ||
             value.Length > maxLength ||
             value.Any(char.IsControl)))
        {
            Add(errors, "text.invalid", path, "Optional metadata text must be bounded and free of control characters.");
        }
    }

    private static void CheckProvenance(
        ImmutableArray<string> ids,
        ReviewStatus status,
        string? reason,
        HashSet<string> evidenceIds,
        string path,
        List<ContractError> errors)
    {
        if (ids.IsDefault)
        {
            Add(errors, "collection.missing", path + ".evidenceIds", "Evidence IDs must be an initialized collection.");
            return;
        }

        if (!Enum.IsDefined(status))
        {
            Add(errors, "review.status", path + ".status", "Status must be confirmed or requiresReview.");
        }

        if (status == ReviewStatus.Confirmed && ids.IsEmpty)
        {
            Add(errors, "review.unsubstantiated", path + ".status", "A confirmed assertion must reference evidence; otherwise mark it requiresReview.");
        }

        if (status == ReviewStatus.RequiresReview && string.IsNullOrWhiteSpace(reason))
        {
            Add(errors, "review.reason", path + ".reviewReason", "An inference requiring review must explain why.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < ids.Length; index++)
        {
            string? id = ids[index];
            if (!IsValidId(id) || !evidenceIds.Contains(id))
            {
                Add(errors, "evidence.referenceMissing", path + ".evidenceIds[" + index + "]", "Referenced evidence must exist in the snapshot.");
            }
            else if (!seen.Add(id))
            {
                Add(errors, "evidence.duplicateReference", path + ".evidenceIds[" + index + "]", "Evidence must not be referenced twice by one assertion.");
            }
        }
    }

    private static void ValidateVersion(string? version, string path, List<ContractError> errors)
    {
        if (version != ContractSchema.Version)
        {
            Add(errors, "schema.unsupported", path, "Only schema version " + ContractSchema.Version + " is supported.");
        }
    }

    private static void CheckPath(string? path, string location, List<ContractError> errors)
    {
        if (!IsNormalizedRelativePath(path))
        {
            Add(errors, "path.invalid", location, "Use a normalized repository-relative path with '/' separators and no traversal.");
        }
    }

    private static void CheckText(string? value, string path, List<ContractError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "text.required", path, "A nonempty descriptive value is required.");
        }
    }

    private static void CheckId(string? id, string path, List<ContractError> errors)
    {
        if (!IsValidId(id))
        {
            Add(errors, "id.invalid", path, "ID must start with a lowercase letter and contain only lowercase ASCII letters, digits, '.', '_' or '-' (max 128 characters).");
        }
    }

    private static bool IsValidId(string? id)
    {
        return !string.IsNullOrEmpty(id)
            && id.Length <= 128
            && id[0] is >= 'a' and <= 'z'
            && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void Add(List<ContractError> errors, string code, string path, string message) =>
        errors.Add(new ContractError(code, path, message));
}
