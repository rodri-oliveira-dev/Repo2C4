using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;

namespace Repo2C4.Core.C3;

/// <summary>
/// Proposes reviewable worker, messaging and persistence Semantic C3 components.
/// Local structural facts establish internal responsibility candidates; imported integration evidence
/// supplies transport/data direction and the already-mapped C1/C2 relation supplies the external peer.
/// </summary>
public static class SemanticC3WorkerIntegrationProposer
{
    public static SemanticC3Proposal Propose(
        ArchitectureModel baseModel,
        string selectedContainerId,
        SemanticC3FactSet factSet,
        ExternalIntegrationEvidenceResult externalEvidence)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedContainerId);
        ArgumentNullException.ThrowIfNull(factSet);
        ArgumentNullException.ThrowIfNull(externalEvidence);

        ValidateInputs(baseModel, factSet, externalEvidence);

        ArchitectureElement? selected = baseModel.Elements.SingleOrDefault(element =>
            element.Id == selectedContainerId &&
            element.Kind == ArchitectureElementKind.Container);
        if (selected is null)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "semanticC3.containerMissing",
                    "$.selectedContainerIds",
                    "Selected Semantic C3 container must exist in the C2 base model."),
            ]);
        }

        HashSet<string> selectedProjects = ResolveSelectedProjects(baseModel, selected);
        SemanticC3Fact[] scopedFacts =
        [
            .. factSet.Facts
                .Where(fact => selectedProjects.Contains(fact.ProjectPath))
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];

        Dictionary<string, SemanticC3Fact> typeFacts = scopedFacts
            .Where(fact => fact.Kind == SemanticC3FactKind.TypeDeclaration)
            .GroupBy(fact => fact.SourceSymbol.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        List<SemanticC3ComponentCandidate> components = [];
        List<SemanticC3RelationCandidate> relations = [];

        WorkerCandidate[] workers = BuildWorkers(selected, scopedFacts, typeFacts);
        components.AddRange(workers.Select(worker => worker.Component));

        MessagingCandidate[] messaging = BuildMessaging(
            selected,
            scopedFacts,
            typeFacts,
            selectedProjects,
            externalEvidence);
        components.AddRange(messaging.Select(candidate => candidate.Component));

        PersistenceCandidate[] persistence = BuildPersistence(
            selected,
            scopedFacts,
            typeFacts,
            workers,
            messaging);
        components.AddRange(persistence.Select(candidate => candidate.Component));

        AddMessagingExternalRelations(
            baseModel,
            selected,
            messaging,
            relations);

        AddPersistenceExternalRelations(
            baseModel,
            selected,
            workers,
            persistence,
            selectedProjects,
            externalEvidence,
            relations);

        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            [selected.Id],
            [
                .. components
                    .GroupBy(component => component.Id, StringComparer.Ordinal)
                    .Select(group => MergeComponents(group))
                    .OrderBy(component => component.Id, StringComparer.Ordinal),
            ],
            [
                .. relations
                    .GroupBy(relation => relation.Id, StringComparer.Ordinal)
                    .Select(group => MergeRelations(group))
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal),
            ]);

        ImmutableArray<ContractError> errors = SemanticC3Validator.Validate(proposal);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return proposal;
    }

    private static WorkerCandidate[] BuildWorkers(
        ArchitectureElement selected,
        SemanticC3Fact[] facts,
        Dictionary<string, SemanticC3Fact> typeFacts)
    {
        Dictionary<string, WorkerAccumulator> workers = new(StringComparer.Ordinal);

        foreach (SemanticC3Fact fact in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.HostedService &&
                     fact.Category == "semantic.host.backgroundService"))
        {
            WorkerAccumulator accumulator = GetWorker(workers, fact.SourceSymbol);
            accumulator.DirectBoundary = true;
            accumulator.EvidenceIds.Add(fact.Id);
            if (typeFacts.TryGetValue(fact.SourceSymbol.Id, out SemanticC3Fact? typeFact))
            {
                accumulator.EvidenceIds.Add(typeFact.Id);
            }
        }

        foreach (SemanticC3Fact registration in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.HostedService &&
                     fact.Category == "semantic.wiring.hostedServiceRegistration"))
        {
            string? hostedType = ParseHostedType(registration.RelatedSymbolId);
            if (hostedType is null)
            {
                continue;
            }

            SemanticC3Fact? typeFact = ResolveConcreteType(typeFacts.Values, hostedType);
            if (typeFact is null)
            {
                continue;
            }

            WorkerAccumulator accumulator = GetWorker(workers, typeFact.SourceSymbol);
            accumulator.RegisteredBoundary = true;
            accumulator.EvidenceIds.Add(typeFact.Id);
            accumulator.EvidenceIds.Add(registration.Id);
        }

        return
        [
            .. workers.Values
                .Select(worker =>
                {
                    string typeName = SimpleTypeNameFromSymbol(worker.SourceSymbol.SymbolId);
                    string logicalName = WorkerLogicalName(typeName);
                    SemanticC3ComponentCandidate component = new(
                        StableIds.ForSemanticC3Component(
                            selected.Id,
                            SemanticC3ComponentCategory.BackgroundWorker,
                            worker.SourceSymbol.Id),
                        selected.Id,
                        SemanticC3ComponentCategory.BackgroundWorker,
                        logicalName,
                        "Runs a hosted/background processing boundary inside the selected container.",
                        [.. worker.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                        worker.SourceSymbol,
                        ReviewStatus.RequiresReview,
                        worker.DirectBoundary
                            ? "BackgroundService/IHostedService structure supports this worker boundary; its C3 responsibility remains reviewable."
                            : "Hosted-service registration points to this type, but the implementation boundary is only registration-backed and requires review.");

                    return new WorkerCandidate(
                        component,
                        worker.SourceSymbol,
                        worker.DirectBoundary,
                        worker.RegisteredBoundary);
                })
                .OrderBy(worker => worker.Component.Id, StringComparer.Ordinal),
        ];
    }

    private static MessagingCandidate[] BuildMessaging(
        ArchitectureElement selected,
        SemanticC3Fact[] facts,
        Dictionary<string, SemanticC3Fact> typeFacts,
        HashSet<string> selectedProjects,
        ExternalIntegrationEvidenceResult externalEvidence)
    {
        SemanticC3Fact[] localSignals =
        [
            .. facts
                .Where(fact =>
                    fact.Kind == SemanticC3FactKind.MessagingCandidate &&
                    IsConcreteTypeSignal(fact, typeFacts))
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];

        Dictionary<string, MessagingAccumulator> candidates = new(StringComparer.Ordinal);

        foreach (ExternalIntegrationEvidence evidence in externalEvidence.Evidence
                     .Where(item =>
                         selectedProjects.Contains(item.ProjectPath) &&
                         item.Kind == ExternalIntegrationKind.Messaging &&
                         item.Direction is ExternalIntegrationDirection.Publish or ExternalIntegrationDirection.Consume &&
                         ExternalIntegrationArchitectureMapper.IsSupported(item))
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            SemanticC3Fact? local = MatchMessagingSignal(localSignals, evidence);
            if (local is null)
            {
                continue;
            }

            SemanticC3ComponentCategory category = evidence.Direction == ExternalIntegrationDirection.Publish
                ? SemanticC3ComponentCategory.MessagingPublisher
                : SemanticC3ComponentCategory.MessagingConsumer;
            string stableKey = local.SourceSymbol.Id + "|" + evidence.Technology.ToLowerInvariant();
            string componentId = StableIds.ForSemanticC3Component(
                selected.Id,
                category,
                stableKey);

            if (!candidates.TryGetValue(componentId, out MessagingAccumulator? candidate))
            {
                candidate = new MessagingAccumulator(
                    componentId,
                    local.SourceSymbol,
                    category,
                    evidence.Technology);
                candidates.Add(componentId, candidate);
            }

            candidate.LocalEvidenceIds.Add(local.Id);
            candidate.ExternalEvidence.Add(evidence);
        }

        return
        [
            .. candidates.Values
                .Select(candidate =>
                {
                    string provider = ExternalIntegrationArchitectureMapper.MessagingProviderName(
                        candidate.Technology) ?? Humanize(candidate.Technology);
                    string directionName = candidate.Category == SemanticC3ComponentCategory.MessagingPublisher
                        ? "publisher"
                        : "consumer";
                    ExternalIntegrationConfidence weakest = candidate.ExternalEvidence
                        .Max(item => item.Confidence);
                    string confidence = weakest.ToString().ToLowerInvariant();
                    string reviewReason =
                        "Local messaging-role evidence and imported " + directionName +
                        " evidence converge on this adapter; external integration confidence is " +
                        confidence + " and the C3 boundary remains reviewable.";

                    HashSet<string> evidenceIds = new(candidate.LocalEvidenceIds, StringComparer.Ordinal);
                    foreach (ExternalIntegrationEvidence external in candidate.ExternalEvidence)
                    {
                        evidenceIds.Add(external.Id);
                    }

                    SemanticC3ComponentCandidate component = new(
                        candidate.ComponentId,
                        selected.Id,
                        candidate.Category,
                        Truncate(provider + " " + directionName + " adapter", 120),
                        candidate.Category == SemanticC3ComponentCategory.MessagingPublisher
                            ? "Publishes messages from the selected container through the evidenced messaging provider."
                            : "Consumes messages for the selected container through the evidenced messaging provider.",
                        [.. evidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                        candidate.SourceSymbol,
                        ReviewStatus.RequiresReview,
                        reviewReason);

                    return new MessagingCandidate(
                        component,
                        candidate.SourceSymbol,
                        candidate.Technology,
                        [.. candidate.ExternalEvidence.OrderBy(item => item.Id, StringComparer.Ordinal)]);
                })
                .OrderBy(candidate => candidate.Component.Id, StringComparer.Ordinal),
        ];
    }

    private static PersistenceCandidate[] BuildPersistence(
        ArchitectureElement selected,
        SemanticC3Fact[] facts,
        Dictionary<string, SemanticC3Fact> typeFacts,
        WorkerCandidate[] workers,
        MessagingCandidate[] messaging)
    {
        Dictionary<string, PersistenceAccumulator> candidates = new(StringComparer.Ordinal);

        foreach (SemanticC3Fact fact in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.PersistenceCandidate &&
                     fact.Category == "semantic.persistence.dbContext"))
        {
            PersistenceAccumulator candidate = GetPersistence(candidates, fact.SourceSymbol, PersistenceKind.DbContext);
            candidate.EvidenceIds.Add(fact.Id);
            if (typeFacts.TryGetValue(fact.SourceSymbol.Id, out SemanticC3Fact? typeFact))
            {
                candidate.EvidenceIds.Add(typeFact.Id);
            }
        }

        SemanticC3Fact[] repositoryFacts =
        [
            .. facts.Where(fact =>
                fact.Kind == SemanticC3FactKind.PersistenceCandidate &&
                fact.Category == "semantic.persistence.repositoryImplementation"),
        ];

        DiRegistration[] registrations =
        [
            .. facts
                .Where(fact => fact.Kind == SemanticC3FactKind.DependencyInjectionRegistration)
                .Select(ParseRegistration)
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.Fact.Id, StringComparer.Ordinal),
        ];

        HashSet<string> activeBoundaryTypeIds =
        [
            .. workers.Select(worker => worker.SourceSymbol.SymbolId),
            .. messaging.Select(candidate => candidate.SourceSymbol.SymbolId),
        ];

        foreach (DiRegistration registration in registrations)
        {
            SemanticC3Fact? repository = ResolvePersistenceFact(repositoryFacts, registration.ImplementationType);
            if (repository is null)
            {
                continue;
            }

            SemanticC3Fact? injection = facts.FirstOrDefault(fact =>
                fact.Kind == SemanticC3FactKind.ConstructorInjection &&
                activeBoundaryTypeIds.Contains(DeclaringTypeId(fact.SourceSymbol.SymbolId) ?? string.Empty) &&
                string.Equals(
                    SimpleTypeName(RelatedTypeName(fact.RelatedSymbolId) ?? string.Empty),
                    SimpleTypeName(registration.ServiceType),
                    StringComparison.Ordinal));

            if (injection is null)
            {
                continue;
            }

            PersistenceAccumulator candidate = GetPersistence(
                candidates,
                repository.SourceSymbol,
                PersistenceKind.Repository);
            candidate.EvidenceIds.Add(repository.Id);
            candidate.EvidenceIds.Add(registration.Fact.Id);
            candidate.EvidenceIds.Add(injection.Id);
            if (typeFacts.TryGetValue(repository.SourceSymbol.Id, out SemanticC3Fact? typeFact))
            {
                candidate.EvidenceIds.Add(typeFact.Id);
            }
        }

        foreach (SemanticC3Fact repository in repositoryFacts)
        {
            string repositoryType = repository.SourceSymbol.SymbolId.StartsWith("T:", StringComparison.Ordinal)
                ? repository.SourceSymbol.SymbolId[2..]
                : repository.SourceSymbol.SymbolId;

            SemanticC3Fact? invocation = facts.FirstOrDefault(fact =>
                fact.Kind == SemanticC3FactKind.SymbolInvocation &&
                activeBoundaryTypeIds.Contains(DeclaringTypeId(fact.SourceSymbol.SymbolId) ?? string.Empty) &&
                string.Equals(
                    RelatedMethodTypeName(fact.RelatedSymbolId),
                    repositoryType,
                    StringComparison.Ordinal));

            if (invocation is null)
            {
                continue;
            }

            PersistenceAccumulator candidate = GetPersistence(
                candidates,
                repository.SourceSymbol,
                PersistenceKind.Repository);
            candidate.EvidenceIds.Add(repository.Id);
            candidate.EvidenceIds.Add(invocation.Id);
            if (typeFacts.TryGetValue(repository.SourceSymbol.Id, out SemanticC3Fact? typeFact))
            {
                candidate.EvidenceIds.Add(typeFact.Id);
            }
        }

        return
        [
            .. candidates.Values
                .Select(candidate =>
                {
                    string typeName = SimpleTypeNameFromSymbol(candidate.SourceSymbol.SymbolId);
                    string name = candidate.Kind == PersistenceKind.DbContext
                        ? DbContextName(typeName)
                        : RepositoryName(typeName);
                    string responsibility = candidate.Kind == PersistenceKind.DbContext
                        ? "Provides the EF Core persistence/transaction boundary observed in the selected container."
                        : "Implements a repository persistence boundary that is observably used by a worker or messaging adapter.";
                    string reason = candidate.Kind == PersistenceKind.DbContext
                        ? "DbContext inheritance is a strong local persistence signal, but the architectural component boundary remains reviewable."
                        : "Repository shape is combined with DI/injection or direct collaboration before proposing persistence; the component boundary remains reviewable.";

                    SemanticC3ComponentCandidate component = new(
                        StableIds.ForSemanticC3Component(
                            selected.Id,
                            SemanticC3ComponentCategory.PersistenceAdapter,
                            candidate.SourceSymbol.Id),
                        selected.Id,
                        SemanticC3ComponentCategory.PersistenceAdapter,
                        name,
                        responsibility,
                        [.. candidate.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                        candidate.SourceSymbol,
                        ReviewStatus.RequiresReview,
                        reason);

                    return new PersistenceCandidate(component, candidate.SourceSymbol, candidate.Kind);
                })
                .OrderBy(candidate => candidate.Component.Id, StringComparer.Ordinal),
        ];
    }

    private static void AddMessagingExternalRelations(
        ArchitectureModel baseModel,
        ArchitectureElement selected,
        MessagingCandidate[] messaging,
        List<SemanticC3RelationCandidate> relations)
    {
        foreach (MessagingCandidate candidate in messaging)
        {
            foreach (ExternalIntegrationEvidence evidence in candidate.ExternalEvidence)
            {
                ArchitectureRelation? mapped = FindMappedExternalRelation(
                    baseModel,
                    selected.Id,
                    evidence.Id);
                if (mapped is null)
                {
                    continue;
                }

                string? externalElementId = OtherEndpoint(mapped, selected.Id);
                if (externalElementId is null)
                {
                    continue;
                }

                HashSet<string> evidenceIds = new(candidate.Component.EvidenceIds, StringComparer.Ordinal)
                {
                    evidence.Id,
                };

                relations.Add(new SemanticC3RelationCandidate(
                    StableIds.ForRelation(
                        candidate.Component.Id,
                        externalElementId,
                        "semantic-c3|external|" + evidence.Id),
                    candidate.Component.Id,
                    externalElementId,
                    SemanticC3RelationTargetKind.ArchitectureElement,
                    mapped.Description,
                    [.. evidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                    ReviewStatus.RequiresReview,
                    "The existing C1/C2 external relation is associated with this local messaging adapter; runtime flow remains reviewable at C3."));
            }
        }
    }

    private static void AddPersistenceExternalRelations(
        ArchitectureModel baseModel,
        ArchitectureElement selected,
        WorkerCandidate[] workers,
        PersistenceCandidate[] persistence,
        HashSet<string> selectedProjects,
        ExternalIntegrationEvidenceResult externalEvidence,
        List<SemanticC3RelationCandidate> relations)
    {
        ExternalIntegrationEvidence[] dataEvidence =
        [
            .. externalEvidence.Evidence
                .Where(item =>
                    selectedProjects.Contains(item.ProjectPath) &&
                    item.Kind == ExternalIntegrationKind.Database)
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];

        foreach (ExternalIntegrationEvidence evidence in dataEvidence)
        {
            ArchitectureRelation? mapped = FindMappedExternalRelation(
                baseModel,
                selected.Id,
                evidence.Id);
            if (mapped is null)
            {
                continue;
            }

            string? externalElementId = OtherEndpoint(mapped, selected.Id);
            if (externalElementId is null)
            {
                continue;
            }

            PersistenceCandidate? persistenceCandidate = MatchPersistenceCandidate(
                persistence,
                evidence);

            SemanticC3ComponentCandidate? source = persistenceCandidate?.Component;
            if (source is null)
            {
                WorkerCandidate[] exactWorkers =
                [
                    .. workers.Where(worker =>
                        string.Equals(
                            WorkerSourcePath(worker, baseModel, evidence.ProjectPath),
                            evidence.SourcePath,
                            StringComparison.Ordinal)),
                ];
                if (exactWorkers.Length == 1)
                {
                    source = exactWorkers[0].Component;
                }
            }

            if (source is null)
            {
                continue;
            }

            HashSet<string> evidenceIds = new(source.EvidenceIds, StringComparer.Ordinal)
            {
                evidence.Id,
            };

            relations.Add(new SemanticC3RelationCandidate(
                StableIds.ForRelation(
                    source.Id,
                    externalElementId,
                    "semantic-c3|external|" + evidence.Id),
                source.Id,
                externalElementId,
                SemanticC3RelationTargetKind.ArchitectureElement,
                mapped.Description,
                [.. evidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                ReviewStatus.RequiresReview,
                evidence.Confidence == ExternalIntegrationConfidence.High
                    ? "The existing C1/C2 database relation is associated with this local persistence responsibility; runtime flow remains reviewable at C3."
                    : "The existing C1/C2 database relation has " +
                      evidence.Confidence.ToString().ToLowerInvariant() +
                      " confidence and remains reviewable when associated with this local persistence responsibility."));
        }
    }

    private static PersistenceCandidate? MatchPersistenceCandidate(
        PersistenceCandidate[] persistence,
        ExternalIntegrationEvidence evidence)
    {
        PersistenceCandidate[] samePath =
        [
            .. persistence.Where(candidate =>
                string.Equals(
                    SourcePathFor(candidate.Component, candidate.SourceSymbol, evidence.ProjectPath),
                    evidence.SourcePath,
                    StringComparison.Ordinal)),
        ];
        if (samePath.Length == 1)
        {
            return samePath[0];
        }

        PersistenceCandidate[] sameProject =
        [
            .. persistence.Where(candidate =>
                candidate.SourceSymbol.ProjectPath == evidence.ProjectPath),
        ];
        return sameProject.Length == 1 ? sameProject[0] : null;
    }

    private static string? WorkerSourcePath(
        WorkerCandidate worker,
        ArchitectureModel baseModel,
        string projectPath)
    {
        _ = baseModel;
        _ = projectPath;
        return null;
    }

    private static string? SourcePathFor(
        SemanticC3ComponentCandidate component,
        SemanticC3SourceSymbolIdentity symbol,
        string projectPath)
    {
        _ = component;
        _ = symbol;
        _ = projectPath;
        return null;
    }

    private static SemanticC3Fact? MatchMessagingSignal(
        SemanticC3Fact[] localSignals,
        ExternalIntegrationEvidence evidence)
    {
        MessagingRole expected = evidence.Direction == ExternalIntegrationDirection.Publish
            ? MessagingRole.Publisher
            : MessagingRole.Consumer;

        SemanticC3Fact[] samePath =
        [
            .. localSignals.Where(fact =>
                fact.ProjectPath == evidence.ProjectPath &&
                fact.SourcePath == evidence.SourcePath &&
                RoleOf(fact.SourceSymbol) is var role &&
                (role == expected || role == MessagingRole.Ambiguous)),
        ];
        if (samePath.Length == 1)
        {
            return samePath[0];
        }

        SemanticC3Fact[] projectRole =
        [
            .. localSignals.Where(fact =>
                fact.ProjectPath == evidence.ProjectPath &&
                RoleOf(fact.SourceSymbol) == expected),
        ];
        return projectRole.Length == 1 ? projectRole[0] : null;
    }

    private static MessagingRole RoleOf(SemanticC3SourceSymbolIdentity symbol)
    {
        string name = SimpleTypeNameFromSymbol(symbol.SymbolId);
        bool publisher =
            name.Contains("Publisher", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Producer", StringComparison.OrdinalIgnoreCase);
        bool consumer =
            name.Contains("Consumer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Subscriber", StringComparison.OrdinalIgnoreCase);

        return (publisher, consumer) switch
        {
            (true, false) => MessagingRole.Publisher,
            (false, true) => MessagingRole.Consumer,
            _ => MessagingRole.Ambiguous,
        };
    }

    private static bool IsConcreteTypeSignal(
        SemanticC3Fact fact,
        Dictionary<string, SemanticC3Fact> typeFacts) =>
        typeFacts.TryGetValue(fact.SourceSymbol.Id, out SemanticC3Fact? typeFact) &&
        !typeFact.Description.StartsWith("Declares interface", StringComparison.Ordinal);

    private static ArchitectureRelation? FindMappedExternalRelation(
        ArchitectureModel model,
        string selectedContainerId,
        string evidenceId)
    {
        ArchitectureRelation[] matches =
        [
            .. model.Relations.Where(relation =>
                relation.EvidenceIds.Contains(evidenceId, StringComparer.Ordinal) &&
                (relation.SourceId == selectedContainerId ||
                 relation.DestinationId == selectedContainerId)),
        ];

        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? OtherEndpoint(ArchitectureRelation relation, string selectedContainerId)
    {
        if (relation.SourceId == selectedContainerId)
        {
            return relation.DestinationId;
        }

        return relation.DestinationId == selectedContainerId
            ? relation.SourceId
            : null;
    }

    private static WorkerAccumulator GetWorker(
        Dictionary<string, WorkerAccumulator> workers,
        SemanticC3SourceSymbolIdentity sourceSymbol)
    {
        if (!workers.TryGetValue(sourceSymbol.Id, out WorkerAccumulator? worker))
        {
            worker = new WorkerAccumulator(sourceSymbol);
            workers.Add(sourceSymbol.Id, worker);
        }

        return worker;
    }

    private static PersistenceAccumulator GetPersistence(
        Dictionary<string, PersistenceAccumulator> candidates,
        SemanticC3SourceSymbolIdentity sourceSymbol,
        PersistenceKind kind)
    {
        if (!candidates.TryGetValue(sourceSymbol.Id, out PersistenceAccumulator? candidate))
        {
            candidate = new PersistenceAccumulator(sourceSymbol, kind);
            candidates.Add(sourceSymbol.Id, candidate);
        }

        return candidate;
    }

    private static string? ParseHostedType(string? related)
    {
        if (related is null || !related.StartsWith("HOST:", StringComparison.Ordinal))
        {
            return null;
        }

        int last = related.LastIndexOf(':');
        return last <= "HOST:".Length
            ? null
            : related["HOST:".Length..last];
    }

    private static DiRegistration? ParseRegistration(SemanticC3Fact fact)
    {
        string? related = fact.RelatedSymbolId;
        if (related is null || !related.StartsWith("DI:", StringComparison.Ordinal))
        {
            return null;
        }

        int lifetimeSeparator = related.IndexOf(':', 3);
        int arrow = related.IndexOf("->", StringComparison.Ordinal);
        int ordinalSeparator = related.LastIndexOf(':');

        if (lifetimeSeparator < 0 ||
            arrow <= lifetimeSeparator ||
            ordinalSeparator <= arrow + 2)
        {
            return null;
        }

        string service = related[(lifetimeSeparator + 1)..arrow];
        string implementation = related[(arrow + 2)..ordinalSeparator];

        return string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(implementation)
            ? null
            : new DiRegistration(service, implementation, fact);
    }

    private static SemanticC3Fact? ResolveConcreteType(
        IEnumerable<SemanticC3Fact> typeFacts,
        string typeName)
    {
        string normalized = NormalizeTypeName(typeName);
        string simple = SimpleTypeName(normalized);
        SemanticC3Fact[] candidates =
        [
            .. typeFacts.Where(fact =>
                !fact.Description.StartsWith("Declares interface", StringComparison.Ordinal) &&
                (fact.SourceSymbol.SymbolId == "T:" + normalized ||
                 SimpleTypeNameFromSymbol(fact.SourceSymbol.SymbolId)
                    .Equals(simple, StringComparison.Ordinal))),
        ];

        SemanticC3Fact[] exact =
        [
            .. candidates.Where(fact => fact.SourceSymbol.SymbolId == "T:" + normalized),
        ];

        if (exact.Length == 1)
        {
            return exact[0];
        }

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static SemanticC3Fact? ResolvePersistenceFact(
        IEnumerable<SemanticC3Fact> persistenceFacts,
        string typeName)
    {
        string normalized = NormalizeTypeName(typeName);
        string simple = SimpleTypeName(normalized);
        SemanticC3Fact[] candidates =
        [
            .. persistenceFacts.Where(fact =>
                fact.SourceSymbol.SymbolId == "T:" + normalized ||
                SimpleTypeNameFromSymbol(fact.SourceSymbol.SymbolId)
                    .Equals(simple, StringComparison.Ordinal)),
        ];

        SemanticC3Fact[] exact =
        [
            .. candidates.Where(fact => fact.SourceSymbol.SymbolId == "T:" + normalized),
        ];

        if (exact.Length == 1)
        {
            return exact[0];
        }

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? RelatedTypeName(string? relatedSymbolId)
    {
        if (relatedSymbolId is null ||
            !relatedSymbolId.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        int ordinal = relatedSymbolId.LastIndexOf(':');
        return ordinal <= 2 ? null : relatedSymbolId[2..ordinal];
    }

    private static string? RelatedMethodTypeName(string? relatedSymbolId)
    {
        if (relatedSymbolId is null ||
            !relatedSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        string target = relatedSymbolId[2..];
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

    private static HashSet<string> ResolveSelectedProjects(
        ArchitectureModel model,
        ArchitectureElement selected)
    {
        Dictionary<string, Evidence> evidenceById = model.Snapshot.Evidence
            .ToDictionary(evidence => evidence.Id, StringComparer.Ordinal);
        string[] projectPaths =
        [
            .. model.Snapshot.Files
                .Select(file => file.RelativePath)
                .Where(path => Path.GetExtension(path)
                    .Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal),
        ];

        HashSet<string> selectedProjects = new(StringComparer.Ordinal);
        foreach (string evidenceId in selected.EvidenceIds)
        {
            if (!evidenceById.TryGetValue(evidenceId, out Evidence? evidence))
            {
                continue;
            }

            if (Path.GetExtension(evidence.RelativePath)
                .Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                selectedProjects.Add(evidence.RelativePath);
                continue;
            }

            string? nearest = projectPaths
                .Where(project => IsWithinProject(evidence.RelativePath, project))
                .OrderByDescending(ProjectDepth)
                .ThenBy(project => project, StringComparer.Ordinal)
                .FirstOrDefault();
            if (nearest is not null)
            {
                selectedProjects.Add(nearest);
            }
        }

        return selectedProjects;
    }

    private static bool IsWithinProject(string sourcePath, string projectPath)
    {
        string directory = DirectoryPart(projectPath);
        return directory.Length == 0 ||
            sourcePath.StartsWith(directory + "/", StringComparison.Ordinal) ||
            sourcePath.Equals(directory, StringComparison.Ordinal);
    }

    private static int ProjectDepth(string projectPath) =>
        DirectoryPart(projectPath).Count(character => character == '/') + 1;

    private static string DirectoryPart(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static SemanticC3ComponentCandidate MergeComponents(
        IGrouping<string, SemanticC3ComponentCandidate> group)
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
    }

    private static SemanticC3RelationCandidate MergeRelations(
        IGrouping<string, SemanticC3RelationCandidate> group)
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
    }

    private static string WorkerLogicalName(string typeName)
    {
        string value = TrimSuffix(typeName, "BackgroundService");
        value = TrimSuffix(value, "HostedService");
        value = TrimSuffix(value, "Worker");

        if (value.EndsWith("Poller", StringComparison.Ordinal))
        {
            return Truncate(Humanize(value), 120);
        }

        string humanized = Humanize(value);
        return Truncate(
            (humanized.Length == 0 ? "Background" : humanized) + " worker",
            120);
    }

    private static string DbContextName(string typeName)
    {
        string baseName = TrimSuffix(typeName, "DbContext");
        baseName = TrimSuffix(baseName, "Context");
        string humanized = Humanize(baseName);
        return Truncate(
            (humanized.Length == 0 ? "Database" : humanized) + " persistence",
            120);
    }

    private static string RepositoryName(string typeName)
    {
        string baseName = TrimSuffix(typeName, "Repository");
        string humanized = Humanize(baseName);
        return Truncate(
            (humanized.Length == 0 ? "Repository" : humanized + " repository") +
            " persistence",
            120);
    }

    private static string SimpleTypeNameFromSymbol(string symbolId) =>
        symbolId.StartsWith("T:", StringComparison.Ordinal)
            ? SimpleTypeName(symbolId[2..])
            : SimpleTypeName(symbolId);

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
        int separator = normalized.LastIndexOf('.');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string TrimSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) &&
        value.Length > suffix.Length
            ? value[..^suffix.Length]
            : value;

    private static string Humanize(string value)
    {
        StringBuilder builder = new();
        char previous = '\0';

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                AppendSpace(builder);
                previous = character;
                continue;
            }

            if (char.IsUpper(character) &&
                builder.Length > 0 &&
                (char.IsLower(previous) || char.IsDigit(previous)))
            {
                AppendSpace(builder);
            }

            builder.Append(character);
            previous = character;
        }

        return builder.ToString().Trim();
    }

    private static void AppendSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != ' ')
        {
            builder.Append(' ');
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength].TrimEnd();

    private static void ValidateInputs(
        ArchitectureModel baseModel,
        SemanticC3FactSet factSet,
        ExternalIntegrationEvidenceResult externalEvidence)
    {
        ImmutableArray<ContractError> baseErrors = ContractValidator.ValidateModel(baseModel);
        if (!baseErrors.IsEmpty)
        {
            throw new ContractValidationException(baseErrors);
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

        if (externalEvidence.Evidence.IsDefault ||
            externalEvidence.Diagnostics.IsDefault)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "semanticC3.externalEvidenceInvalid",
                    "$.externalEvidence",
                    "External integration evidence must use initialized collections."),
            ]);
        }
    }

    private enum MessagingRole
    {
        Ambiguous,
        Publisher,
        Consumer,
    }

    private enum PersistenceKind
    {
        DbContext,
        Repository,
    }

    private sealed class WorkerAccumulator(SemanticC3SourceSymbolIdentity sourceSymbol)
    {
        public SemanticC3SourceSymbolIdentity SourceSymbol { get; } = sourceSymbol;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);

        public bool DirectBoundary { get; set; }

        public bool RegisteredBoundary { get; set; }
    }

    private sealed record WorkerCandidate(
        SemanticC3ComponentCandidate Component,
        SemanticC3SourceSymbolIdentity SourceSymbol,
        bool DirectBoundary,
        bool RegisteredBoundary);

    private sealed class MessagingAccumulator(
        string componentId,
        SemanticC3SourceSymbolIdentity sourceSymbol,
        SemanticC3ComponentCategory category,
        string technology)
    {
        public string ComponentId { get; } = componentId;

        public SemanticC3SourceSymbolIdentity SourceSymbol { get; } = sourceSymbol;

        public SemanticC3ComponentCategory Category { get; } = category;

        public string Technology { get; } = technology;

        public HashSet<string> LocalEvidenceIds { get; } = new(StringComparer.Ordinal);

        public List<ExternalIntegrationEvidence> ExternalEvidence { get; } = [];
    }

    private sealed record MessagingCandidate(
        SemanticC3ComponentCandidate Component,
        SemanticC3SourceSymbolIdentity SourceSymbol,
        string Technology,
        ImmutableArray<ExternalIntegrationEvidence> ExternalEvidence);

    private sealed class PersistenceAccumulator(
        SemanticC3SourceSymbolIdentity sourceSymbol,
        PersistenceKind kind)
    {
        public SemanticC3SourceSymbolIdentity SourceSymbol { get; } = sourceSymbol;

        public PersistenceKind Kind { get; } = kind;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PersistenceCandidate(
        SemanticC3ComponentCandidate Component,
        SemanticC3SourceSymbolIdentity SourceSymbol,
        PersistenceKind Kind);

    private sealed record DiRegistration(
        string ServiceType,
        string ImplementationType,
        SemanticC3Fact Fact);
}
