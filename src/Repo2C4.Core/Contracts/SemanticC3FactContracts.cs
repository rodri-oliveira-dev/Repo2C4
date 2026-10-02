using System.Collections.Immutable;

namespace Repo2C4.Core.Contracts;

/// <summary>Structural .NET fact kind used as input for later Semantic C3 classification.</summary>
public enum SemanticC3FactKind
{
    TypeDeclaration,
    MethodDeclaration,
    ConstructorDeclaration,
    BaseType,
    ImplementedInterface,
    Attribute,
    DependencyInjectionRegistration,
    ConstructorInjection,
    HttpBoundary,
    ControllerAction,
    HostedService,
    PersistenceCandidate,
    MessagingCandidate,
    SymbolInvocation,
}

/// <summary>
/// A bounded, verifiable structural fact. It never contains source bodies, arbitrary literals or absolute paths.
/// </summary>
public sealed record SemanticC3Fact(
    string Id,
    string ProjectPath,
    string SourcePath,
    int? Line,
    SemanticC3SourceSymbolIdentity SourceSymbol,
    SemanticC3FactKind Kind,
    string Category,
    string Description,
    string? RelatedSymbolId);

/// <summary>Deterministic result of local Semantic C3 structural fact extraction.</summary>
public sealed record SemanticC3FactSet(
    string SchemaVersion,
    ImmutableArray<SemanticC3Fact> Facts,
    ImmutableArray<RepositoryDiagnostic> Diagnostics);
