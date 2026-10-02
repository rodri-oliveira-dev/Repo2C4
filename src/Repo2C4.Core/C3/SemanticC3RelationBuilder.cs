using System.Collections.Immutable;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.C3;

/// <summary>Deterministic budgets applied while composing the bounded Semantic C3 graph.</summary>
public sealed record SemanticC3RelationBuildOptions
{
    public int MaxComponents { get; init; } = 512;

    public int MaxRelations { get; init; } = 1_024;
}

/// <summary>Semantic C3 graph plus controlled diagnostics for omitted or review-only graph assertions.</summary>
public sealed record SemanticC3RelationBuildResult(
    SemanticC3Proposal Proposal,
    ImmutableArray<RepositoryDiagnostic> Diagnostics);

/// <summary>
/// Builds bounded component-to-component and component-to-existing-architecture relations from structural facts.
/// It intentionally ignores project references, names and isolated type declarations as runtime-flow evidence.
/// </summary>
public static class SemanticC3RelationBuilder
{
    public static SemanticC3RelationBuildResult Build(
        ArchitectureModel baseModel,
        SemanticC3Proposal proposal,
        SemanticC3FactSet factSet,
        SemanticC3RelationBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(factSet);
        options ??= new SemanticC3RelationBuildOptions();

        ValidateInputs(baseModel, proposal, factSet, options);

        List<RepositoryDiagnostic> diagnostics = [];
        SemanticC3ComponentCandidate[] components =
        [
            .. proposal.Components
                .OrderBy(component => component.Id, StringComparer.Ordinal)
                .Take(options.MaxComponents),
        ];

        if (proposal.Components.Length > options.MaxComponents)
        {
            diagnostics.Add(new RepositoryDiagnostic(
                "semanticC3.componentBudget",
                DiagnosticSeverity.Warning,
                null,
                "Semantic C3 component budget reached; relation analysis used the deterministic first component subset."));
        }

        Dictionary<string, SemanticC3ComponentCandidate> componentsById =
            components.ToDictionary(component => component.Id, StringComparer.Ordinal);
        ComponentIndex componentIndex = new(components);
        SemanticC3Fact[] facts =
        [
            .. factSet.Facts
                .Where(fact => components.Any(component =>
                    component.SourceSymbol?.ProjectPath == fact.ProjectPath))
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];

        DiRegistration[] registrations =
        [
            .. facts
                .Where(fact => fact.Kind == SemanticC3FactKind.DependencyInjectionRegistration)
                .Select(ParseRegistration)
                .Where(registration => registration is not null)
                .Select(registration => registration!)
                .OrderBy(registration => registration.Fact.Id, StringComparer.Ordinal),
        ];

        Dictionary<string, List<InterfaceImplementation>> interfaces =
            BuildInterfaceIndex(facts, componentIndex);
        HandlerIndex handlerIndex = BuildHandlerIndex(facts, componentIndex);
        HashSet<string> controllerActions = facts
            .Where(fact => fact.Kind == SemanticC3FactKind.ControllerAction)
            .Select(fact => fact.SourceSymbol.Id)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<RelationKey, SignalAccumulator> signals = [];

        foreach (SemanticC3Fact fact in facts)
        {
            switch (fact.Kind)
            {
                case SemanticC3FactKind.EndpointDependency:
                    AddWiringSignal(
                        fact,
                        componentIndex.FindExact(fact.SourceSymbol),
                        ResolveDependency(
                            fact.RelatedSymbolId,
                            componentIndex,
                            registrations,
                            interfaces),
                        signals);
                    break;

                case SemanticC3FactKind.ConstructorInjection:
                    AddWiringSignal(
                        fact,
                        componentIndex.FindByMethodOwner(fact.SourceSymbol),
                        ResolveDependency(
                            fact.RelatedSymbolId,
                            componentIndex,
                            registrations,
                            interfaces),
                        signals);
                    break;

                case SemanticC3FactKind.MethodParameter:
                    SemanticC3ComponentCandidate? parameterSource =
                        handlerIndex.MethodOwners.TryGetValue(
                            fact.SourceSymbol.Id,
                            out HandlerOwner? handlerOwner)
                            ? handlerOwner.Component
                            : controllerActions.Contains(fact.SourceSymbol.Id)
                                ? componentIndex.FindByMethodOwner(fact.SourceSymbol)
                                : null;

                    if (parameterSource is not null)
                    {
                        DependencyResolution parameterDestination = ResolveDependency(
                            fact.RelatedSymbolId,
                            componentIndex,
                            registrations,
                            interfaces);
                        AddWiringSignal(
                            fact,
                            parameterSource,
                            parameterDestination,
                            signals,
                            handlerIndex.MethodOwners.TryGetValue(
                                fact.SourceSymbol.Id,
                                out HandlerOwner? owner)
                                ? owner.HandlerEvidenceId
                                : null);
                    }

                    break;

                case SemanticC3FactKind.SymbolInvocation:
                    SemanticC3ComponentCandidate? invocationSource =
                        handlerIndex.MethodOwners.TryGetValue(
                            fact.SourceSymbol.Id,
                            out HandlerOwner? invocationHandler)
                            ? invocationHandler.Component
                            : componentIndex.FindByMethodOwner(fact.SourceSymbol);
                    SemanticC3ComponentCandidate? invocationDestination =
                        ResolveInvocationDestination(fact.RelatedSymbolId, componentIndex);
                    AddInvocationSignal(
                        fact,
                        invocationSource,
                        invocationDestination,
                        signals,
                        invocationHandler?.HandlerEvidenceId);
                    break;
            }
        }

        List<SemanticC3RelationCandidate> generatedInternal =
            BuildInternalRelations(signals, diagnostics);

        List<SemanticC3RelationCandidate> allRelations = [];
        allRelations.AddRange(NormalizeExistingRelations(
            proposal.Relations,
            componentsById,
            baseModel,
            diagnostics));
        allRelations.AddRange(generatedInternal);
        allRelations.AddRange(BuildExternalRelations(
            baseModel,
            components,
            diagnostics));

        SemanticC3RelationCandidate[] normalized = NormalizeRelations(allRelations);
        SemanticC3RelationCandidate[] boundedRelations =
        [
            .. normalized
                .Where(relation =>
                    componentsById.ContainsKey(relation.SourceComponentId) &&
                    (relation.DestinationKind != SemanticC3RelationTargetKind.Component ||
                     componentsById.ContainsKey(relation.DestinationId)))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                .Take(options.MaxRelations),
        ];

        if (normalized.Length > options.MaxRelations)
        {
            diagnostics.Add(new RepositoryDiagnostic(
                "semanticC3.relationBudget",
                DiagnosticSeverity.Warning,
                null,
                "Semantic C3 relation budget reached; output was deterministically truncated."));
        }

        SemanticC3Proposal result = new(
            SemanticC3ContractSchema.Version,
            [
                .. proposal.SelectedContainerIds
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal),
            ],
            [.. components],
            [.. boundedRelations]);

