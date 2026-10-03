namespace Repo2C4.Core.Inspection;

/// <summary>Independent budgets for Semantic C3 C# structural analysis.</summary>
public sealed record SemanticC3FactExtractionOptions(RepositoryScanOptions ScanOptions)
{
    public int MaxCSharpFiles { get; init; } = 512;

    public long MaxTotalSourceBytes { get; init; } = 8_388_608;

    public int MaxSymbols { get; init; } = 4_096;

    public int MaxRelations { get; init; } = 8_192;

    public int MaxFacts { get; init; } = 20_000;

    public int MaxDiagnostics { get; init; } = 128;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
