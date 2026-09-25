using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

/// <summary>Stable, reviewable component scope for one explicitly selected C2 container.</summary>
public sealed record ArchitectureComponent(
    string Id,
    string ContainerId,
    string Name,
    string Responsibility,
    ImmutableArray<string> EvidenceIds,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>Directed relation between C3 components or between a component and an existing C1/C2 element.</summary>
public sealed record ArchitectureComponentRelation(
    string Id,
    string SourceId,
    string DestinationId,
    string Description,
    ImmutableArray<string> EvidenceIds,
    ReviewStatus Status,
    string? ReviewReason);

/// <summary>Optional C3 extension bound to an existing v1 C2 model and exactly one selected container.</summary>
public sealed record ArchitectureC3Model(
    string SchemaVersion,
    ArchitectureModel BaseModel,
    string SelectedContainerId,
    ImmutableArray<ArchitectureComponent> Components,
    ImmutableArray<ArchitectureComponentRelation> Relations);