        ImmutableArray<ContractError> errors = SemanticC3Validator.Validate(result);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return new SemanticC3RelationBuildResult(
            result,
            [.. diagnostics
                .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)]);
    }

    private static List<SemanticC3RelationCandidate> BuildInternalRelations(
        Dictionary<RelationKey, SignalAccumulator> signals,
        List<RepositoryDiagnostic> diagnostics)
    {
        List<InternalRelationSeed> seeds = [];

        foreach ((RelationKey key, SignalAccumulator signal) in signals
                     .OrderBy(item => item.Key.SourceId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.DestinationId, StringComparer.Ordinal))
        {
            if (key.SourceId == key.DestinationId)
            {
                diagnostics.Add(new RepositoryDiagnostic(
                    "semanticC3.selfLoopOmitted",
                    DiagnosticSeverity.Info,
                    null,
                    "A Semantic C3 self-loop without explicit architectural semantics was omitted."));
                continue;
            }

            if (signal.Source.ContainerId != signal.Destination.ContainerId)
            {
                diagnostics.Add(new RepositoryDiagnostic(
                    "semanticC3.crossContainerInternalOmitted",
                    DiagnosticSeverity.Info,
                    null,
                    "A component collaboration crossing container boundaries was not emitted as an internal C3 relation."));
                continue;
            }

            if (!IsSupportedPair(signal.Source.Category, signal.Destination.Category))
            {
                continue;
            }

            bool confirmed = signal.HasWiring && signal.HasInvocation;
            seeds.Add(new InternalRelationSeed(
                signal,
                confirmed ? ReviewStatus.Confirmed : ReviewStatus.RequiresReview,
                confirmed ? null : ReviewReason(signal),
                Description(signal.Source, signal.Destination)));
        }

        HashSet<RelationKey> omit = [];
        Dictionary<(string A, string B), List<InternalRelationSeed>> reciprocalGroups =
            seeds.GroupBy(seed => OrderedPair(seed.Signal.Source.Id, seed.Signal.Destination.Id))
                .ToDictionary(group => group.Key, group => group.ToList());

        foreach (List<InternalRelationSeed> group in reciprocalGroups.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            InternalRelationSeed[] directions =
            [
                .. group
                    .GroupBy(seed => new RelationKey(
                        seed.Signal.Source.Id,
                        seed.Signal.Destination.Id))
                    .Select(items => items.First()),
            ];
            if (directions.Length < 2)
            {
                continue;
            }

            InternalRelationSeed[] confirmed =
            [
                .. directions.Where(seed => seed.Status == ReviewStatus.Confirmed),
            ];

            if (confirmed.Length == 0)
            {
                foreach (InternalRelationSeed seed in directions)
                {
                    omit.Add(new RelationKey(
                        seed.Signal.Source.Id,
                        seed.Signal.Destination.Id));
                }

                diagnostics.Add(new RepositoryDiagnostic(
                    "semanticC3.weakCycleOmitted",
                    DiagnosticSeverity.Info,
                    null,
                    "Reciprocal review-only source references were omitted to avoid fabricating an architectural cycle."));
            }
            else if (confirmed.Length == 1)
            {
                foreach (InternalRelationSeed seed in directions.Where(seed =>
                             seed.Status == ReviewStatus.RequiresReview))
                {
                    omit.Add(new RelationKey(
                        seed.Signal.Source.Id,
                        seed.Signal.Destination.Id));
                }
            }
        }

        return
        [
            .. seeds
                .Where(seed => !omit.Contains(new RelationKey(
                    seed.Signal.Source.Id,
                    seed.Signal.Destination.Id)))
                .Select(seed => new SemanticC3RelationCandidate(
                    StableIds.ForRelation(
                        seed.Signal.Source.Id,
                        seed.Signal.Destination.Id,
                        "semantic-c3|internal|" +
                        seed.Signal.Source.Category + "|" +
                        seed.Signal.Destination.Category),
                    seed.Signal.Source.Id,
                    seed.Signal.Destination.Id,
                    SemanticC3RelationTargetKind.Component,
                    seed.Description,
                    [.. seed.Signal.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                    seed.Status,
                    seed.ReviewReason))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal),
        ];
    }

    private static IEnumerable<SemanticC3RelationCandidate> BuildExternalRelations(
        ArchitectureModel baseModel,
        SemanticC3ComponentCandidate[] components,
        List<RepositoryDiagnostic> diagnostics)
    {
        HashSet<string> architectureElementIds = baseModel.Elements
            .Select(element => element.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (SemanticC3ComponentCandidate component in components
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            ArchitectureRelation[] matches =
            [
                .. baseModel.Relations
                    .Where(relation =>
                        relation.SourceId == component.ContainerId ||
                        relation.DestinationId == component.ContainerId)
                    .Where(relation =>
                        relation.EvidenceIds.Intersect(
                            component.EvidenceIds,
                            StringComparer.Ordinal).Any())
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal),
            ];

            foreach (ArchitectureRelation relation in matches)
            {
                string destinationId = relation.SourceId == component.ContainerId
                    ? relation.DestinationId
                    : relation.SourceId;

                if (!architectureElementIds.Contains(destinationId) ||
                    destinationId == component.ContainerId)
                {
                    diagnostics.Add(new RepositoryDiagnostic(
                        "semanticC3.externalEndpointMissing",
                        DiagnosticSeverity.Warning,
                        null,
                        "A component-to-external relation was omitted because its existing C1/C2 peer is unavailable."));
                    continue;
                }

                yield return new SemanticC3RelationCandidate(
                    StableIds.ForRelation(
                        component.Id,
                        destinationId,
                        "semantic-c3|external|" + relation.Description),
                    component.Id,
                    destinationId,
                    SemanticC3RelationTargetKind.ArchitectureElement,
                    relation.Description,
                    [
                        .. component.EvidenceIds
                            .Intersect(relation.EvidenceIds, StringComparer.Ordinal)
                            .Concat(relation.EvidenceIds)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal),
                    ],
                    relation.Status == ReviewStatus.Confirmed
                        ? ReviewStatus.Confirmed
                        : ReviewStatus.RequiresReview,
                    relation.Status == ReviewStatus.Confirmed
                        ? null
                        : "The existing C1/C2 external relation still requires review; the C3 adapter association cannot be stronger than its peer mapping.");
            }
        }
    }

    private static IEnumerable<SemanticC3RelationCandidate> NormalizeExistingRelations(
        ImmutableArray<SemanticC3RelationCandidate> relations,
        Dictionary<string, SemanticC3ComponentCandidate> componentsById,
        ArchitectureModel baseModel,
        List<RepositoryDiagnostic> diagnostics)
    {
        HashSet<string> architectureElementIds = baseModel.Elements
            .Select(element => element.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (SemanticC3RelationCandidate relation in relations
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!componentsById.TryGetValue(
                    relation.SourceComponentId,
                    out SemanticC3ComponentCandidate? source))
            {
                continue;
            }

            if (relation.DestinationKind == SemanticC3RelationTargetKind.Component)
            {
                if (!componentsById.TryGetValue(
                        relation.DestinationId,
                        out SemanticC3ComponentCandidate? destination))
                {
                    diagnostics.Add(new RepositoryDiagnostic(
                        "semanticC3.internalEndpointMissing",
                        DiagnosticSeverity.Warning,
                        null,
                        "An existing Semantic C3 relation was omitted because a component endpoint is unavailable."));
                    continue;
                }

                if (source.ContainerId != destination.ContainerId)
                {
                    diagnostics.Add(new RepositoryDiagnostic(
                        "semanticC3.crossContainerInternalOmitted",
                        DiagnosticSeverity.Info,
                        null,
                        "An existing cross-container component edge was omitted from the internal C3 graph."));
                    continue;
                }
            }
            else if (!architectureElementIds.Contains(relation.DestinationId))
            {
                diagnostics.Add(new RepositoryDiagnostic(
                    "semanticC3.externalEndpointMissing",
                    DiagnosticSeverity.Warning,
                    null,
                    "An existing Semantic C3 external relation was omitted because its C1/C2 element does not exist."));
                continue;
            }

            yield return relation;
        }
    }

    private static SemanticC3RelationCandidate[] NormalizeRelations(
        IEnumerable<SemanticC3RelationCandidate> relations)
    {
        return
        [
            .. relations
                .GroupBy(
                    relation => new RelationSemanticKey(
                        relation.SourceComponentId,
                        relation.DestinationId,
                        relation.DestinationKind,
                        relation.Description))
                .Select(group =>
                {
                    SemanticC3RelationCandidate[] items =
                    [
                        .. group.OrderBy(item => item.Id, StringComparer.Ordinal),
                    ];
                    SemanticC3RelationCandidate first = items[0];
                    bool confirmed = items.Any(item => item.Status == ReviewStatus.Confirmed);
                    string stableKey = "semantic-c3|normalized|" +
                        first.DestinationKind + "|" + first.Description;

                    return new SemanticC3RelationCandidate(
                        StableIds.ForRelation(
                            first.SourceComponentId,
                            first.DestinationId,
                            stableKey),
                        first.SourceComponentId,
                        first.DestinationId,
                        first.DestinationKind,
                        first.Description,
                        [
                            .. items
                                .SelectMany(item => item.EvidenceIds)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(id => id, StringComparer.Ordinal),
                        ],
                        confirmed ? ReviewStatus.Confirmed : ReviewStatus.RequiresReview,
                        confirmed
                            ? null
                            : items
                                .Select(item => item.ReviewReason)
                                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                                .OrderBy(reason => reason, StringComparer.Ordinal)
                                .FirstOrDefault() ??
                              "The Semantic C3 relation requires architectural review.");
                })
                .OrderBy(relation => relation.Id, StringComparer.Ordinal),
        ];
    }

    private static void AddWiringSignal(
        SemanticC3Fact fact,
        SemanticC3ComponentCandidate? source,
        DependencyResolution destination,
        Dictionary<RelationKey, SignalAccumulator> signals,
        string? handlerEvidenceId = null)
    {
        if (source is null || destination.Component is null)
        {
            return;
        }

        SignalAccumulator signal = GetSignal(signals, source, destination.Component);
        signal.HasWiring = true;
        signal.EvidenceIds.Add(fact.Id);
        if (handlerEvidenceId is not null)
        {
            signal.EvidenceIds.Add(handlerEvidenceId);
        }

        foreach (string evidenceId in destination.EvidenceIds)
        {
            signal.EvidenceIds.Add(evidenceId);
        }
    }

    private static void AddInvocationSignal(
        SemanticC3Fact fact,
        SemanticC3ComponentCandidate? source,
        SemanticC3ComponentCandidate? destination,
        Dictionary<RelationKey, SignalAccumulator> signals,
        string? handlerEvidenceId)
    {
        if (source is null || destination is null)
        {
            return;
        }

        SignalAccumulator signal = GetSignal(signals, source, destination);
        signal.HasInvocation = true;
        signal.EvidenceIds.Add(fact.Id);
        if (handlerEvidenceId is not null)
        {
            signal.EvidenceIds.Add(handlerEvidenceId);
        }
    }

    private static SignalAccumulator GetSignal(
        Dictionary<RelationKey, SignalAccumulator> signals,
        SemanticC3ComponentCandidate source,
        SemanticC3ComponentCandidate destination)
    {
        RelationKey key = new(source.Id, destination.Id);
        if (!signals.TryGetValue(key, out SignalAccumulator? signal))
        {
            signal = new SignalAccumulator(source, destination);
            signals.Add(key, signal);
        }

        return signal;
    }

    private static DependencyResolution ResolveDependency(
        string? relatedSymbolId,
        ComponentIndex index,
        DiRegistration[] registrations,
        Dictionary<string, List<InterfaceImplementation>> interfaces)
    {
        string? typeName = RelatedTypeName(relatedSymbolId);
        if (typeName is null)
        {
            return DependencyResolution.Empty;
        }

        string simple = SimpleTypeName(typeName);
        HashSet<string> evidenceIds = new(StringComparer.Ordinal);

        DiRegistration[] registrationMatches =
        [
            .. registrations.Where(registration =>
                SameType(registration.ServiceType, typeName) ||
                SameType(registration.ImplementationType, typeName)),
        ];

        foreach (DiRegistration registration in registrationMatches)
        {
            SemanticC3ComponentCandidate? component =
                index.FindByTypeName(registration.ImplementationType);
            if (component is null)
            {
                continue;
            }

            evidenceIds.Add(registration.Fact.Id);
            AddInterfaceEvidence(interfaces, simple, component.Id, evidenceIds);
            return new DependencyResolution(component, [.. evidenceIds]);
        }

        SemanticC3ComponentCandidate? direct = index.FindByTypeName(typeName);
        if (direct is not null)
        {
            AddInterfaceEvidence(interfaces, simple, direct.Id, evidenceIds);
            return new DependencyResolution(direct, [.. evidenceIds]);
        }

        if (interfaces.TryGetValue(simple, out List<InterfaceImplementation>? implementations))
        {
            InterfaceImplementation[] unique =
            [
                .. implementations
                    .GroupBy(item => item.Component.Id, StringComparer.Ordinal)
                    .Select(group => group.First()),
            ];
            if (unique.Length == 1)
            {
                evidenceIds.Add(unique[0].Fact.Id);
                return new DependencyResolution(
                    unique[0].Component,
                    [.. evidenceIds]);
            }
        }

        return DependencyResolution.Empty;
    }

    private static SemanticC3ComponentCandidate? ResolveInvocationDestination(
        string? relatedSymbolId,
        ComponentIndex index)
    {
        string? targetType = RelatedMethodTypeName(relatedSymbolId);
        return targetType is null
            ? null
            : index.FindByTypeName(targetType);
    }

    private static Dictionary<string, List<InterfaceImplementation>> BuildInterfaceIndex(
        SemanticC3Fact[] facts,
        ComponentIndex index)
    {
        Dictionary<string, List<InterfaceImplementation>> result =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (SemanticC3Fact fact in facts.Where(item =>
                     item.Kind == SemanticC3FactKind.ImplementedInterface))
        {
            string? interfaceName = RelatedInterfaceName(fact.RelatedSymbolId);
            SemanticC3ComponentCandidate? component = index.FindExact(fact.SourceSymbol);
            if (interfaceName is null || component is null)
            {
                continue;
            }

            string key = SimpleTypeName(interfaceName);
            if (!result.TryGetValue(key, out List<InterfaceImplementation>? list))
            {
                list = [];
                result.Add(key, list);
            }

            list.Add(new InterfaceImplementation(component, fact));
        }

        return result;
    }

    private static void AddInterfaceEvidence(
        Dictionary<string, List<InterfaceImplementation>> interfaces,
        string interfaceName,
        string componentId,
        HashSet<string> evidenceIds)
    {
        if (!interfaces.TryGetValue(
                SimpleTypeName(interfaceName),
                out List<InterfaceImplementation>? implementations))
        {
            return;
        }

        foreach (InterfaceImplementation implementation in implementations.Where(item =>
                     item.Component.Id == componentId))
        {
            evidenceIds.Add(implementation.Fact.Id);
        }
    }

    private static HandlerIndex BuildHandlerIndex(
        SemanticC3Fact[] facts,
        ComponentIndex componentIndex)
    {
        Dictionary<string, HandlerOwner> owners = new(StringComparer.Ordinal);

        foreach (SemanticC3Fact handler in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.EndpointHandler))
        {
            SemanticC3ComponentCandidate? endpoint =
                componentIndex.FindExact(handler.SourceSymbol);
            if (endpoint is null ||
                endpoint.Category != SemanticC3ComponentCategory.HttpEndpoint)
            {
                continue;
            }

            SemanticC3Fact[] methods =
            [
                .. facts.Where(fact =>
                    fact.Kind == SemanticC3FactKind.MethodDeclaration &&
                    MatchesMethodTarget(
                        fact.SourceSymbol.SymbolId,
                        handler.RelatedSymbolId)),
            ];
            if (methods.Length == 1)
            {
                owners[methods[0].SourceSymbol.Id] =
                    new HandlerOwner(endpoint, handler.Id);
            }
        }

        return new HandlerIndex(owners);
    }

    private static bool IsSupportedPair(
        SemanticC3ComponentCategory source,
        SemanticC3ComponentCategory destination) =>
        (source, destination) switch
        {
            (SemanticC3ComponentCategory.HttpEndpoint,
                SemanticC3ComponentCategory.ApplicationService) => true,
            (SemanticC3ComponentCategory.ApplicationService,
                SemanticC3ComponentCategory.PersistenceAdapter) => true,
            (SemanticC3ComponentCategory.ApplicationService,
                SemanticC3ComponentCategory.IntegrationAdapter) => true,
            (SemanticC3ComponentCategory.BackgroundWorker,
                SemanticC3ComponentCategory.MessagingPublisher) => true,
            (SemanticC3ComponentCategory.MessagingConsumer,
                SemanticC3ComponentCategory.ApplicationService) => true,
            _ => false,
        };

    private static string Description(
        SemanticC3ComponentCandidate source,
        SemanticC3ComponentCandidate destination) =>
        (source.Category, destination.Category) switch
        {
            (SemanticC3ComponentCategory.HttpEndpoint,
                SemanticC3ComponentCategory.ApplicationService) =>
                "Delegates HTTP processing to " + destination.Name,
            (SemanticC3ComponentCategory.ApplicationService,
                SemanticC3ComponentCategory.PersistenceAdapter) =>
                "Uses " + destination.Name + " for persistence",
            (SemanticC3ComponentCategory.ApplicationService,
                SemanticC3ComponentCategory.IntegrationAdapter) =>
                "Calls " + destination.Name,
            (SemanticC3ComponentCategory.BackgroundWorker,
                SemanticC3ComponentCategory.MessagingPublisher) =>
                "Publishes through " + destination.Name,
            (SemanticC3ComponentCategory.MessagingConsumer,
                SemanticC3ComponentCategory.ApplicationService) =>
                "Delegates consumed messages to " + destination.Name,
            _ => "Collaborates with " + destination.Name,
        };

    private static string ReviewReason(SignalAccumulator signal)
    {
        if (signal.HasWiring && !signal.HasInvocation)
        {
            return "DI/handler wiring supports the collaboration, but no direct symbol invocation was observed; confirm runtime flow.";
        }

        if (!signal.HasWiring && signal.HasInvocation)
        {
            return "A direct symbol invocation was observed without supporting DI/handler wiring; confirm the architectural collaboration.";
        }

        return "The available structural signals are insufficient to confirm runtime collaboration.";
    }

    private static (string A, string B) OrderedPair(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0
            ? (first, second)
            : (second, first);

    private static DiRegistration? ParseRegistration(SemanticC3Fact fact)
    {
        string? related = fact.RelatedSymbolId;
        if (related is null || !related.StartsWith("DI:", StringComparison.Ordinal))
        {
            return null;
        }

        int lifetime = related.IndexOf(':', 3);
        int arrow = related.IndexOf("->", StringComparison.Ordinal);
        int ordinal = related.LastIndexOf(':');
        if (lifetime < 0 || arrow <= lifetime || ordinal <= arrow + 2)
        {
            return null;
        }

        string service = related[(lifetime + 1)..arrow];
        string implementation = related[(arrow + 2)..ordinal];
        return string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(implementation)
            ? null
            : new DiRegistration(service, implementation, fact);
    }

    private static string? RelatedTypeName(string? related)
    {
        if (related is null || !related.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        int ordinal = related.LastIndexOf(':');
        return ordinal <= 2 ? null : related[2..ordinal];
    }

    private static string? RelatedInterfaceName(string? related)
    {
        if (related is null || !related.StartsWith("I:", StringComparison.Ordinal))
        {
            return null;
        }

        int ordinal = related.LastIndexOf(':');
        return ordinal <= 2 ? null : related[2..ordinal];
    }

    private static string? RelatedMethodTypeName(string? related)
    {
        if (related is null || !related.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        string target = related[2..];
        int method = target.LastIndexOf('.');
        return method <= 0 ? null : target[..method];
    }

    private static string? DeclaringTypeId(string methodSymbolId)
    {
        if (!methodSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        int parameters = methodSymbolId.IndexOf('(');
        string head = parameters >= 0
            ? methodSymbolId[..parameters]
            : methodSymbolId;
        int separator = head.LastIndexOf('.');
        return separator <= 2 ? null : "T:" + head[2..separator];
    }

    private static bool MatchesMethodTarget(
        string methodSymbolId,
        string? relatedTarget)
    {
        if (relatedTarget is null ||
            !relatedTarget.StartsWith("M:", StringComparison.Ordinal) ||
            !methodSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return false;
        }

        int parameters = methodSymbolId.IndexOf('(');
        string methodHead = parameters >= 0
            ? methodSymbolId[..parameters]
            : methodSymbolId;
        string target = relatedTarget[2..];
        string candidate = methodHead[2..];

        return candidate.Equals(target, StringComparison.Ordinal) ||
            candidate.EndsWith("." + target, StringComparison.Ordinal);
    }

    private static bool SameType(string left, string right) =>
        string.Equals(
            NormalizeTypeName(left),
            NormalizeTypeName(right),
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            SimpleTypeName(left),
            SimpleTypeName(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTypeName(string value)
    {
        string normalized = value.Trim()
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .TrimEnd('?');

        int generic = normalized.IndexOf('<');
        if (generic >= 0)
        {
            normalized = normalized[..generic];
        }

        while (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized[..^2];
        }

        return normalized;
    }

    private static string SimpleTypeName(string value)
    {
        string normalized = NormalizeTypeName(value);
        int dot = normalized.LastIndexOf('.');
        return dot < 0 ? normalized : normalized[(dot + 1)..];
    }

    private static void ValidateInputs(
        ArchitectureModel baseModel,
        SemanticC3Proposal proposal,
        SemanticC3FactSet factSet,
        SemanticC3RelationBuildOptions options)
    {
        ImmutableArray<ContractError> modelErrors =
            ContractValidator.ValidateModel(baseModel);
        if (!modelErrors.IsEmpty)
        {
            throw new ContractValidationException(modelErrors);
        }

        ImmutableArray<ContractError> proposalErrors =
            SemanticC3Validator.Validate(proposal);
        if (!proposalErrors.IsEmpty)
        {
            throw new ContractValidationException(proposalErrors);
        }

        if (factSet.SchemaVersion != SemanticC3ContractSchema.Version ||
            factSet.Facts.IsDefault ||
            factSet.Diagnostics.IsDefault)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "semanticC3.factsInvalid",
                    "$.facts",
                    "Semantic C3 fact set must use the supported schema and initialized collections."),
            ]);
        }

        if (options.MaxComponents <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxComponents must be positive.");
        }

        if (options.MaxRelations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxRelations must be positive.");
        }
    }

    private sealed class ComponentIndex(SemanticC3ComponentCandidate[] components)
    {
        private readonly SemanticC3ComponentCandidate[] _components = components;

        public SemanticC3ComponentCandidate? FindExact(
            SemanticC3SourceSymbolIdentity sourceSymbol)
        {
            SemanticC3ComponentCandidate[] matches =
            [
                .. _components.Where(component =>
                    component.SourceSymbol?.Id == sourceSymbol.Id),
            ];
            return matches.Length == 1 ? matches[0] : null;
        }

        public SemanticC3ComponentCandidate? FindByMethodOwner(
            SemanticC3SourceSymbolIdentity methodSymbol)
        {
            string? declaringType = DeclaringTypeId(methodSymbol.SymbolId);
            if (declaringType is null)
            {
                return FindExact(methodSymbol);
            }

            SemanticC3ComponentCandidate[] matches =
            [
                .. _components.Where(component =>
                    component.SourceSymbol?.ProjectPath == methodSymbol.ProjectPath &&
                    component.SourceSymbol.SymbolId == declaringType),
            ];
            return matches.Length == 1 ? matches[0] : null;
        }

        public SemanticC3ComponentCandidate? FindByTypeName(string typeName)
        {
            string normalized = NormalizeTypeName(typeName);
            SemanticC3ComponentCandidate[] exact =
            [
                .. _components.Where(component =>
                    component.SourceSymbol is not null &&
                    component.SourceSymbol.SymbolId == "T:" + normalized),
            ];
            if (exact.Length == 1)
            {
                return exact[0];
            }

            string simple = SimpleTypeName(normalized);
            SemanticC3ComponentCandidate[] simpleMatches =
            [
                .. _components.Where(component =>
                    component.SourceSymbol is not null &&
                    component.SourceSymbol.SymbolId.StartsWith(
                        "T:",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        SimpleTypeName(component.SourceSymbol.SymbolId[2..]),
                        simple,
                        StringComparison.OrdinalIgnoreCase)),
            ];
            return simpleMatches.Length == 1 ? simpleMatches[0] : null;
        }
    }

    private sealed class SignalAccumulator(
        SemanticC3ComponentCandidate source,
        SemanticC3ComponentCandidate destination)
    {
        public SemanticC3ComponentCandidate Source { get; } = source;

        public SemanticC3ComponentCandidate Destination { get; } = destination;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);

        public bool HasWiring
        {
            get;
            set;
        }

        public bool HasInvocation
        {
            get;
            set;
        }
    }

    private readonly record struct RelationKey(
        string SourceId,
        string DestinationId);

    private readonly record struct RelationSemanticKey(
        string SourceId,
        string DestinationId,
        SemanticC3RelationTargetKind DestinationKind,
        string Description);

    private sealed record InternalRelationSeed(
        SignalAccumulator Signal,
        ReviewStatus Status,
        string? ReviewReason,
        string Description);

    private sealed record DependencyResolution(
        SemanticC3ComponentCandidate? Component,
        ImmutableArray<string> EvidenceIds)
    {
        public static readonly DependencyResolution Empty = new(null, []);
    }

    private sealed record DiRegistration(
        string ServiceType,
        string ImplementationType,
        SemanticC3Fact Fact);

    private sealed record InterfaceImplementation(
        SemanticC3ComponentCandidate Component,
        SemanticC3Fact Fact);

    private sealed record HandlerOwner(
        SemanticC3ComponentCandidate Component,
        string HandlerEvidenceId);

    private sealed record HandlerIndex(
        Dictionary<string, HandlerOwner> MethodOwners);
}
