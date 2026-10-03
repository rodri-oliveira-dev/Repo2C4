using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Repo2C4.Core.Contracts;

/// <summary>Produces stable, order-independent IDs from logical keys without exposing absolute paths.</summary>
public static class StableIds
{
    public static string ForEvidence(
        string category,
        string relativePath,
        int? line,
        string objectiveDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectiveDescription);

        if (!ContractValidator.IsNormalizedRelativePath(relativePath))
        {
            throw new ArgumentException("Evidence path must be a normalized repository-relative path.", nameof(relativePath));
        }

        if (line is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Line must be a positive 1-based index.");
        }

        return Hash("ev", category, relativePath, line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, objectiveDescription);
    }

    public static string ForElement(ArchitectureElementKind kind, string stableLogicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLogicalKey);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return Hash("el", kind.ToString(), stableLogicalKey);
    }

    public static string ForRelation(string sourceId, string destinationId, string stableLogicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLogicalKey);

        return Hash("rel", sourceId, destinationId, stableLogicalKey);
    }

    public static string ForSemanticC3SourceSymbol(string projectPath, string symbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        if (!ContractValidator.IsNormalizedRelativePath(projectPath))
        {
            throw new ArgumentException(
                "Source symbol project path must be a normalized repository-relative path.",
                nameof(projectPath));
        }

        return Hash("sym", projectPath, symbolId);
    }

    public static string ForSemanticC3Component(
        string containerId,
        SemanticC3ComponentCategory category,
        string stableLogicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLogicalKey);

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return Hash("cmp", containerId, category.ToString(), stableLogicalKey);
    }

    public static string ForSemanticC3Fact(
        string projectPath,
        string sourcePath,
        string sourceSymbolId,
        SemanticC3FactKind kind,
        string category,
        string relatedSymbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSymbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(relatedSymbolId);

        if (!ContractValidator.IsNormalizedRelativePath(projectPath))
        {
            throw new ArgumentException(
                "Semantic C3 fact project path must be repository-relative.",
                nameof(projectPath));
        }

        if (!ContractValidator.IsNormalizedRelativePath(sourcePath))
        {
            throw new ArgumentException(
                "Semantic C3 fact source path must be repository-relative.",
                nameof(sourcePath));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return Hash(
            "fact",
            projectPath,
            sourcePath,
            sourceSymbolId,
            kind.ToString(),
            category,
            relatedSymbolId);
    }

    private static string Hash(string prefix, params string[] parts)
    {
        // Length-prefix fields so ["ab", "c"] cannot alias ["a", "bc"].
        StringBuilder key = new();
        foreach (string part in parts)
        {
            key.Append(part.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(part);
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()));
        return prefix + "_" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }
}
