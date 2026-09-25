using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpSnapshotStoreTests
{
    [Fact]
    public void StoredSnapshotExpiresAndCannotBeRecovered()
    {
        DateTimeOffset now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        using McpSnapshotStore store = new(
            TimeSpan.FromMinutes(1),
            () => now,
            Enumerable.Repeat((byte)0x5A, 32).ToArray());
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_test",
            [],
            [],
            []);

        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        Assert.Same(snapshot, store.Get(entry.SnapshotId).Snapshot);

        now = now.AddMinutes(2);

        Exception exception =
            Assert.ThrowsAny<Exception>(() => store.Get(entry.SnapshotId));
        Assert.Contains("snapshot_not_found_or_expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CursorIsBoundToSessionSnapshotAndFilters()
    {
        using McpSnapshotStore store = new(
            TimeSpan.FromMinutes(1),
            cursorKey: Enumerable.Repeat((byte)0x23, 32).ToArray());

        string cursor = store.CreateCursor("evidence", "snapshot_abc", "category\npath", 50);

        Assert.Equal(50, store.ReadCursor(cursor, "evidence", "snapshot_abc", "category\npath"));
        Assert.ThrowsAny<Exception>(
            () => store.ReadCursor(cursor, "evidence", "snapshot_abc", "other-filter"));
    }
}
