using System.Collections.Immutable;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.ExternalIntegrations;

/// <summary>InspectionReport compatibility accepted by the external integration boundary.</summary>
public static class ExternalIntegrationReportSchema
{
    public const int SupportedMajor = 1;

    public const int MinimumMinor = 6;

    public const string MinimumVersion = "1.6";
}

public enum ExternalIntegrationKind
{
    Http,
    Messaging,
    Database,
    Cache,
    Storage,
    Rpc,
    Unknown,
}

public enum ExternalIntegrationDirection
{
    Outbound,
    Publish,
    Consume,
    Read,
    Write,
    Bidirectional,
    Unknown,
}

public enum ExternalIntegrationConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>Repository-relative provenance imported from one normalized integration finding.</summary>
public sealed record ExternalIntegrationEvidence(
    string Id,
    string? OriginalFindingId,
    string ProjectPath,
    ExternalIntegrationKind Kind,
    ExternalIntegrationDirection Direction,
    string Technology,
    string? Target,
    string? ResourceType,
    string? ConfigurationKey,
    string? Contract,
    string SourcePath,
    int SourceLine,
    ExternalIntegrationConfidence Confidence,
    ImmutableArray<string> Signals);

/// <summary>A controlled import diagnostic that never contains report or source bodies.</summary>
public sealed record ExternalIntegrationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Path,
    string Message);

/// <summary>Bounded normalized output; no raw JSON or secret-bearing configuration values are retained.</summary>
public sealed record ExternalIntegrationEvidenceResult(
    string ReportSchemaVersion,
    bool DiscoveryCompleted,
    bool Truncated,
    ImmutableArray<ExternalIntegrationEvidence> Evidence,
    ImmutableArray<ExternalIntegrationDiagnostic> Diagnostics);

/// <summary>Hard limits applied while reading an untrusted InspectionReport JSON stream.</summary>
public sealed record ExternalIntegrationEvidenceReadOptions
{
    public const long DefaultMaxJsonBytes = 8 * 1024 * 1024;

    public const int DefaultMaxFindings = 5_000;

    public const int DefaultMaxSignalsPerFinding = 32;

    public const int DefaultMaxTextLength = 512;

    public long MaxJsonBytes { get; init; } = DefaultMaxJsonBytes;

    public int MaxFindings { get; init; } = DefaultMaxFindings;

    public int MaxSignalsPerFinding { get; init; } = DefaultMaxSignalsPerFinding;

    public int MaxTextLength { get; init; } = DefaultMaxTextLength;
}

/// <summary>
/// Reads external integration findings through the public, versioned JSON boundary only.
/// Implementations must not execute DotNetRepoInspector or retain the input report.
/// </summary>
public interface IExternalIntegrationEvidenceSource
{
    ValueTask<ExternalIntegrationEvidenceResult> ReadAsync(
        Stream report,
        ExternalIntegrationEvidenceReadOptions? options = null,
        CancellationToken cancellationToken = default);
}
