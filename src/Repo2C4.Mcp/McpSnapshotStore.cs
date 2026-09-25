using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Mcp;

internal sealed class McpSnapshotStore : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SnapshotEntry> _snapshots = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly byte[] _cursorKey;
    private bool _disposed;

    internal McpSnapshotStore(
        TimeSpan? lifetime = null,
        Func<DateTimeOffset>? utcNow = null,
        byte[]? cursorKey = null)
    {
        _lifetime = lifetime ?? McpLimits.SnapshotLifetime;
        _utcNow = utcNow ?? static () => DateTimeOffset.UtcNow;
        _cursorKey = cursorKey is null ? RandomNumberGenerator.GetBytes(32) : [.. cursorKey];

        if (_lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        if (_cursorKey.Length < 32)
        {
            throw new ArgumentException("Cursor key must contain at least 32 bytes.", nameof(cursorKey));
        }
    }

    internal SnapshotEntry Store(RepositorySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);

        string canonical = ContractJson.SerializeSnapshot(snapshot);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string snapshotId = "snapshot_" + Convert.ToHexString(digest).ToLowerInvariant()[..32];
        DateTimeOffset expiresAtUtc = _utcNow().Add(_lifetime);
        SnapshotEntry entry = new(snapshotId, snapshot, expiresAtUtc);

        lock (_sync)
        {
            PurgeExpiredCore(_utcNow());
            _snapshots[snapshotId] = entry;
        }

        return entry;
    }

    internal SnapshotEntry Get(string snapshotId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 80)
        {
            throw SnapshotNotFound();
        }

        lock (_sync)
        {
            DateTimeOffset now = _utcNow();
            PurgeExpiredCore(now);
            if (!_snapshots.TryGetValue(snapshotId, out SnapshotEntry? entry))
            {
                throw SnapshotNotFound();
            }

            return entry;
        }
    }

    internal string CreateCursor(string scope, string snapshotId, string filterKey, int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(filterKey);

        string filterHash = HashFilter(filterKey);
        string payload = string.Join(
            "\n",
            "v1",
            scope,
            snapshotId,
            filterHash,
            offset.ToString(CultureInfo.InvariantCulture));
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = HMACSHA256.HashData(_cursorKey, payloadBytes);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    internal int ReadCursor(
        string? cursor,
        string scope,
        string snapshotId,
        string filterKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            string[] tokenParts = cursor.Split('.', StringSplitOptions.None);
            if (tokenParts.Length != 2)
            {
                throw CursorInvalid();
            }

            byte[] payloadBytes = Base64UrlDecode(tokenParts[0]);
            byte[] providedSignature = Base64UrlDecode(tokenParts[1]);
            byte[] expectedSignature = HMACSHA256.HashData(_cursorKey, payloadBytes);
            if (providedSignature.Length != expectedSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            {
                throw CursorInvalid();
            }

            string[] payloadParts = Encoding.UTF8.GetString(payloadBytes).Split('\n');
            if (payloadParts.Length != 5 ||
                payloadParts[0] != "v1" ||
                payloadParts[1] != scope ||
                payloadParts[2] != snapshotId ||
                payloadParts[3] != HashFilter(filterKey) ||
                !int.TryParse(payloadParts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int offset) ||
                offset < 0)
            {
                throw CursorInvalid();
            }

            return offset;
        }
        catch (FormatException)
        {
            throw CursorInvalid();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _snapshots.Clear();
            CryptographicOperations.ZeroMemory(_cursorKey);
            _disposed = true;
        }
    }

    private void PurgeExpiredCore(DateTimeOffset now)
    {
        string[] expired =
        [
            .. _snapshots
                .Where(item => item.Value.ExpiresAtUtc <= now)
                .Select(item => item.Key),
        ];

        foreach (string snapshotId in expired)
        {
            _snapshots.Remove(snapshotId);
        }
    }

    private static string HashFilter(string filterKey)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(filterKey));
        return Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += normalized.Length % 4 switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException(),
        };

        return Convert.FromBase64String(normalized);
    }

    private static McpException SnapshotNotFound() =>
        new("snapshot_not_found_or_expired: inspect the repository again in this MCP session.");

    private static McpException CursorInvalid() =>
        new("cursor_invalid: use the cursor returned by the previous page in this MCP session.");

    internal sealed record SnapshotEntry(
        string SnapshotId,
        RepositorySnapshot Snapshot,
        DateTimeOffset ExpiresAtUtc);
}
