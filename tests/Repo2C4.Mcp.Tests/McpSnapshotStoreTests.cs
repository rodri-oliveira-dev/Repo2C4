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
    public void SnapshotIdIsStableForSameCanonicalSnapshot()
    {
        using McpSnapshotStore store = new(
            TimeSpan.FromMinutes(1),
            cursorKey: Enumerable.Repeat((byte)0x11, 32).ToArray());
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_stable",
            [],
            [],
            []);

        string first = store.Store(snapshot).SnapshotId;
        string second = store.Store(snapshot).SnapshotId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void SnapshotCannotBeRecoveredFromAnotherSessionStore()
    {
        using McpSnapshotStore firstStore = new(
            TimeSpan.FromMinutes(1),
            cursorKey: Enumerable.Repeat((byte)0x12, 32).ToArray());
        using McpSnapshotStore secondStore = new(
            TimeSpan.FromMinutes(1),
            cursorKey: Enumerable.Repeat((byte)0x13, 32).ToArray());
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_session",
            [],
            [],
            []);

        McpSnapshotStore.SnapshotEntry entry = firstStore.Store(snapshot);

        Exception exception =
            Assert.ThrowsAny<Exception>(() => secondStore.Get(entry.SnapshotId));
        Assert.Contains("snapshot_not_found_or_expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedOutOfRangeEvidenceCursorIsRejected()
    {
        using McpSnapshotStore store = new(
            TimeSpan.FromMinutes(1),
            cursorKey: Enumerable.Repeat((byte)0x14, 32).ToArray());
        Evidence evidence = new(
            "ev_test",
            "dotnet.project",
            "src/App/App.csproj",
            1,
            EvidenceSourceType.ProjectFile,
            "MSBuild project manifest is present.");
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_cursor",
            [new RepositoryFile("src/App/App.csproj", 128, null)],
            [evidence],
            []);
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        string cursor = store.CreateCursor("evidence", entry.SnapshotId, "\n", 5);
        McpArchitectureTools tools = new(Path.GetTempPath(), store);

        Exception exception = Assert.ThrowsAny<Exception>(
            () => tools.GetEvidence(
                entry.SnapshotId,
                pageSize: 1,
                cursor: cursor,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cursor_invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreEvictsOldestSnapshotWhenSessionLimitIsReached()
    {
        DateTimeOffset now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        using McpSnapshotStore store = new(
            TimeSpan.FromMinutes(30),
            () => now,
            Enumerable.Repeat((byte)0x31, 32).ToArray());

        List<McpSnapshotStore.SnapshotEntry> entries = [];
        for (int index = 0; index <= McpLimits.MaxSnapshots; index++)
        {
            RepositorySnapshot snapshot = new(
                ContractSchema.Version,
                $"repo_{index:D2}",
                [],
                [],
                []);
            entries.Add(store.Store(snapshot));
            now = now.AddSeconds(1);
        }

        Exception evicted = Assert.ThrowsAny<Exception>(() => store.Get(entries[0].SnapshotId));
        Assert.Contains("snapshot_not_found_or_expired", evicted.Message, StringComparison.Ordinal);
        Assert.Same(entries[^1].Snapshot, store.Get(entries[^1].SnapshotId).Snapshot);
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
