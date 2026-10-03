using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

/// <summary>Version of the additive Semantic C3 contract. It is independent from the public C1/C2 v1 schema.</summary>
public static class SemanticC3ContractSchema
{
    public const string Version = "1.0";
}

/// <summary>Architectural responsibility proposed for a semantically relevant .NET component candidate.</summary>
public enum SemanticC3ComponentCategory
{
    HttpEndpoint,
    ApplicationService,
    BackgroundWorker,
    MessagingConsumer,
    MessagingPublisher,
    PersistenceAdapter,
    IntegrationAdapter,
}

/// <summary>Identifies whether a C3 relation targets another component or an existing C1/C2 element.</summary>
public enum SemanticC3RelationTargetKind
{
    Component,
    ArchitectureElement,
}

/// <summary>
/// Stable source-symbol identity. ProjectPath is repository-relative and SymbolId is semantic, not line-based.
/// </summary>
public sealed record SemanticC3SourceSymbolIdentity(
    string Id,
    string ProjectPath,
    string SymbolId);

/// <summary>
/// Evidence-first component proposal. A source symbol is optional because some candidates may aggregate multiple facts.
/// </summary>
public sealed record SemanticC3ComponentCandidate(
    string Id,
    string ContainerId,
    SemanticC3ComponentCategory Category,
    string Name,
    string Responsibility,
    ImmutableArray<string> EvidenceIds,
    SemanticC3SourceSymbolIdentity? SourceSymbol,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>
/// Directed relation from a component candidate to another component or to an existing C1/C2 element.
/// </summary>
public sealed record SemanticC3RelationCandidate(
    string Id,
    string SourceComponentId,
    string DestinationId,
    SemanticC3RelationTargetKind DestinationKind,
    string Description,
    ImmutableArray<string> EvidenceIds,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>
/// Versioned Semantic C3 proposal for one or more explicitly selected C2 containers.
/// </summary>
public sealed record SemanticC3Proposal(
    string SchemaVersion,
    ImmutableArray<string> SelectedContainerIds,
    ImmutableArray<SemanticC3ComponentCandidate> Components,
    ImmutableArray<SemanticC3RelationCandidate> Relations);
