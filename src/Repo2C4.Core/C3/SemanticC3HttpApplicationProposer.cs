using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.C3;

/// <summary>
/// Proposes reviewable Semantic C3 HTTP and application-layer components from bounded structural facts.
/// HTTP routes are aggregated by controller type or by the enclosing Minimal API semantic symbol; a route is
/// never promoted to a standalone component merely because it appears in source.
/// </summary>
public static class SemanticC3HttpApplicationProposer
{
    private static readonly string[] InfrastructureSuffixes =
    [
        "Adapter",
        "Client",
        "Consumer",
        "Context",
        "DbContext",
        "Producer",
        "Publisher",
        "Repository",
        "Subscriber",
        "Worker",
    ];

    public static SemanticC3Proposal Propose(
        ArchitectureModel baseModel,
        string selectedContainerId,
        SemanticC3FactSet factSet)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedContainerId);
        ArgumentNullException.ThrowIfNull(factSet);

        ThrowIfInvalidBase(baseModel);
        ValidateFactSet(factSet);

        ArchitectureElement? selected = baseModel.Elements.FirstOrDefault(element =>
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
        if (selectedProjects.Count == 0)
        {
            return new SemanticC3Proposal(
                SemanticC3ContractSchema.Version,
                [selectedContainerId],
                [],
                []);
        }

        SemanticC3Fact[] facts =
        [
            .. factSet.Facts
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];

        BoundarySeed[] boundaries = BuildBoundaries(facts, selectedProjects);
        List<SemanticC3ComponentCandidate> components =
        [
            .. boundaries.Select(boundary =>
                CreateHttpCandidate(selected, boundary)),
        ];

        components.AddRange(BuildApplicationCandidates(
            selected,
            facts,
            selectedProjects,
            boundaries));

        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            [selectedContainerId],
            [.. components
                .GroupBy(component => component.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(component => component.Id, StringComparer.Ordinal)],
            []);

        ImmutableArray<ContractError> errors = SemanticC3Validator.Validate(proposal);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return proposal;
    }

    private static BoundarySeed[] BuildBoundaries(
        SemanticC3Fact[] facts,
        HashSet<string> selectedProjects)
    {
        Dictionary<string, BoundaryAccumulator> boundaries = new(StringComparer.Ordinal);

        foreach (SemanticC3Fact fact in facts.Where(fact =>
                     selectedProjects.Contains(fact.ProjectPath) &&
                     fact.Category == "semantic.host.minimalApi"))
        {
            AddBoundary(
                boundaries,
                fact.SourceSymbol,
                BoundaryKind.MinimalApi,
                fact);
        }

        foreach (SemanticC3Fact fact in facts.Where(fact =>
                     selectedProjects.Contains(fact.ProjectPath) &&
                     fact.Category == "semantic.host.controller"))
        {
            AddBoundary(
                boundaries,
                fact.SourceSymbol,
                BoundaryKind.Controller,
                fact);
        }

        foreach (SemanticC3Fact fact in facts.Where(fact =>
                     selectedProjects.Contains(fact.ProjectPath) &&
                     fact.Category == "semantic.host.controllerAction"))
        {
            SemanticC3SourceSymbolIdentity? typeSymbol = DeclaringTypeIdentity(fact.SourceSymbol);
            if (typeSymbol is null)
            {
                continue;
            }

            BoundaryAccumulator boundary = AddBoundary(
                boundaries,
                typeSymbol,
                BoundaryKind.Controller,
                fact);
            boundary.ActionSymbolIds.Add(fact.SourceSymbol.Id);
        }

        return
        [
            .. boundaries.Values
                .Select(boundary => boundary.ToSeed())
                .OrderBy(boundary => boundary.SourceSymbol.Id, StringComparer.Ordinal),
        ];
    }

    private static List<SemanticC3ComponentCandidate> BuildApplicationCandidates(
        ArchitectureElement selected,
        SemanticC3Fact[] facts,
        HashSet<string> selectedProjects,
        BoundarySeed[] boundaries)
    {
        Dictionary<string, SignalAccumulator> signals = BuildEndpointSignals(
            facts,
            selectedProjects,
            boundaries);

        DiRegistration[] registrations =
        [
            .. facts
                .Where(fact =>
                    selectedProjects.Contains(fact.ProjectPath) &&
                    fact.Kind == SemanticC3FactKind.DependencyInjectionRegistration)
                .Select(ParseRegistration)
                .Where(registration => registration is not null)
                .Select(registration => registration!)
                .OrderBy(registration => registration.Fact.Id, StringComparer.Ordinal),
        ];

        SemanticC3Fact[] typeFacts =
        [
            .. facts.Where(fact => fact.Kind == SemanticC3FactKind.TypeDeclaration),
        ];

        Dictionary<string, ApplicationAccumulator> candidates = new(StringComparer.Ordinal);
        HashSet<string> minimalProjects = boundaries
            .Where(boundary => boundary.Kind == BoundaryKind.MinimalApi)
            .Select(boundary => boundary.ProjectPath)
            .ToHashSet(StringComparer.Ordinal);

        foreach (DiRegistration registration in registrations)
        {
            SemanticC3Fact? typeFact = ResolveConcreteType(typeFacts, registration.ImplementationType);
            if (typeFact is null ||
                IsInfrastructureType(typeFact, facts) ||
                !HasApplicationMethod(typeFact, facts))
            {
                continue;
            }

            SignalAccumulator? signal = FindSignal(
                signals,
                registration.ServiceType,
                registration.ImplementationType);

            bool coLocatedMinimalApi = signal is null &&
                minimalProjects.Contains(registration.Fact.ProjectPath);
            if (signal is null && !coLocatedMinimalApi)
            {
                continue;
            }

            ApplicationAccumulator accumulator = GetApplicationAccumulator(
                candidates,
                typeFact,
                selected.Id);

            accumulator.EvidenceIds.Add(typeFact.Id);
            accumulator.EvidenceIds.Add(registration.Fact.Id);

            if (signal is not null)
            {
                accumulator.StrongEndpointSignal = true;
                foreach (string evidenceId in signal.EvidenceIds)
                {
                    accumulator.EvidenceIds.Add(evidenceId);
                }
            }
            else
            {
                accumulator.CoLocatedOnly = true;
                foreach (BoundarySeed boundary in boundaries.Where(boundary =>
                             boundary.Kind == BoundaryKind.MinimalApi &&
                             boundary.ProjectPath == registration.Fact.ProjectPath))
                {
                    foreach (string evidenceId in boundary.EvidenceIds)
                    {
                        accumulator.EvidenceIds.Add(evidenceId);
                    }
                }
            }
        }

        foreach ((string targetType, SignalAccumulator signal) in signals
                     .Where(item => item.Value.DirectInvocation)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            SemanticC3Fact? typeFact = ResolveConcreteType(typeFacts, targetType);
            if (typeFact is null ||
                IsInfrastructureType(typeFact, facts) ||
                !HasApplicationMethod(typeFact, facts))
            {
                continue;
            }

            ApplicationAccumulator accumulator = GetApplicationAccumulator(
                candidates,
                typeFact,
                selected.Id);
            accumulator.StrongEndpointSignal = true;
            accumulator.EvidenceIds.Add(typeFact.Id);
            foreach (string evidenceId in signal.EvidenceIds)
            {
                accumulator.EvidenceIds.Add(evidenceId);
            }
        }

        return
        [
            .. candidates.Values
                .Select(CreateApplicationCandidate)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal),
        ];
    }

    private static Dictionary<string, SignalAccumulator> BuildEndpointSignals(
        SemanticC3Fact[] facts,
        HashSet<string> selectedProjects,
        BoundarySeed[] boundaries)
    {
        Dictionary<string, SignalAccumulator> signals =
            new(StringComparer.OrdinalIgnoreCase);

        HashSet<string> minimalBoundarySymbols = boundaries
            .Where(boundary => boundary.Kind == BoundaryKind.MinimalApi)
            .Select(boundary => boundary.SourceSymbol.Id)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<string, BoundarySeed> controllerByTypeSymbol = boundaries
            .Where(boundary => boundary.Kind == BoundaryKind.Controller)
            .ToDictionary(
                boundary => boundary.SourceSymbol.Id,
                StringComparer.Ordinal);

        foreach (SemanticC3Fact dependency in facts.Where(fact =>
                     selectedProjects.Contains(fact.ProjectPath) &&
                     fact.Kind == SemanticC3FactKind.EndpointDependency &&
                     minimalBoundarySymbols.Contains(fact.SourceSymbol.Id)))
        {
            string? typeName = RelatedTypeName(dependency.RelatedSymbolId);
            if (typeName is null)
            {
                continue;
            }

            SignalAccumulator signal = GetSignal(signals, typeName);
            signal.EvidenceIds.Add(dependency.Id);
            AddBoundaryEvidence(signal, boundaries, dependency.SourceSymbol.Id);
        }

        foreach (BoundarySeed controller in controllerByTypeSymbol.Values)
        {
            foreach (SemanticC3Fact injection in facts.Where(fact =>
                         selectedProjects.Contains(fact.ProjectPath) &&
                         fact.Kind == SemanticC3FactKind.ConstructorInjection &&
                         DeclaringTypeId(fact.SourceSymbol.SymbolId) == controller.SourceSymbol.SymbolId))
            {
                string? typeName = RelatedTypeName(injection.RelatedSymbolId);
                if (typeName is null)
                {
                    continue;
                }

                SignalAccumulator signal = GetSignal(signals, typeName);
                signal.EvidenceIds.Add(injection.Id);
                AddBoundaryEvidence(signal, controller);
            }

            foreach (SemanticC3Fact parameter in facts.Where(fact =>
                         fact.Kind == SemanticC3FactKind.MethodParameter &&
                         controller.ActionSymbolIds.Contains(fact.SourceSymbol.Id)))
            {
                string? typeName = RelatedTypeName(parameter.RelatedSymbolId);
                if (typeName is null)
                {
                    continue;
                }

                SignalAccumulator signal = GetSignal(signals, typeName);
                signal.EvidenceIds.Add(parameter.Id);
                AddBoundaryEvidence(signal, controller);
            }

            foreach (SemanticC3Fact invocation in facts.Where(fact =>
                         fact.Kind == SemanticC3FactKind.SymbolInvocation &&
                         controller.ActionSymbolIds.Contains(fact.SourceSymbol.Id)))
            {
                string? targetType = RelatedMethodTypeName(invocation.RelatedSymbolId);
                if (targetType is null)
                {
                    continue;
                }

                SignalAccumulator signal = GetSignal(signals, targetType);
                signal.DirectInvocation = true;
                signal.EvidenceIds.Add(invocation.Id);
                AddBoundaryEvidence(signal, controller);
            }
        }

        foreach (SemanticC3Fact handler in facts.Where(fact =>
                     selectedProjects.Contains(fact.ProjectPath) &&
                     fact.Kind == SemanticC3FactKind.EndpointHandler &&
                     minimalBoundarySymbols.Contains(fact.SourceSymbol.Id)))
        {
            SemanticC3Fact[] handlerMethods =
            [
                .. facts.Where(fact =>
                    fact.Kind == SemanticC3FactKind.MethodDeclaration &&
                    MatchesMethodTarget(fact.SourceSymbol.SymbolId, handler.RelatedSymbolId)),
            ];

            if (handlerMethods.Length != 1)
            {
                continue;
            }

            SemanticC3Fact method = handlerMethods[0];
            foreach (SemanticC3Fact parameter in facts.Where(fact =>
                         fact.Kind == SemanticC3FactKind.MethodParameter &&
                         fact.SourceSymbol.Id == method.SourceSymbol.Id))
            {
                string? typeName = RelatedTypeName(parameter.RelatedSymbolId);
                if (typeName is null)
                {
                    continue;
                }

                SignalAccumulator signal = GetSignal(signals, typeName);
                signal.EvidenceIds.Add(handler.Id);
                signal.EvidenceIds.Add(parameter.Id);
                AddBoundaryEvidence(signal, boundaries, handler.SourceSymbol.Id);
            }

            foreach (SemanticC3Fact invocation in facts.Where(fact =>
                         fact.Kind == SemanticC3FactKind.SymbolInvocation &&
                         fact.SourceSymbol.Id == method.SourceSymbol.Id))
            {
                string? targetType = RelatedMethodTypeName(invocation.RelatedSymbolId);
                if (targetType is null)
                {
                    continue;
                }

                SignalAccumulator signal = GetSignal(signals, targetType);
                signal.DirectInvocation = true;
                signal.EvidenceIds.Add(handler.Id);
                signal.EvidenceIds.Add(invocation.Id);
                AddBoundaryEvidence(signal, boundaries, handler.SourceSymbol.Id);
            }
        }

        return signals;
    }

    private static SemanticC3ComponentCandidate CreateHttpCandidate(
        ArchitectureElement selected,
        BoundarySeed boundary)
    {
        string name = HttpCandidateName(selected.Name, boundary);
        string responsibility = boundary.Kind == BoundaryKind.Controller
            ? "Receives HTTP requests through a controller boundary and exposes application entry points."
            : "Receives HTTP requests through Minimal API mappings and exposes application entry points.";

        return new SemanticC3ComponentCandidate(
            StableIds.ForSemanticC3Component(
                selected.Id,
                SemanticC3ComponentCategory.HttpEndpoint,
                boundary.SourceSymbol.Id),
            selected.Id,
            SemanticC3ComponentCategory.HttpEndpoint,
            name,
            responsibility,
            [.. boundary.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
            boundary.SourceSymbol,
            ReviewStatus.RequiresReview,
            boundary.Kind == BoundaryKind.Controller
                ? "Controller routes are aggregated by declaring controller type; the resulting C3 boundary requires architectural review."
                : "Minimal API routes are aggregated by their enclosing semantic symbol; the resulting C3 boundary requires architectural review.");
    }

    private static SemanticC3ComponentCandidate CreateApplicationCandidate(
        ApplicationAccumulator candidate)
    {
        string simpleName = SimpleTypeNameFromSymbol(candidate.TypeFact.SourceSymbol.SymbolId);
        string name = ApplicationCandidateName(simpleName);

        string reviewReason = candidate.StrongEndpointSignal
            ? "HTTP dependency/handler or invocation evidence converges on this application type; the C3 responsibility boundary remains reviewable."
            : "The type is DI-registered inside a Minimal API host project, but endpoint-level usage was not observed; keep this application candidate under review.";

        return new SemanticC3ComponentCandidate(
            StableIds.ForSemanticC3Component(
                candidate.ContainerId,
                SemanticC3ComponentCategory.ApplicationService,
                candidate.TypeFact.SourceSymbol.Id),
            candidate.ContainerId,
            SemanticC3ComponentCategory.ApplicationService,
            name,
            "Coordinates application behavior reached from the HTTP boundary.",
            [.. candidate.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
            candidate.TypeFact.SourceSymbol,
            ReviewStatus.RequiresReview,
            reviewReason);
    }

    private static BoundaryAccumulator AddBoundary(
        Dictionary<string, BoundaryAccumulator> boundaries,
        SemanticC3SourceSymbolIdentity sourceSymbol,
        BoundaryKind kind,
        SemanticC3Fact fact)
    {
        string key = sourceSymbol.Id;
        if (!boundaries.TryGetValue(key, out BoundaryAccumulator? boundary))
        {
            boundary = new BoundaryAccumulator(sourceSymbol, kind);
            boundaries.Add(key, boundary);
        }

        if (kind == BoundaryKind.Controller)
        {
            boundary.Kind = BoundaryKind.Controller;
        }

        boundary.EvidenceIds.Add(fact.Id);
        return boundary;
    }

    private static void AddBoundaryEvidence(
        SignalAccumulator signal,
        BoundarySeed[] boundaries,
        string sourceSymbolId)
    {
        foreach (BoundarySeed boundary in boundaries.Where(boundary =>
                     boundary.SourceSymbol.Id == sourceSymbolId))
        {
            AddBoundaryEvidence(signal, boundary);
        }
    }

    private static void AddBoundaryEvidence(
        SignalAccumulator signal,
        BoundarySeed boundary)
    {
        foreach (string evidenceId in boundary.EvidenceIds)
        {
            signal.EvidenceIds.Add(evidenceId);
        }
    }

    private static SignalAccumulator GetSignal(
        Dictionary<string, SignalAccumulator> signals,
        string typeName)
    {
        string key = SimpleTypeName(typeName);
        if (!signals.TryGetValue(key, out SignalAccumulator? signal))
        {
            signal = new SignalAccumulator();
            signals.Add(key, signal);
        }

        return signal;
    }

    private static SignalAccumulator? FindSignal(
        Dictionary<string, SignalAccumulator> signals,
        string serviceType,
        string implementationType)
    {
        if (signals.TryGetValue(SimpleTypeName(serviceType), out SignalAccumulator? service))
        {
            return service;
        }

        return signals.TryGetValue(SimpleTypeName(implementationType), out SignalAccumulator? implementation)
            ? implementation
            : null;
    }

    private static ApplicationAccumulator GetApplicationAccumulator(
        Dictionary<string, ApplicationAccumulator> candidates,
        SemanticC3Fact typeFact,
        string containerId)
    {
        if (!candidates.TryGetValue(typeFact.SourceSymbol.Id, out ApplicationAccumulator? candidate))
        {
            candidate = new ApplicationAccumulator(typeFact, containerId);
            candidates.Add(typeFact.SourceSymbol.Id, candidate);
        }

        return candidate;
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
        SemanticC3Fact[] typeFacts,
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
            .. candidates.Where(fact =>
                fact.SourceSymbol.SymbolId == "T:" + normalized),
        ];

        if (exact.Length == 1)
        {
            return exact[0];
        }

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsInfrastructureType(
        SemanticC3Fact typeFact,
        SemanticC3Fact[] facts)
    {
        if (facts.Any(fact =>
                fact.SourceSymbol.Id == typeFact.SourceSymbol.Id &&
                fact.Kind is SemanticC3FactKind.HostedService
                    or SemanticC3FactKind.PersistenceCandidate
                    or SemanticC3FactKind.MessagingCandidate
                    or SemanticC3FactKind.HttpBoundary))
        {
            return true;
        }

        string simpleName = SimpleTypeNameFromSymbol(typeFact.SourceSymbol.SymbolId);
        return InfrastructureSuffixes.Any(suffix =>
            simpleName.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static bool HasApplicationMethod(
        SemanticC3Fact typeFact,
        SemanticC3Fact[] facts)
    {
        string qualified = typeFact.SourceSymbol.SymbolId["T:".Length..];
        string methodPrefix = "M:" + qualified + ".";

        return facts.Any(fact =>
            fact.Kind == SemanticC3FactKind.MethodDeclaration &&
            fact.SourceSymbol.SymbolId.StartsWith(methodPrefix, StringComparison.Ordinal));
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

    private static SemanticC3SourceSymbolIdentity? DeclaringTypeIdentity(
        SemanticC3SourceSymbolIdentity method)
    {
        string? typeId = DeclaringTypeId(method.SymbolId);
        return typeId is null
            ? null
            : new SemanticC3SourceSymbolIdentity(
                StableIds.ForSemanticC3SourceSymbol(method.ProjectPath, typeId),
                method.ProjectPath,
                typeId);
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
        int methodSeparator = head.LastIndexOf('.');
        if (methodSeparator <= 2)
        {
            return null;
        }

        return "T:" + head["M:".Length..methodSeparator];
    }

    private static string? RelatedTypeName(string? relatedSymbolId)
    {
        if (relatedSymbolId is null ||
            !relatedSymbolId.StartsWith("T:", StringComparison.Ordinal))
        {
            return null;
        }

        int ordinalSeparator = relatedSymbolId.LastIndexOf(':');
        if (ordinalSeparator <= 2)
        {
            return null;
        }

        return relatedSymbolId[2..ordinalSeparator];
    }

    private static string? RelatedMethodTypeName(string? relatedSymbolId)
    {
        if (relatedSymbolId is null ||
            !relatedSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        string target = relatedSymbolId[2..];
        int methodSeparator = target.LastIndexOf('.');
        if (methodSeparator <= 0)
        {
            return null;
        }

        return target[..methodSeparator];
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

    private static string HttpCandidateName(
        string selectedContainerName,
        BoundarySeed boundary)
    {
        string semanticName;
        if (boundary.Kind == BoundaryKind.Controller)
        {
            semanticName = SimpleTypeNameFromSymbol(boundary.SourceSymbol.SymbolId);
            semanticName = TrimSuffix(semanticName, "Controller");
        }
        else
        {
            semanticName = MethodNameFromSymbol(boundary.SourceSymbol.SymbolId);
            if (semanticName is "<top-level>" or "Program" or "")
            {
                semanticName = selectedContainerName;
            }
            else
            {
                semanticName = TrimPrefix(semanticName, "Map");
                semanticName = TrimPrefix(semanticName, "Configure");
                semanticName = TrimPrefix(semanticName, "Register");
                semanticName = TrimSuffix(semanticName, "Endpoints");
            }
        }

        string humanized = Humanize(semanticName);
        if (humanized.Length == 0)
        {
            humanized = "HTTP";
        }

        return Truncate(humanized + " HTTP endpoint", 120);
    }

    private static string ApplicationCandidateName(string simpleName)
    {
        if (simpleName.EndsWith("UseCase", StringComparison.Ordinal))
        {
            return Truncate(
                Humanize(TrimSuffix(simpleName, "UseCase")) + " use case",
                120);
        }

        if (simpleName.EndsWith("Handler", StringComparison.Ordinal))
        {
            return Truncate(
                Humanize(TrimSuffix(simpleName, "Handler")) + " handler",
                120);
        }

        string baseName = TrimSuffix(simpleName, "Service");
        string humanized = Humanize(baseName);
        if (humanized.Length == 0)
        {
            humanized = "Application";
        }

        return Truncate(humanized + " application service", 120);
    }

    private static string MethodNameFromSymbol(string symbolId)
    {
        if (!symbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int parameters = symbolId.IndexOf('(');
        string head = parameters >= 0 ? symbolId[..parameters] : symbolId;
        int separator = head.LastIndexOf('.');
        return separator < 0 ? string.Empty : head[(separator + 1)..];
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

    private static string TrimPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length
            ? value[prefix.Length..]
            : value;

    private static string TrimSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) &&
        value.Length > suffix.Length
            ? value[..^suffix.Length]
            : value;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength].TrimEnd();

    private static void ThrowIfInvalidBase(ArchitectureModel baseModel)
    {
        ImmutableArray<ContractError> errors = ContractValidator.ValidateModel(baseModel);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }
    }

    private static void ValidateFactSet(SemanticC3FactSet factSet)
    {
        if (factSet.SchemaVersion != SemanticC3ContractSchema.Version)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "schema.unsupported",
                    "$.facts.schemaVersion",
                    "Only Semantic C3 fact schema version " +
                    SemanticC3ContractSchema.Version +
                    " is supported."),
            ]);
        }

        if (factSet.Facts.IsDefault || factSet.Diagnostics.IsDefault)
        {
            throw new ContractValidationException(
            [
                new ContractError(
                    "collection.missing",
                    "$.facts",
                    "Semantic C3 facts and diagnostics must be initialized collections."),
            ]);
        }
    }

    private enum BoundaryKind
    {
        MinimalApi,
        Controller,
    }

    private sealed record BoundarySeed(
        SemanticC3SourceSymbolIdentity SourceSymbol,
        BoundaryKind Kind,
        ImmutableArray<string> EvidenceIds,
        ImmutableHashSet<string> ActionSymbolIds)
    {
        public string ProjectPath => SourceSymbol.ProjectPath;
    }

    private sealed class BoundaryAccumulator(
        SemanticC3SourceSymbolIdentity sourceSymbol,
        BoundaryKind kind)
    {
        public SemanticC3SourceSymbolIdentity SourceSymbol { get; } = sourceSymbol;

        public BoundaryKind Kind
        {
            get;
            set;
        } = kind;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ActionSymbolIds { get; } = new(StringComparer.Ordinal);

        public BoundarySeed ToSeed() =>
            new(
                SourceSymbol,
                Kind,
                [.. EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
                ActionSymbolIds.ToImmutableHashSet(StringComparer.Ordinal));
    }

    private sealed class SignalAccumulator
    {
        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);

        public bool DirectInvocation
        {
            get;
            set;
        }
    }

    private sealed class ApplicationAccumulator(
        SemanticC3Fact typeFact,
        string containerId)
    {
        public SemanticC3Fact TypeFact { get; } = typeFact;

        public string ContainerId { get; } = containerId;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);

        public bool StrongEndpointSignal
        {
            get;
            set;
        }

        public bool CoLocatedOnly
        {
            get;
            set;
        }
    }

    private sealed record DiRegistration(
        string ServiceType,
        string ImplementationType,
        SemanticC3Fact Fact);
}
