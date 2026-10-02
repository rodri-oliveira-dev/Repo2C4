using System.Collections.Immutable;
using System.Globalization;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.ExternalIntegrations;

/// <summary>Optional explicit project-to-origin decisions used after evidence-based correlation.</summary>
public sealed record ExternalIntegrationMappingOptions
{
    public ImmutableDictionary<string, string> ProjectOriginElementIds
    {
        get; init;
    } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
}

/// <summary>Adds conservative external dependency proposals to an existing reviewed C1/C2 model.</summary>
public static class ExternalIntegrationArchitectureMapper
{
    public static ArchitectureModel Map(
        ArchitectureModel model,
        ExternalIntegrationEvidenceResult externalEvidence,
        string focalSystemId,
        ExternalIntegrationMappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(externalEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(focalSystemId);
        options ??= new ExternalIntegrationMappingOptions();

        _ = ContractJson.SerializeModel(model);
        ArchitectureElement focalSystem = ValidateOptions(model, focalSystemId, options);

        RepositorySnapshot snapshot = MergeEvidence(model.Snapshot, externalEvidence.RepositoryEvidence);
        Dictionary<string, Evidence> originalEvidenceById = model.Snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, ArchitectureElement> elements = model.Elements
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, ArchitectureRelation> relations = model.Relations
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        foreach (ExternalIntegrationEvidence evidence in externalEvidence.Evidence
                     .Where(IsSupported)
                     .Where(item => !string.IsNullOrWhiteSpace(item.Target))
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            OriginResolution origin = ResolveOrigin(model, focalSystem, evidence, originalEvidenceById, options);
            if (origin.Element is null)
            {
                continue;
            }

            string destinationId = DestinationId(evidence);
            ArchitectureElement destination = CreateOrMergeDestination(elements, destinationId, evidence);
            elements[destinationId] = destination;

            string description = RelationDescription(evidence);
            string relationId = StableIds.ForRelation(
                origin.Element.Id,
                destinationId,
                "external:" + evidence.Kind.ToString() + ":" + evidence.Direction.ToString() + ":" +
                evidence.Technology + ":" + evidence.ResourceType + ":" + evidence.Target);
            ArchitectureRelation relation = CreateOrMergeRelation(
                relations,
                relationId,
                origin,
                destination,
                evidence,
                description);
            relations[relationId] = relation;
        }

        ArchitectureModel result = model with
        {
            Snapshot = snapshot,
            Elements = [.. elements.Values.OrderBy(item => item.Id, StringComparer.Ordinal)],
            Relations = [.. relations.Values.OrderBy(item => item.Id, StringComparer.Ordinal)],
        };

        _ = ContractJson.SerializeModel(result);
        return result;
    }

    private static RepositorySnapshot MergeEvidence(
        RepositorySnapshot snapshot,
        ImmutableArray<Evidence> importedEvidence)
    {
        Dictionary<string, Evidence> evidence = snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (Evidence imported in importedEvidence)
        {
            if (evidence.TryGetValue(imported.Id, out Evidence? existing) && existing != imported)
            {
                throw new InvalidOperationException("Imported external evidence conflicts with an existing evidence ID.");
            }

            evidence[imported.Id] = imported;
        }

        return snapshot with
        {
            Evidence = [.. evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal)]
        };
    }

    private static OriginResolution ResolveOrigin(
        ArchitectureModel model,
        ArchitectureElement focalSystem,
        ExternalIntegrationEvidence externalEvidence,
        Dictionary<string, Evidence> evidenceById,
        ExternalIntegrationMappingOptions options)
    {
        if (model.Level == ArchitectureLevel.C1)
        {
            return new OriginResolution(focalSystem, true, null);
        }

        ArchitectureElement[] provenanceMatches =
        [
            .. model.Elements
                .Where(element => element.Kind == ArchitectureElementKind.Container && element.ParentId == focalSystem.Id)
                .Where(element => element.EvidenceIds.Any(id =>
                    evidenceById.TryGetValue(id, out Evidence? evidence) &&
                    string.Equals(evidence.RelativePath, externalEvidence.ProjectPath, StringComparison.Ordinal)))
                .OrderBy(element => element.Id, StringComparer.Ordinal),
        ];

        if (provenanceMatches.Length == 1)
        {
            return new OriginResolution(provenanceMatches[0], true, null);
        }

        if (options.ProjectOriginElementIds.TryGetValue(externalEvidence.ProjectPath, out string? explicitId))
        {
            ArchitectureElement element = model.Elements.Single(item => item.Id == explicitId);
            return new OriginResolution(element, true, null);
        }

        string reason = provenanceMatches.Length > 1
            ? "Multiple containers reference the finding project; the focal software system is used as a review-only origin."
            : "The finding project is not explicitly correlated to a model element; the focal software system is used as a review-only origin.";
        return new OriginResolution(focalSystem, false, reason);
    }

