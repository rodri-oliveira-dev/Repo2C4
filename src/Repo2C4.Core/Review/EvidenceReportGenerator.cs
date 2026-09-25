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
        _ = ContractJson.SerializeModel(model);

        Dictionary<string, Evidence> evidenceById = model.Snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        HashSet<string> inventoriedPaths = model.Snapshot.Files
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        List<string> confirmed = [];
        List<string> review = [];
        int missingOrigins = 0;

        foreach (ArchitectureElement element in model.Elements.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            string line = FormatAssertion("Element", element.Id, element.Name, element.EvidenceIds, element.ReviewReason, evidenceById);
            (element.Status == ReviewStatus.Confirmed ? confirmed : review).Add(line);
            missingOrigins += CountMissingOrigins(element.EvidenceIds, evidenceById, inventoriedPaths);
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
            missingOrigins += CountMissingOrigins(relation.EvidenceIds, evidenceById, inventoriedPaths);
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
        builder.AppendLine("Generated from the reviewed architecture model. Source bodies and configuration values are intentionally omitted.");
        builder.AppendLine();
        AppendSection(builder, "Verified facts", confirmed, "No confirmed architectural assertions.");
        AppendSection(builder, "Hypotheses requiring review", review, "No hypotheses require review.");
        AppendDiagnostics(builder, warnings);
        AppendMissingOrigins(builder, model, evidenceById, inventoriedPaths);

        string text = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        return new EvidenceReportResult(
            FileName,
            text,
            new EvidenceReportSummary(confirmed.Count, review.Count, warnings.Length, missingOrigins));
    }

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
                string location = diagnostic.RelativePath is null ? string.Empty : " [" + Escape(diagnostic.RelativePath) + "]";
                builder.AppendLine("- `" + Escape(diagnostic.Code) + "`" + location + ": " + Escape(diagnostic.Message));
            }
        }

        builder.AppendLine();
    }

    private static void AppendMissingOrigins(
        StringBuilder builder,
        ArchitectureModel model,
        Dictionary<string, Evidence> evidenceById,
        HashSet<string> inventoriedPaths)
    {
        builder.AppendLine("## Evidence whose origin is no longer inventoried");
        builder.AppendLine();

        List<string> missing = [];
        IEnumerable<string> referencedIds = model.Elements.SelectMany(item => item.EvidenceIds)
            .Concat(model.Relations.SelectMany(item => item.EvidenceIds))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal);

        foreach (string evidenceId in referencedIds)
        {
            if (!evidenceById.TryGetValue(evidenceId, out Evidence? evidence))
            {
                missing.Add("- `" + Escape(evidenceId) + "`: referenced evidence is not present in the snapshot.");
                continue;
            }

            if (!inventoriedPaths.Contains(evidence.RelativePath))
            {
                missing.Add("- `" + Escape(evidence.Id) + "` -> `" + Escape(evidence.RelativePath) + "`.");
            }
        }

        if (missing.Count == 0)
        {
            builder.AppendLine("All referenced evidence origins are present in the snapshot inventory.");
        }
        else
        {
            foreach (string line in missing)
            {
                builder.AppendLine(line);
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
            evidenceIds.OrderBy(item => item, StringComparer.Ordinal).Select(item => FormatEvidence(item, evidenceById)));

        string suffix = string.IsNullOrWhiteSpace(reviewReason)
            ? string.Empty
            : " Review: " + Escape(reviewReason);

        return kind + " `" + Escape(id) + "` (" + Escape(description) + ") | evidence: " +
            (evidence.Length == 0 ? "none" : evidence) + "." + suffix;
    }

    private static string FormatEvidence(string id, Dictionary<string, Evidence> evidenceById)
    {
        if (!evidenceById.TryGetValue(id, out Evidence? evidence))
        {
            return "`" + Escape(id) + "` (missing)";
        }

        string line = evidence.Line is null ? string.Empty : ":" + evidence.Line.Value;
        return "`" + Escape(evidence.Id) + "` (`" + Escape(evidence.RelativePath) + line + "`)";
    }

    private static int CountMissingOrigins(
        IEnumerable<string> evidenceIds,
        Dictionary<string, Evidence> evidenceById,
        HashSet<string> inventoriedPaths)
    {
        int count = 0;
        foreach (string id in evidenceIds.Distinct(StringComparer.Ordinal))
        {
            if (!evidenceById.TryGetValue(id, out Evidence? evidence) || !inventoriedPaths.Contains(evidence.RelativePath))
            {
                count++;
            }
        }

        return count;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
