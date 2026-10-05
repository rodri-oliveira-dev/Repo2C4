using System.Collections.Immutable;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;

namespace Repo2C4.Core.C3;

/// <summary>
/// Proposes integration-adapter components only when normalized external evidence can be
/// correlated to exactly one concrete local type in the finding source file.
/// </summary>
public static class SemanticC3IntegrationProposer
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

        HashSet<string> projects = ResolveSelectedProjects(baseModel, selected);
        SemanticC3Fact[] typeFacts =
        [
            .. factSet.Facts
                .Where(fact =>
                    fact.Kind == SemanticC3FactKind.TypeDeclaration &&
                    projects.Contains(fact.ProjectPath) &&
                    !fact.Description.StartsWith("Declares interface", StringComparison.Ordinal))
                .OrderBy(fact => fact.Id, StringComparer.Ordinal),
        ];

        List<SemanticC3ComponentCandidate> components = [];
        List<SemanticC3RelationCandidate> relations = [];

        foreach (ExternalIntegrationEvidence evidence in externalEvidence.Evidence
                     .Where(item =>
                         projects.Contains(item.ProjectPath) &&
                         item.Kind is ExternalIntegrationKind.Http
                             or ExternalIntegrationKind.Cache
                             or ExternalIntegrationKind.Storage &&
                         ExternalIntegrationArchitectureMapper.IsSupported(item))
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            SemanticC3Fact[] matches =
            [
                .. typeFacts.Where(fact =>
                    fact.ProjectPath == evidence.ProjectPath &&
                    fact.SourcePath == evidence.SourcePath),
            ];
            if (matches.Length != 1)
            {
                continue;
            }

            SemanticC3Fact typeFact = matches[0];
            string simpleName = SimpleTypeName(typeFact.SourceSymbol.SymbolId);
            string technology = Humanize(evidence.Technology);
            string name = evidence.Kind switch
            {
                ExternalIntegrationKind.Cache => technology + " cache adapter",
                ExternalIntegrationKind.Storage => technology + " storage adapter",
                _ => Humanize(TrimSuffix(simpleName, "Client", "Adapter")) + " integration adapter",
            };

            SemanticC3ComponentCandidate component = new(
                StableIds.ForSemanticC3Component(
                    selected.Id,
                    SemanticC3ComponentCategory.IntegrationAdapter,
                    typeFact.SourceSymbol.Id + "|" + evidence.Id),
                selected.Id,
                SemanticC3ComponentCategory.IntegrationAdapter,
                name,
                "Connects the selected container to an evidence-backed external integration.",
                [typeFact.Id, evidence.Id],
                typeFact.SourceSymbol,
                ReviewStatus.RequiresReview,
                "The external finding is correlated to one concrete local type, but the C3 adapter boundary remains reviewable.");
            components.Add(component);

            ArchitectureRelation? mapped = baseModel.Relations.SingleOrDefault(relation =>
                relation.EvidenceIds.Contains(evidence.Id, StringComparer.Ordinal) &&
                (relation.SourceId == selected.Id || relation.DestinationId == selected.Id));
            if (mapped is null)
            {
                continue;
            }

            string destinationId = mapped.SourceId == selected.Id
                ? mapped.DestinationId
                : mapped.SourceId;
            relations.Add(new SemanticC3RelationCandidate(
                StableIds.ForRelation(
                    component.Id,
                    destinationId,
                    "semantic-c3|integration|" + evidence.Id),
                component.Id,
                destinationId,
                SemanticC3RelationTargetKind.ArchitectureElement,
                mapped.Description,
                [typeFact.Id, evidence.Id],
                ReviewStatus.RequiresReview,
                "The existing C1/C2 external relation is associated with this local integration adapter; C3 runtime flow remains reviewable."));
        }

        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            [selected.Id],
            [.. components.OrderBy(component => component.Id, StringComparer.Ordinal)],
            [.. relations.OrderBy(relation => relation.Id, StringComparer.Ordinal)]);

        ImmutableArray<ContractError> errors = SemanticC3Validator.Validate(proposal);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return proposal;
    }

    private static HashSet<string> ResolveSelectedProjects(
        ArchitectureModel model,
        ArchitectureElement selected)
    {
        Dictionary<string, Evidence> evidenceById = model.Snapshot.Evidence
            .ToDictionary(evidence => evidence.Id, StringComparer.Ordinal);
        string[] projects =
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

            string? project = projects
                .Where(candidate => IsWithinProject(evidence.RelativePath, candidate))
                .OrderByDescending(ProjectDepth)
                .ThenBy(candidate => candidate, StringComparer.Ordinal)
                .FirstOrDefault();
            if (project is not null)
            {
                selectedProjects.Add(project);
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

    private static string SimpleTypeName(string symbolId)
    {
        string value = symbolId.StartsWith("T:", StringComparison.Ordinal)
            ? symbolId[2..]
            : symbolId;
        int separator = value.LastIndexOf('.');
        return separator < 0 ? value : value[(separator + 1)..];
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return "External";
        }

        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                char.IsLower(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current is '-' or '_' ? ' ' : current);
        }

        return builder.ToString().Trim();
    }
}
