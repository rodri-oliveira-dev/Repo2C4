using System.Collections.Immutable;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Review;

public sealed record EvidenceReportSummary(
    int ConfirmedAssertions,
    int ReviewRequiredAssertions,
    int ScanWarnings,
    int MissingOrigins);

public sealed record EvidenceReportResult(
    string FileName,
    string Content,
    EvidenceReportSummary Summary);

public static class EvidenceReportGenerator
{
    public const string FileName = "evidence-report.md";

    public static EvidenceReportResult Generate(ArchitectureModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ImmutableArray<ContractError> structuralErrors = ValidateReportModel(model);
        if (!structuralErrors.IsEmpty)
        {
            throw new ContractValidationException(structuralErrors);
        }

        Dictionary<string, Evidence> evidenceById = model.Snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        HashSet<string> inventoriedPaths = model.Snapshot.Files
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        List<string> confirmed = [];
        List<string> review = [];
        HashSet<string> missingOrigins = new(StringComparer.Ordinal);

        foreach (ArchitectureElement element in model.Elements.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            string line = FormatAssertion(
                "Element",
                element.Id,
                element.Name,
                element.EvidenceIds,
                element.ReviewReason,
                evidenceById);
            (element.Status == ReviewStatus.Confirmed ? confirmed : review).Add(line);
            CollectMissingOrigins(
                "Element",
                element.Id,
                element.EvidenceIds,
                evidenceById,
                inventoriedPaths,
                missingOrigins);
        }

        foreach (ArchitectureRelation relation in model.Relations.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            string line = FormatAssertion(
                "Relation",
                relation.Id,
                relation.SourceId + " -> " + relation.DestinationId + ": " + relation.Description,
                relation.EvidenceIds,
                relation.ReviewReason,
                evidenceById);
            (relation.Status == ReviewStatus.Confirmed ? confirmed : review).Add(line);
            CollectMissingOrigins(
                "Relation",
                relation.Id,
                relation.EvidenceIds,
                evidenceById,
                inventoriedPaths,
                missingOrigins);
        }

        RepositoryDiagnostic[] warnings =
        [
            .. model.Snapshot.Diagnostics
                .Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.RelativePath, StringComparer.Ordinal),
        ];

        StringBuilder builder = new();
        builder.AppendLine("# Evidence report");
        builder.AppendLine();
        builder.AppendLine(
            "Generated from the reviewed architecture model. Source bodies, configuration values and evidence descriptions are intentionally omitted.");
        builder.AppendLine();
        AppendSection(builder, "Verified facts", confirmed, "No confirmed architectural assertions.");
        AppendSection(builder, "Hypotheses requiring review", review, "No hypotheses require review.");
        AppendDiagnostics(builder, warnings);
        AppendMissingOrigins(builder, missingOrigins);

        string text = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        return new EvidenceReportResult(
            FileName,
            text,
            new EvidenceReportSummary(
                confirmed.Count,
                review.Count,
                warnings.Length,
                missingOrigins.Count));
    }

    private static ImmutableArray<ContractError> ValidateReportModel(ArchitectureModel model) =>
        ContractValidator.ValidateModel(model);

    private static void AppendSection(StringBuilder builder, string title, List<string> items, string emptyMessage)
    {
        builder.AppendLine("## " + title);
        builder.AppendLine();
        if (items.Count == 0)
        {
            builder.AppendLine(emptyMessage);
        }
        else
        {
            foreach (string item in items)
            {
                builder.AppendLine("- " + item);
            }
        }

        builder.AppendLine();
    }

    private static void AppendDiagnostics(StringBuilder builder, RepositoryDiagnostic[] diagnostics)
    {
        builder.AppendLine("## Scan warnings and omissions");
        builder.AppendLine();
        if (diagnostics.Length == 0)
        {
            builder.AppendLine("No scan warnings or omissions were reported.");
        }
        else
        {
            foreach (RepositoryDiagnostic diagnostic in diagnostics)
            {
                string location = diagnostic.RelativePath is null
                    ? string.Empty
                    : " [" + Code(diagnostic.RelativePath) + "]";
                builder.AppendLine(
                    "- " + Code(diagnostic.Code) + location +
                    " (" + diagnostic.Severity.ToString().ToLowerInvariant() + ").");
            }
        }

        builder.AppendLine();
    }

    private static void AppendMissingOrigins(StringBuilder builder, HashSet<string> missingOrigins)
    {
        builder.AppendLine("## Evidence whose origin is no longer inventoried");
        builder.AppendLine();

        if (missingOrigins.Count == 0)
        {
            builder.AppendLine("All referenced evidence origins are present in the snapshot inventory.");
        }
        else
        {
            foreach (string origin in missingOrigins.OrderBy(item => item, StringComparer.Ordinal))
            {
                builder.AppendLine("- " + origin);
            }
        }

        builder.AppendLine();
    }

    private static string FormatAssertion(
        string kind,
        string id,
        string description,
        IEnumerable<string> evidenceIds,
        string? reviewReason,
        Dictionary<string, Evidence> evidenceById)
    {
        string evidence = string.Join(
            ", ",
            evidenceIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .Select(item => FormatEvidence(item, evidenceById)));

        string suffix = string.IsNullOrWhiteSpace(reviewReason)
            ? string.Empty
            : " Review: " + PlainText(reviewReason);

        return kind + " " + Code(id) + " (" + PlainText(description) + ") | evidence: " +
            (evidence.Length == 0 ? "none" : evidence) + "." + suffix;
    }

    private static string FormatEvidence(string id, Dictionary<string, Evidence> evidenceById)
    {
        if (!evidenceById.TryGetValue(id, out Evidence? evidence))
        {
            return Code(id) + " (missing from snapshot)";
        }

        string line = evidence.Line is null ? string.Empty : ":" + evidence.Line.Value;
        return Code(evidence.Id) + " (" + Code(evidence.RelativePath + line) + ")";
    }

    private static void CollectMissingOrigins(
        string assertionKind,
        string assertionId,
        IEnumerable<string> evidenceIds,
        Dictionary<string, Evidence> evidenceById,
        HashSet<string> inventoriedPaths,
        HashSet<string> missingOrigins)
    {
        string[] ids = [.. evidenceIds.Distinct(StringComparer.Ordinal)];
        if (ids.Length == 0)
        {
            missingOrigins.Add(
                assertionKind + " " + Code(assertionId) +
                ": no supporting evidence is referenced; review is required.");
            return;
        }

        foreach (string id in ids)
        {
            if (!evidenceById.TryGetValue(id, out Evidence? evidence))
            {
                continue;
            }

            if (!inventoriedPaths.Contains(evidence.RelativePath))
            {
                missingOrigins.Add(Code(evidence.Id) + " -> " + Code(evidence.RelativePath) + ".");
            }
        }
    }

    private static string PlainText(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("[", "(", StringComparison.Ordinal)
        .Replace("]", ")", StringComparison.Ordinal)
        .Replace("<", "(", StringComparison.Ordinal)
        .Replace(">", ")", StringComparison.Ordinal);

    private static string Code(string value) =>
        "`" + value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal) + "`";
}