    private static ArchitectureElement CreateOrMergeDestination(
        Dictionary<string, ArchitectureElement> elements,
        string destinationId,
        ExternalIntegrationEvidence evidence)
    {
        bool confirmed = evidence.Confidence == ExternalIntegrationConfidence.High;
        if (!elements.TryGetValue(destinationId, out ArchitectureElement? existing))
        {
            return new ArchitectureElement(
                destinationId,
                ArchitectureElementKind.SoftwareSystem,
                evidence.Target!,
                null,
                [evidence.Id],
                confirmed ? ReviewStatus.Confirmed : ReviewStatus.RequiresReview,
                confirmed ? null : ConfidenceReviewReason(evidence));
        }

        if (existing.Kind != ArchitectureElementKind.SoftwareSystem || existing.ParentId is not null ||
            !string.Equals(existing.Name, evidence.Target, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A generated external target ID conflicts with an existing architecture element.");
        }

        ImmutableArray<string> evidenceIds = AddId(existing.EvidenceIds, evidence.Id);
        if (existing.Status == ReviewStatus.Confirmed || !confirmed)
        {
            return existing with
            {
                EvidenceIds = evidenceIds
            };
        }

        return existing with
        {
            EvidenceIds = evidenceIds,
            Status = ReviewStatus.Confirmed,
            ReviewReason = null,
        };
    }

    private static ArchitectureRelation CreateOrMergeRelation(
        Dictionary<string, ArchitectureRelation> relations,
        string relationId,
        OriginResolution origin,
        ArchitectureElement destination,
        ExternalIntegrationEvidence evidence,
        string description)
    {
        bool confirmed = origin.Exact &&
            origin.Element!.Status == ReviewStatus.Confirmed &&
            destination.Status == ReviewStatus.Confirmed &&
            evidence.Confidence == ExternalIntegrationConfidence.High;
        string? reviewReason = confirmed ? null : RelationReviewReason(origin, destination, evidence);

        if (!relations.TryGetValue(relationId, out ArchitectureRelation? existing))
        {
            return new ArchitectureRelation(
                relationId,
                origin.Element!.Id,
                destination.Id,
                description,
                [evidence.Id],
                confirmed ? ReviewStatus.Confirmed : ReviewStatus.RequiresReview,
                reviewReason);
        }

        if (!string.Equals(existing.SourceId, origin.Element!.Id, StringComparison.Ordinal) ||
            !string.Equals(existing.DestinationId, destination.Id, StringComparison.Ordinal) ||
            !string.Equals(existing.Description, description, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A generated external relation ID conflicts with an existing architecture relation.");
        }

        ImmutableArray<string> evidenceIds = AddId(existing.EvidenceIds, evidence.Id);
        if (existing.Status == ReviewStatus.Confirmed || !confirmed)
        {
            return existing with
            {
                EvidenceIds = evidenceIds
            };
        }

        return existing with
        {
            EvidenceIds = evidenceIds,
            Status = ReviewStatus.Confirmed,
            ReviewReason = null,
        };
    }

    private static string RelationReviewReason(
        OriginResolution origin,
        ArchitectureElement destination,
        ExternalIntegrationEvidence evidence)
    {
        if (!origin.Exact)
        {
            return origin.ReviewReason!;
        }

        if (origin.Element!.Status == ReviewStatus.RequiresReview)
        {
            return "The correlated source element still requires architectural review.";
        }

        if (destination.Status == ReviewStatus.RequiresReview || evidence.Confidence != ExternalIntegrationConfidence.High)
        {
            return ConfidenceReviewReason(evidence);
        }

        return "The external integration requires architectural review.";
    }

    private static string ConfidenceReviewReason(ExternalIntegrationEvidence evidence) =>
        "The external target is supported with " + evidence.Confidence.ToString().ToLowerInvariant() +
        " confidence; confirm its identity and runtime use.";

    private static string DestinationId(ExternalIntegrationEvidence evidence) =>
        StableIds.ForElement(
            ArchitectureElementKind.SoftwareSystem,
            "external:" + evidence.Kind.ToString() + ":" + evidence.Technology + ":" +
            evidence.ResourceType + ":" + evidence.Target);

    private static string RelationDescription(ExternalIntegrationEvidence evidence)
    {
        string technology = TechnologyName(evidence.Technology);
        return (evidence.Kind, evidence.Direction) switch
        {
            (ExternalIntegrationKind.Http, ExternalIntegrationDirection.Outbound) =>
                "Calls " + evidence.Target + " via HTTP (" + technology + ")",
            (ExternalIntegrationKind.Database, _) =>
                "Uses " + technology + " database " + evidence.Target,
            (ExternalIntegrationKind.Cache, _) =>
                "Uses " + technology + " cache " + evidence.Target,
            (ExternalIntegrationKind.Storage, ExternalIntegrationDirection.Write) =>
                "Writes objects to " + TechnologyAndResource(technology, evidence) + " " + evidence.Target,
            (ExternalIntegrationKind.Storage, ExternalIntegrationDirection.Read) =>
                "Reads objects from " + TechnologyAndResource(technology, evidence) + " " + evidence.Target,
            (ExternalIntegrationKind.Storage, _) =>
                "Uses " + TechnologyAndResource(technology, evidence) + " " + evidence.Target,
            _ => throw new InvalidOperationException("Unsupported external integration mapping."),
        };
    }

    private static string TechnologyAndResource(string technology, ExternalIntegrationEvidence evidence) =>
        evidence.ResourceType is null ? technology + " storage" : technology + " " + evidence.ResourceType;

    private static string TechnologyName(string technology) => technology.ToLowerInvariant() switch
    {
        "amazon-s3" => "Amazon S3",
        "azure-blob" => "Azure Blob",
        "bigquery" => "BigQuery",
        "gcs" or "google-cloud-storage" => "GCS",
        "httpclient" => "HttpClient",
        "postgresql" => "PostgreSQL",
        "redis" => "Redis",
        "refit" => "Refit",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(technology.Replace('-', ' ')),
    };

    private static bool IsSupported(ExternalIntegrationEvidence evidence) => evidence.Kind switch
    {
        ExternalIntegrationKind.Http => evidence.Direction == ExternalIntegrationDirection.Outbound,
        ExternalIntegrationKind.Database or ExternalIntegrationKind.Cache or ExternalIntegrationKind.Storage => true,
        _ => false,
    };

    private static ImmutableArray<string> AddId(ImmutableArray<string> ids, string id) =>
        [.. ids.Append(id).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)];

    private static ArchitectureElement ValidateOptions(
        ArchitectureModel model,
        string focalSystemId,
        ExternalIntegrationMappingOptions options)
    {
        ArchitectureElement? focalSystem = model.Elements.SingleOrDefault(item => item.Id == focalSystemId);
        if (focalSystem is null || focalSystem.Kind != ArchitectureElementKind.SoftwareSystem || focalSystem.ParentId is not null)
        {
            throw new ArgumentException("The focal system must reference a root software system in the model.", nameof(focalSystemId));
        }

        foreach ((string projectPath, string elementId) in options.ProjectOriginElementIds)
        {
            if (!ContractValidator.IsNormalizedRelativePath(projectPath))
            {
                throw new ArgumentException("Explicit project paths must be normalized repository-relative paths.", nameof(options));
            }

            ArchitectureElement? element = model.Elements.SingleOrDefault(item => item.Id == elementId);
            ArchitectureElementKind expectedKind = model.Level == ArchitectureLevel.C2
                ? ArchitectureElementKind.Container
                : ArchitectureElementKind.SoftwareSystem;
            if (element is null || element.Kind != expectedKind ||
                (model.Level == ArchitectureLevel.C2 && element.ParentId != focalSystemId) ||
                (model.Level == ArchitectureLevel.C1 && element.Id != focalSystemId))
            {
                throw new ArgumentException("Every explicit project origin must reference an eligible model element.", nameof(options));
            }
        }

        return focalSystem;
    }

    private sealed record OriginResolution(
        ArchitectureElement? Element,
        bool Exact,
        string? ReviewReason);
}
