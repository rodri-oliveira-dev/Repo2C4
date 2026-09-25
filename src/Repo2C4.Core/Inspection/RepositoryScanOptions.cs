using System.Collections.Immutable;

namespace Repo2C4.Core.Inspection;

/// <summary>
/// The caller authorizes exactly one existing, absolute, non-linked local root.
/// No option grants access to files outside that root or overrides sensitive-file exclusions.
/// </summary>
public sealed record RepositoryScanOptions(string RootPath, string RepositoryId)
{
    public int MaxFiles { get; init; } = 1_000;

    public long MaxBytesPerFile { get; init; } = 1_048_576;

    public long MaxTotalBytes { get; init; } = 16_777_216;

    public int MaxVisitedEntries { get; init; } = 20_000;

    /// <summary>Optional case-insensitive glob patterns; empty means all safe text files.</summary>
    public ImmutableArray<string> IncludePatterns { get; init; } = [];

    /// <summary>Optional additional case-insensitive glob patterns; cannot disable built-in exclusions.</summary>
    public ImmutableArray<string> ExcludePatterns { get; init; } = [];
}
