using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

/// <summary>Current stable JSON contract version. Incompatible changes require a new major version.</summary>
public static class ContractSchema
{
    public const string Version = "1.0";
}

/// <summary>Origin of an observed fact, rather than a model-generated conclusion.</summary>
public enum EvidenceSourceType
{
    ProjectFile,
    SourceCode,
    Configuration,
    Documentation,
    Manifest,
}

/// <summary>Classification of an architectural element; projects are not automatically containers.</summary>
public enum ArchitectureElementKind
{
    Actor,
    SoftwareSystem,
    Container,
}

/// <summary>Confirmed means supported by referenced evidence, not independently verified semantics.</summary>
public enum ReviewStatus
{
    Confirmed,
    RequiresReview,
}

/// <summary>The diagram scope explicitly separates context (C1) and containers (C2).</summary>
public enum ArchitectureLevel
{
    C1,
    C2,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Metadata only; a snapshot never embeds source text or secrets.</summary>
public sealed record RepositoryFile(string RelativePath, long SizeBytes, string? Sha256);

/// <summary>A verifiable fact tied to a repository-relative source, with an optional 1-based line.</summary>
public sealed record Evidence(
    string Id,
    string Category,
    string RelativePath,
    int? Line,
    EvidenceSourceType SourceType,
    string Description);

/// <summary>A bounded-scan diagnostic; no raw repository content is included.</summary>
public sealed record RepositoryDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string? RelativePath,
    string Message);

/// <summary>Repository inventory and observed facts; RepositoryId must be stable and contain no local absolute path.</summary>
public sealed record RepositorySnapshot(
    string SchemaVersion,
    string RepositoryId,
    ImmutableArray<RepositoryFile> Files,
    ImmutableArray<Evidence> Evidence,
    ImmutableArray<RepositoryDiagnostic> Diagnostics);

/// <summary>Containment is represented by ParentId and validated against the model's element set.</summary>
public sealed record ArchitectureElement(
    string Id,
    ArchitectureElementKind Kind,
    string Name,
    string? ParentId,
    ImmutableArray<string> EvidenceIds,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>A directed relationship between existing elements, with explicit provenance or a review flag.</summary>
public sealed record ArchitectureRelation(
    string Id,
    string SourceId,
    string DestinationId,
    string Description,
    ImmutableArray<string> EvidenceIds,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>A reviewable C1/C2 model tied to the exact inventory and evidence used to produce it.</summary>
public sealed record ArchitectureModel(
    string SchemaVersion,
    ArchitectureLevel Level,
    RepositorySnapshot Snapshot,
    ImmutableArray<ArchitectureElement> Elements,
    ImmutableArray<ArchitectureRelation> Relations);
