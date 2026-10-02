using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.ExternalIntegrations;

/// <summary>Imports only Integration Discovery findings from an untrusted InspectionReport JSON stream.</summary>
public sealed class InspectionReportIntegrationEvidenceSource : IExternalIntegrationEvidenceSource
{
    private static readonly HashSet<string> ForbiddenPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authentication",
        "authorization",
        "connectionString",
        "credential",
        "headers",
        "password",
        "payload",
        "query",
        "rawJson",
        "secret",
        "sourceBody",
        "sourceCode",
        "token",
    };

    private static readonly string[] SecretMarkers =
    [
        "accountkey=",
        "apikey=",
        "clientsecret=",
        "password=",
        "pwd=",
        "sharedaccesskey=",
        "token=",
    ];

    public async ValueTask<ExternalIntegrationEvidenceResult> ReadAsync(
        Stream report,
        ExternalIntegrationImportContext context,
        ExternalIntegrationEvidenceReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Snapshot);

        options ??= new ExternalIntegrationEvidenceReadOptions();
        ValidateOptions(options);

        if (!report.CanRead)
        {
            throw new ArgumentException("The report stream must be readable.", nameof(report));
        }

        ImmutableArray<ContractError> snapshotErrors = ContractValidator.ValidateSnapshot(context.Snapshot);
        if (!snapshotErrors.IsEmpty)
        {
            throw new ContractValidationException(snapshotErrors);
        }

        byte[]? bytes = await ReadBoundedAsync(report, options.MaxJsonBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return Failure("", "external.report.tooLarge", "$", "The inspection report exceeds the configured byte limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            return Import(document.RootElement, context, options, cancellationToken);
        }
        catch (JsonException)
        {
            return Failure("", "external.report.invalidJson", "$", "The inspection report is not valid bounded JSON.");
        }
    }

    private static ExternalIntegrationEvidenceResult Import(
        JsonElement root,
        ExternalIntegrationImportContext context,
        ExternalIntegrationEvidenceReadOptions options,
        CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "schemaVersion", out string? schemaVersion))
        {
            return Failure("", "external.schema.missing", "$.schemaVersion", "A schema version is required.");
        }

        if (!IsCompatibleVersion(schemaVersion!))
        {
            return Failure(schemaVersion!, "external.schema.incompatible", "$.schemaVersion", "Only additive InspectionReport schema 1.6 or later 1.x versions are supported.");
        }

        string? repositoryName = null;
        string? commitSha = null;
        if (root.TryGetProperty("repository", out JsonElement repository) && repository.ValueKind == JsonValueKind.Object)
        {
            TryString(repository, "name", out repositoryName);
            TryString(repository, "commitSha", out commitSha);
        }

        if ((repositoryName is not null && !IsSafeText(repositoryName, options.MaxTextLength)) ||
            (commitSha is not null && !IsCommitSha(commitSha)))
        {
            return Failure(schemaVersion!, "external.repository.invalid", "$.repository", "Repository identity metadata is invalid.");
        }

        ExternalIntegrationEvidenceResult? identityFailure = CheckIdentity(
            schemaVersion!, repositoryName, commitSha, context);
        if (identityFailure is not null)
        {
            return identityFailure;
        }

        if (!TryDiscovery(root, out bool completed, out bool truncated))
        {
            return new ExternalIntegrationEvidenceResult(
                schemaVersion!, repositoryName, commitSha, false, false, [],
                [Diagnostic("external.discovery.incomplete", "$.integrationDiscovery", "Integration Discovery was not enabled and completed for this report.")]);
        }

        if (!root.TryGetProperty("integrations", out JsonElement integrations) || integrations.ValueKind != JsonValueKind.Array)
        {
            return new ExternalIntegrationEvidenceResult(
                schemaVersion!, repositoryName, commitSha, completed, truncated, [],
                [Diagnostic("external.findings.missing", "$.integrations", "The integrations collection must be an array.")]);
        }

        if (integrations.GetArrayLength() > options.MaxFindings)
        {
            return new ExternalIntegrationEvidenceResult(
                schemaVersion!, repositoryName, commitSha, completed, truncated, [],
                [Diagnostic("external.findings.limit", "$.integrations", "The integrations collection exceeds the configured finding limit.")]);
        }

        HashSet<string> reportProjects = ReadReportProjectPaths(root);
        HashSet<string> snapshotPaths = context.Snapshot.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        List<ExternalIntegrationEvidence> evidence = [];
        List<ExternalIntegrationDiagnostic> diagnostics = [];

        if (context.ExpectedRepositoryName is null && context.ExpectedCommitSha is null)
        {
            diagnostics.Add(new ExternalIntegrationDiagnostic(
                "external.correlation.pathOnly",
                DiagnosticSeverity.Info,
                "$.repository",
                "Repository identity and commit were not supplied by the caller; correlation uses inventoried project and source paths."));
        }

        if (truncated)
        {
            diagnostics.Add(new ExternalIntegrationDiagnostic(
                "external.discovery.truncated",
                DiagnosticSeverity.Warning,
                "$.integrationDiscovery.truncated",
                "The producer reports truncated integration discovery results."));
        }

        int index = 0;
        foreach (JsonElement finding in integrations.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = "$.integrations[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            ExternalIntegrationEvidence? imported = TryImportFinding(
                finding, path, reportProjects, snapshotPaths, options, diagnostics);
            if (imported is not null)
            {
                evidence.Add(imported);
            }

            index++;
        }

        ImmutableArray<ExternalIntegrationEvidence> canonicalEvidence =
        [
            .. evidence
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];
        ImmutableArray<ExternalIntegrationDiagnostic> canonicalDiagnostics =
        [
            .. diagnostics
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal),
        ];

        return new ExternalIntegrationEvidenceResult(
            schemaVersion!, repositoryName, commitSha, completed, truncated, canonicalEvidence, canonicalDiagnostics);
    }

    private static ExternalIntegrationEvidence? TryImportFinding(
        JsonElement finding,
        string path,
        HashSet<string> reportProjects,
        HashSet<string> snapshotPaths,
        ExternalIntegrationEvidenceReadOptions options,
        List<ExternalIntegrationDiagnostic> diagnostics)
    {
        if (finding.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Diagnostic("external.finding.invalid", path, "An integration finding must be an object."));
            return null;
        }

        if (ContainsForbiddenProperty(finding))
        {
            diagnostics.Add(Diagnostic("external.finding.forbiddenField", path, "An integration finding contains a forbidden sensitive or source-content field."));
            return null;
        }

        if (!TryRequiredString(finding, "id", options, out string? originalId) ||
            !TryRequiredString(finding, "projectPath", options, out string? projectPath) ||
            !TryRequiredString(finding, "kind", options, out string? kindText) ||
            !TryRequiredString(finding, "direction", options, out string? directionText) ||
            !TryRequiredString(finding, "technology", options, out string? technology) ||
            !TryRequiredString(finding, "confidence", options, out string? confidenceText) ||
            !finding.TryGetProperty("source", out JsonElement source) || source.ValueKind != JsonValueKind.Object ||
            !TryRequiredString(source, "path", options, out string? sourcePath) ||
            !source.TryGetProperty("line", out JsonElement lineElement) || !lineElement.TryGetInt32(out int sourceLine) || sourceLine <= 0)
        {
            diagnostics.Add(Diagnostic("external.finding.incomplete", path, "A required integration finding field is missing or invalid."));
            return null;
        }

        if (!IsFindingId(originalId!))
        {
            diagnostics.Add(Diagnostic("external.finding.id", path + ".id", "The original finding ID is invalid."));
            return null;
        }

        if (!ContractValidator.IsNormalizedRelativePath(projectPath) || !ContractValidator.IsNormalizedRelativePath(sourcePath))
        {
            diagnostics.Add(Diagnostic("external.finding.path", path, "Project and source paths must be normalized repository-relative paths."));
            return null;
        }

        if (!reportProjects.Contains(projectPath!) || !snapshotPaths.Contains(projectPath!) || !snapshotPaths.Contains(sourcePath!))
        {
            diagnostics.Add(Diagnostic("external.repository.mismatch", path, "Finding provenance does not match the external project inventory and local snapshot paths."));
            return null;
        }

        if (!TryKind(kindText!, out ExternalIntegrationKind kind) ||
            !TryDirection(directionText!, out ExternalIntegrationDirection direction) ||
            !TryConfidence(confidenceText!, out ExternalIntegrationConfidence confidence))
        {
            diagnostics.Add(Diagnostic("external.finding.vocabulary", path, "A finding uses an unsupported kind, direction, or confidence value."));
            return null;
        }

        string? target = OptionalString(finding, "target", options);
        string? resourceType = OptionalString(finding, "resourceType", options);
        string? configurationKey = OptionalString(finding, "configurationKey", options);
        string? contract = OptionalString(finding, "contract", options);
        if (HasInvalidOptionalString(finding, options, "target", "resourceType", "configurationKey", "contract"))
        {
            diagnostics.Add(Diagnostic("external.finding.invalid", path, "An optional integration finding field is invalid."));
            return null;
        }

        if (!finding.TryGetProperty("signals", out JsonElement signalsElement) || signalsElement.ValueKind != JsonValueKind.Array ||
            signalsElement.GetArrayLength() is 0 || signalsElement.GetArrayLength() > options.MaxSignalsPerFinding)
        {
            diagnostics.Add(Diagnostic("external.finding.signals", path + ".signals", "Signals must be a non-empty array within the configured limit."));
            return null;
        }

        List<string> signals = [];
        foreach (JsonElement signalElement in signalsElement.EnumerateArray())
        {
            if (signalElement.ValueKind != JsonValueKind.String || !IsSafeText(signalElement.GetString(), options.MaxTextLength))
            {
                diagnostics.Add(Diagnostic("external.finding.signals", path + ".signals", "Every signal must be a bounded non-sensitive string."));
                return null;
            }

            signals.Add(signalElement.GetString()!);
        }

        ImmutableArray<string> canonicalSignals = [.. signals.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)];
        string category = Category(kind, direction);
        string description = Description(kind, direction, technology!, target, resourceType, contract);
        if (description.Length > options.MaxTextLength)
        {
            diagnostics.Add(Diagnostic("external.finding.description", path, "The normalized evidence description exceeds the configured text limit."));
            return null;
        }

        string identity = string.Join(
            '\u001f',
            projectPath, kindText, directionText, technology, target, resourceType, configurationKey, contract,
            sourcePath, sourceLine.ToString(CultureInfo.InvariantCulture), confidenceText,
            string.Join('\u001e', canonicalSignals));
        string id = StableIds.ForEvidence(category, sourcePath!, sourceLine, identity);

        return new ExternalIntegrationEvidence(
            id, category, description, originalId, projectPath!, kind, direction, technology!, target,
            resourceType, configurationKey, contract, sourcePath!, sourceLine, confidence, canonicalSignals);
    }

    private static ExternalIntegrationEvidenceResult? CheckIdentity(
        string schemaVersion,
        string? repositoryName,
        string? commitSha,
        ExternalIntegrationImportContext context)
    {
        if (context.ExpectedRepositoryName is not null &&
            !string.Equals(repositoryName, context.ExpectedRepositoryName, StringComparison.Ordinal))
        {
            return Failure(schemaVersion, "external.repository.identityMismatch", "$.repository.name", "The report repository identity does not match the expected repository.", repositoryName, commitSha);
        }

        if (context.ExpectedCommitSha is not null &&
            !string.Equals(commitSha, context.ExpectedCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(schemaVersion, "external.repository.commitMismatch", "$.repository.commitSha", "The report commit does not match the expected repository revision.", repositoryName, commitSha);
        }

        return null;
    }

    private static HashSet<string> ReadReportProjectPaths(JsonElement root)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        if (!root.TryGetProperty("projects", out JsonElement projects) || projects.ValueKind != JsonValueKind.Array)
        {
            return paths;
        }

        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.ValueKind == JsonValueKind.Object && TryString(project, "path", out string? path) &&
                ContractValidator.IsNormalizedRelativePath(path))
            {
                paths.Add(path!);
            }
        }

        return paths;
    }

    private static bool TryDiscovery(JsonElement root, out bool completed, out bool truncated)
    {
        completed = false;
        truncated = false;
        if (!root.TryGetProperty("integrationDiscovery", out JsonElement discovery) || discovery.ValueKind != JsonValueKind.Object ||
            !discovery.TryGetProperty("enabled", out JsonElement enabledElement) ||
            !discovery.TryGetProperty("completed", out JsonElement completedElement) ||
            !discovery.TryGetProperty("truncated", out JsonElement truncatedElement) ||
            enabledElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            completedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            truncatedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        completed = completedElement.GetBoolean();
        truncated = truncatedElement.GetBoolean();
        return enabledElement.GetBoolean() && completed;
    }

    private static bool IsCompatibleVersion(string version)
    {
        string[] parts = version.Split('.');
        return parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) &&
            major == ExternalIntegrationReportSchema.SupportedMajor &&
            minor >= ExternalIntegrationReportSchema.MinimumMinor;
    }

    private static bool TryRequiredString(
        JsonElement element,
        string propertyName,
        ExternalIntegrationEvidenceReadOptions options,
        out string? value) =>
        TryString(element, propertyName, out value) && IsSafeText(value, options.MaxTextLength);

    private static bool TryString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static string? OptionalString(JsonElement element, string propertyName, ExternalIntegrationEvidenceReadOptions options)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String && IsSafeText(property.GetString(), options.MaxTextLength)
            ? property.GetString()
            : null;
    }

    private static bool HasInvalidOptionalString(
        JsonElement element,
        ExternalIntegrationEvidenceReadOptions options,
        params string[] propertyNames) =>
        propertyNames.Any(name =>
            element.TryGetProperty(name, out JsonElement property) &&
            (property.ValueKind != JsonValueKind.String || !IsSafeText(property.GetString(), options.MaxTextLength)));

    private static bool IsSafeText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl) &&
        !SecretMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsForbiddenProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (ForbiddenPropertyNames.Contains(property.Name) || ContainsForbiddenProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (ContainsForbiddenProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryKind(string value, out ExternalIntegrationKind kind) =>
        TryVocabulary(value, out kind);

    private static bool TryDirection(string value, out ExternalIntegrationDirection direction) =>
        TryVocabulary(value, out direction);

    private static bool TryConfidence(string value, out ExternalIntegrationConfidence confidence) =>
        TryVocabulary(value, out confidence);

    private static bool TryVocabulary<T>(string value, out T parsed)
        where T : struct, Enum
    {
        if (Enum.TryParse(value, true, out parsed) &&
            string.Equals(value, parsed.ToString(), StringComparison.OrdinalIgnoreCase) &&
            char.IsLower(value[0]))
        {
            return true;
        }

        parsed = default;
        return false;
    }

    private static bool IsFindingId(string value) =>
        value.Length == 28 && value.StartsWith("integration-", StringComparison.Ordinal) &&
        value[12..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static string Category(ExternalIntegrationKind kind, ExternalIntegrationDirection direction) => kind switch
    {
        ExternalIntegrationKind.Http when direction == ExternalIntegrationDirection.Outbound => "external.http.outbound",
        ExternalIntegrationKind.Messaging when direction == ExternalIntegrationDirection.Publish => "external.messaging.publish",
        ExternalIntegrationKind.Messaging when direction == ExternalIntegrationDirection.Consume => "external.messaging.consume",
        ExternalIntegrationKind.Database => "external.database",
        ExternalIntegrationKind.Cache => "external.cache",
        ExternalIntegrationKind.Storage => "external.storage",
        ExternalIntegrationKind.Rpc => "external.rpc",
        _ => "external.integration.unknown",
    };

    private static string Description(
        ExternalIntegrationKind kind,
        ExternalIntegrationDirection direction,
        string technology,
        string? target,
        string? resourceType,
        string? contract)
    {
        string endpoint = target ?? "an unidentified external target";
        string resource = resourceType is null ? endpoint : resourceType + " " + endpoint;
        string message = contract ?? "a message";
        return (kind, direction) switch
        {
            (ExternalIntegrationKind.Http, ExternalIntegrationDirection.Outbound) =>
                "Project calls " + endpoint + " via HTTP using " + technology + ".",
            (ExternalIntegrationKind.Messaging, ExternalIntegrationDirection.Publish) =>
                "Project publishes " + message + " to " + resource + " using " + technology + ".",
            (ExternalIntegrationKind.Messaging, ExternalIntegrationDirection.Consume) =>
                "Project consumes " + message + " from " + resource + " using " + technology + ".",
            _ => "Project uses " + resource + " for " + kind.ToString().ToLowerInvariant() + " via " + technology + ".",
        };
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new((int)Math.Min(maxBytes, 81_920));
        byte[] chunk = new byte[81_920];
        long total = 0;
        while (true)
        {
            int requested = (int)Math.Min(chunk.Length, maxBytes - total + 1);
            int read = await stream.ReadAsync(chunk.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateOptions(ExternalIntegrationEvidenceReadOptions options)
    {
        if (options.MaxJsonBytes is <= 0 or > int.MaxValue || options.MaxFindings <= 0 ||
            options.MaxSignalsPerFinding <= 0 || options.MaxTextLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "All importer limits must be positive and the JSON byte limit must fit in memory.");
        }
    }

    private static ExternalIntegrationDiagnostic Diagnostic(string code, string path, string message) =>
        new(code, DiagnosticSeverity.Error, path, message);

    private static ExternalIntegrationEvidenceResult Failure(
        string schemaVersion,
        string code,
        string path,
        string message,
        string? repositoryName = null,
        string? commitSha = null) =>
        new(schemaVersion, repositoryName, commitSha, false, false, [], [Diagnostic(code, path, message)]);
}
