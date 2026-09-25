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
