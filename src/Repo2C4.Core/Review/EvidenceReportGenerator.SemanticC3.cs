using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Review;

public static partial class EvidenceReportGenerator
{
    public const string SemanticC3FileName = "semantic-c3-evidence-report.md";

    public static EvidenceReportResult GenerateSemanticC3(
        ArchitectureModel baseModel,
        SemanticC3RelationBuildResult result,
        SemanticC3FactSet factSet)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(factSet);

        _ = ContractJson.SerializeModel(baseModel);
        ImmutableArray<ContractError> errors =
            SemanticC3Validator.Validate(result.Proposal);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        Dictionary<string, SemanticC3Fact> facts = factSet.Facts
            .ToDictionary(fact => fact.Id, StringComparer.Ordinal);
        Dictionary<string, Evidence> architectureEvidence = baseModel.Snapshot.Evidence
            .ToDictionary(evidence => evidence.Id, StringComparer.Ordinal);

        StringBuilder builder = new();
        builder.AppendLine("# Semantic C3 evidence report");
        builder.AppendLine();
        builder.AppendLine(
            "Generated from bounded Semantic C3 facts and the existing C1/C2 model. Source bodies and configuration values are intentionally omitted.");
        builder.AppendLine();

        builder.AppendLine("## Component candidates");
        builder.AppendLine();
        if (result.Proposal.Components.IsEmpty)
        {
            builder.AppendLine("No Semantic C3 component candidates.");
        }
        else
        {
            foreach (SemanticC3ComponentCandidate component in
                     result.Proposal.Components.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                builder.AppendLine(
                    "- Component " + Code(component.Id) + " (" +
                    PlainText(component.Name) + ", " +
                    Code(component.Category.ToString()) + ") | signals: " +
                    SignalList(component.EvidenceIds, facts, architectureEvidence) +
                    ". Status: " + Code(component.Status.ToString()) + "." +
                    ReviewSuffix(component.ReviewReason));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Relations");
        builder.AppendLine();

        if (result.Proposal.Relations.IsEmpty)
        {
            builder.AppendLine("No Semantic C3 relations.");
        }
        else
        {
            foreach (SemanticC3RelationCandidate relation in
                     result.Proposal.Relations.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                builder.AppendLine(
                    "- Relation " + Code(relation.Id) + " (" +
                    Code(relation.SourceComponentId) + " -> " +
                    Code(relation.DestinationId) + ": " +
                    PlainText(relation.Description) + ") | evidence: " +
                    EvidenceList(relation.EvidenceIds, facts, architectureEvidence) +
                    " | signals: " +
                    SignalList(relation.EvidenceIds, facts, architectureEvidence) +
                    " | missing signals: " +
                    MissingSignals(relation, facts) +
                    ". Status: " + Code(relation.Status.ToString()) + "." +
                    ReviewSuffix(relation.ReviewReason));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Graph diagnostics");
        builder.AppendLine();
        if (result.Diagnostics.IsEmpty)
        {
            builder.AppendLine("No Semantic C3 graph diagnostics.");
        }
        else
        {
            foreach (RepositoryDiagnostic diagnostic in
                     result.Diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal))
            {
                builder.AppendLine(
                    "- " + Code(diagnostic.Code) + " (" +
                    diagnostic.Severity.ToString().ToLowerInvariant() + "): " +
                    PlainText(diagnostic.Message));
            }
        }

        builder.AppendLine();

        int confirmed = result.Proposal.Components.Count(item =>
                item.Status == ReviewStatus.Confirmed) +
            result.Proposal.Relations.Count(item =>
                item.Status == ReviewStatus.Confirmed);
        int review = result.Proposal.Components.Count(item =>
                item.Status == ReviewStatus.RequiresReview) +
            result.Proposal.Relations.Count(item =>
                item.Status == ReviewStatus.RequiresReview);

        return new EvidenceReportResult(
            SemanticC3FileName,
            builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal),
            new EvidenceReportSummary(
                confirmed,
                review,
                result.Diagnostics.Count(item =>
                    item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error),
                0));
    }

    private static string SignalList(
        IEnumerable<string> evidenceIds,
        Dictionary<string, SemanticC3Fact> facts,
        Dictionary<string, Evidence> architectureEvidence)
    {
        string[] signals =
        [
            .. evidenceIds
                .Distinct(StringComparer.Ordinal)
                .Select(id => facts.TryGetValue(id, out SemanticC3Fact? fact)
                    ? fact.Category
                    : architectureEvidence.TryGetValue(id, out Evidence? evidence)
                        ? evidence.Category
                        : "missing")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal),
        ];

        return signals.Length == 0
            ? "none"
            : string.Join(", ", signals.Select(Code));
    }

    private static string EvidenceList(
        IEnumerable<string> evidenceIds,
        Dictionary<string, SemanticC3Fact> facts,
        Dictionary<string, Evidence> architectureEvidence)
    {
        string[] items =
        [
            .. evidenceIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id =>
                {
                    if (facts.TryGetValue(id, out SemanticC3Fact? fact))
                    {
                        string line = fact.Line is null
                            ? string.Empty
                            : ":" + fact.Line.Value;
                        return Code(id) + " (" + Code(fact.SourcePath + line) + ")";
                    }

                    if (architectureEvidence.TryGetValue(id, out Evidence? evidence))
                    {
                        string line = evidence.Line is null
                            ? string.Empty
                            : ":" + evidence.Line.Value;
                        return Code(id) + " (" + Code(evidence.RelativePath + line) + ")";
                    }

                    return Code(id) + " (missing)";
                }),
        ];

        return items.Length == 0 ? "none" : string.Join(", ", items);
    }

    private static string MissingSignals(
        SemanticC3RelationCandidate relation,
        Dictionary<string, SemanticC3Fact> facts)
    {
        if (relation.Status == ReviewStatus.Confirmed)
        {
            return "none";
        }

        if (relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement)
        {
            return "confirmed exact external mapping";
        }

        SemanticC3Fact[] relationFacts =
        [
            .. relation.EvidenceIds
                .Where(facts.ContainsKey)
                .Select(id => facts[id]),
        ];

        bool wiring = relationFacts.Any(fact =>
            fact.Kind is SemanticC3FactKind.ConstructorInjection
                or SemanticC3FactKind.EndpointDependency
                or SemanticC3FactKind.MethodParameter
                or SemanticC3FactKind.DependencyInjectionRegistration
                or SemanticC3FactKind.EndpointHandler);
        bool invocation = relationFacts.Any(fact =>
            fact.Kind == SemanticC3FactKind.SymbolInvocation);

        List<string> missing = [];
        if (!wiring)
        {
            missing.Add("DI/handler wiring");
        }

        if (!invocation)
        {
            missing.Add("direct symbol invocation");
        }

        return missing.Count == 0
            ? "architectural confirmation"
            : string.Join(", ", missing);
    }

    private static string ReviewSuffix(string? reviewReason) =>
        string.IsNullOrWhiteSpace(reviewReason)
            ? string.Empty
            : " Review: " + PlainText(reviewReason);
}
