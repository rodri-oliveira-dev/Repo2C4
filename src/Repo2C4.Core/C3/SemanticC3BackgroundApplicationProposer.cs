using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.C3;

/// <summary>
/// Proposes an application/processor component only when a messaging-consumer boundary
/// structurally depends on or invokes one concrete non-infrastructure type.
/// </summary>
public static class SemanticC3BackgroundApplicationProposer
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
        SemanticC3Fact[] facts =
        [
            .. factSet.Facts
                .Where(fact => selectedProjects.Contains(fact.ProjectPath))
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];
        SemanticC3Fact[] typeFacts =
        [
            .. facts.Where(fact => fact.Kind == SemanticC3FactKind.TypeDeclaration),
        ];

        HashSet<string> consumerTypeIds =
        [
            .. facts
                .Where(fact =>
                    fact.Kind == SemanticC3FactKind.MessagingCandidate &&
                    ConsumerRole(fact.SourceSymbol.SymbolId))
                .Select(fact => fact.SourceSymbol.SymbolId),
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

        Dictionary<string, ApplicationAccumulator> candidates = new(StringComparer.Ordinal);

        foreach (SemanticC3Fact injection in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.ConstructorInjection &&
                     consumerTypeIds.Contains(DeclaringTypeId(fact.SourceSymbol.SymbolId) ?? string.Empty)))
        {
            string? dependency = RelatedTypeName(injection.RelatedSymbolId);
            if (dependency is null)
            {
                continue;
            }

            SemanticC3Fact? typeFact = ResolveDependencyType(
                dependency,
                registrations,
                typeFacts,
                out SemanticC3Fact? registrationFact);
            AddCandidate(
                candidates,
                typeFact,
                registrationFact is null ? [injection] : [injection, registrationFact],
                facts,
                selected.Id);
        }

        foreach (SemanticC3Fact invocation in facts.Where(fact =>
                     fact.Kind == SemanticC3FactKind.SymbolInvocation &&
                     consumerTypeIds.Contains(DeclaringTypeId(fact.SourceSymbol.SymbolId) ?? string.Empty)))
        {
            string? targetType = RelatedMethodTypeName(invocation.RelatedSymbolId);
            if (targetType is null)
            {
                continue;
            }

            SemanticC3Fact? typeFact = ResolveConcreteType(typeFacts, targetType);
            AddCandidate(
                candidates,
                typeFact,
                [invocation],
                facts,
                selected.Id);
        }

        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            [selected.Id],
            [
                .. candidates.Values
                    .Select(CreateCandidate)
                    .OrderBy(component => component.Id, StringComparer.Ordinal),
            ],
            []);

        ImmutableArray<ContractError> errors = SemanticC3Validator.Validate(proposal);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return proposal;
    }

    private static void AddCandidate(
        Dictionary<string, ApplicationAccumulator> candidates,
        SemanticC3Fact? typeFact,
        IEnumerable<SemanticC3Fact> signals,
        SemanticC3Fact[] allFacts,
        string containerId)
    {
        if (typeFact is null ||
            typeFact.Description.StartsWith("Declares interface", StringComparison.Ordinal) ||
            IsInfrastructure(typeFact, allFacts) ||
            !HasApplicationMethod(typeFact, allFacts))
        {
            return;
        }

        if (!candidates.TryGetValue(typeFact.SourceSymbol.Id, out ApplicationAccumulator? candidate))
        {
            candidate = new ApplicationAccumulator(containerId, typeFact);
            candidates.Add(typeFact.SourceSymbol.Id, candidate);
        }

        candidate.EvidenceIds.Add(typeFact.Id);
        foreach (SemanticC3Fact signal in signals)
        {
            candidate.EvidenceIds.Add(signal.Id);
        }
    }

    private static SemanticC3ComponentCandidate CreateCandidate(
        ApplicationAccumulator candidate)
    {
        string typeName = SimpleTypeName(candidate.TypeFact.SourceSymbol.SymbolId);
        string baseName = TrimSuffix(typeName, "Processor", "Handler", "Service", "UseCase");
        string name = Humanize(baseName);
        if (typeName.EndsWith("Processor", StringComparison.Ordinal))
        {
            name += " processor";
        }
        else if (typeName.EndsWith("Handler", StringComparison.Ordinal))
        {
            name += " handler";
        }
        else
        {
            name += " application service";
        }

        return new SemanticC3ComponentCandidate(
            StableIds.ForSemanticC3Component(
                candidate.ContainerId,
                SemanticC3ComponentCategory.ApplicationService,
                candidate.TypeFact.SourceSymbol.Id),
            candidate.ContainerId,
            SemanticC3ComponentCategory.ApplicationService,
            name.Trim(),
            "Processes a transport-neutral application responsibility reached from a messaging consumer boundary.",
            [.. candidate.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)],
            candidate.TypeFact.SourceSymbol,
            ReviewStatus.RequiresReview,
            "Consumer wiring or direct collaboration supports this application responsibility, but its C3 boundary remains reviewable.");
    }

    private static bool IsInfrastructure(
        SemanticC3Fact typeFact,
        SemanticC3Fact[] facts)
    {
        string name = SimpleTypeName(typeFact.SourceSymbol.SymbolId);
        if (InfrastructureSuffixes.Any(suffix =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return facts.Any(fact =>
            fact.SourceSymbol.Id == typeFact.SourceSymbol.Id &&
            fact.Kind is SemanticC3FactKind.PersistenceCandidate
                or SemanticC3FactKind.MessagingCandidate
                or SemanticC3FactKind.HostedService);
    }

    private static bool HasApplicationMethod(
        SemanticC3Fact typeFact,
        SemanticC3Fact[] facts) =>
        facts.Any(fact =>
            fact.ProjectPath == typeFact.ProjectPath &&
            fact.Kind == SemanticC3FactKind.MethodDeclaration &&
            DeclaringTypeId(fact.SourceSymbol.SymbolId) == typeFact.SourceSymbol.SymbolId);

    private static SemanticC3Fact? ResolveDependencyType(
        string dependency,
        DiRegistration[] registrations,
        SemanticC3Fact[] typeFacts,
        out SemanticC3Fact? registrationFact)
    {
        registrationFact = null;
        SemanticC3Fact? direct = ResolveConcreteType(typeFacts, dependency);
        if (direct is not null)
        {
            return direct;
        }

        string simple = SimpleTypeName(dependency);
        DiRegistration[] matches =
        [
            .. registrations.Where(registration =>
                SimpleTypeName(registration.ServiceType)
                    .Equals(simple, StringComparison.OrdinalIgnoreCase)),
        ];
        if (matches.Length != 1)
        {
            return null;
        }

        registrationFact = matches[0].Fact;
        return ResolveConcreteType(typeFacts, matches[0].ImplementationType);
    }

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
        return new DiRegistration(service, implementation, fact);
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
                 SimpleTypeName(fact.SourceSymbol.SymbolId)
                    .Equals(simple, StringComparison.OrdinalIgnoreCase))),
        ];

        SemanticC3Fact[] exact =
        [
            .. candidates.Where(fact => fact.SourceSymbol.SymbolId == "T:" + normalized),
        ];
        return exact.Length == 1
            ? exact[0]
            : candidates.Length == 1
                ? candidates[0]
                : null;
    }

    private static bool ConsumerRole(string symbolId)
    {
        string name = SimpleTypeName(symbolId);
        bool consumer =
            name.Contains("Consumer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Subscriber", StringComparison.OrdinalIgnoreCase);
        bool publisher =
            name.Contains("Publisher", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Producer", StringComparison.OrdinalIgnoreCase);
        return consumer && !publisher;
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

    private static string? RelatedMethodTypeName(string? related)
    {
        if (related is null || !related.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        string value = related[2..];
        int method = value.LastIndexOf('.');
        return method <= 0 ? null : value[..method];
    }

    private static string? DeclaringTypeId(string methodSymbolId)
    {
        if (!methodSymbolId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        int parameters = methodSymbolId.IndexOf('(');
        string head = parameters >= 0 ? methodSymbolId[..parameters] : methodSymbolId;
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
            sourcePath.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    private static int ProjectDepth(string projectPath) =>
        DirectoryPart(projectPath).Count(character => character == '/') + 1;

    private static string DirectoryPart(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string NormalizeTypeName(string value)
    {
        string normalized = value.Trim().TrimEnd('?');
        int generic = normalized.IndexOf('<');
        return generic < 0 ? normalized : normalized[..generic];
    }

    private static string SimpleTypeName(string value)
    {
        string normalized = value.StartsWith("T:", StringComparison.Ordinal)
            ? value[2..]
            : NormalizeTypeName(value);
        int separator = normalized.LastIndexOf('.');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static string TrimSuffix(string value, params string[] suffixes)
    {
        foreach (string suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) &&
                value.Length > suffix.Length)
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

    private static string Humanize(string value)
    {
        StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                char.IsLower(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private sealed record DiRegistration(
        string ServiceType,
        string ImplementationType,
        SemanticC3Fact Fact);

    private sealed class ApplicationAccumulator(
        string containerId,
        SemanticC3Fact typeFact)
    {
        public string ContainerId { get; } = containerId;

        public SemanticC3Fact TypeFact { get; } = typeFact;

        public HashSet<string> EvidenceIds { get; } = new(StringComparer.Ordinal);
    }
}
