using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("SQL Server mutation journal")]
public sealed class MutationJournalProjectionTests
{
    [Fact]
    public Task In_memory_cursor_omission_reset_and_epoch_rotation_preserve_state()
    {
        var store = new InMemoryMutationJournalStore();
        return AssertProjectionContractAsync(store, store);
    }

    [Fact]
    public async Task Sqlite_cursor_omission_reset_and_epoch_rotation_preserve_state()
    {
        using var scope = new Task6SqliteScope();
        using SqliteMutationJournalStore store = scope.Open();
        await AssertProjectionContractAsync(store, store);
    }

    [SqlServerFact]
    public async Task Sql_server_cursor_omission_reset_and_epoch_rotation_preserve_state()
    {
        using var scope = new Task6SqlServerScope();
        SqlServerJournalPrefixStore store = scope.Open();
        await AssertProjectionContractAsync(store, store.Maintenance);
    }

    private static async Task AssertProjectionContractAsync(
        IMutationJournalStore store,
        IMutationJournalMaintenance maintenance)
    {
        await store.InitializeAsync(Initialization(
            1,
            snapshotValue: 7,
            Projection("bag", 1),
            Projection("skills", 2)));
        JournalProjectionRead baseline = await store.ReadProjectionsAsync(
            new JournalProjectionQuery(StreamKey));
        Assert.Equal(new[] { "bag", "skills" }, baseline.Sections.Select(section => section.SectionName));
        Assert.NotNull(baseline.Cursor);

        await store.CommitAsync(Commit(2, 0, new byte[] { 3 }, 3, Projection("bag", 9)));
        JournalProjectionRead delta = await store.ReadProjectionsAsync(
            new JournalProjectionQuery(StreamKey, baseline.Cursor));
        JournalProjectionSection changed = Assert.Single(delta.Sections);
        Assert.Equal("bag", changed.SectionName);
        Assert.Equal(1, changed.SourceVersion);
        Assert.Equal(new byte[] { 9 }, changed.Data.ToArray());

        JournalProjectionRead unchanged = await store.ReadProjectionsAsync(
            new JournalProjectionQuery(StreamKey, delta.Cursor));
        Assert.Equal(JournalProjectionReadStatus.Success, unchanged.Status);
        Assert.Empty(unchanged.Sections);
        Assert.NotNull(unchanged.Cursor);
        Assert.Equal(1, unchanged.HeadVersion);

        Guid priorEpoch = JournalProjectionCursor.DecodeForTest(unchanged.Cursor!).Epoch;
        Guid rotatedEpoch = await maintenance.RotateStoreEpochAsync();
        JournalProjectionRead reset = await store.ReadProjectionsAsync(
            new JournalProjectionQuery(StreamKey, unchanged.Cursor));

        Assert.NotEqual(priorEpoch, rotatedEpoch);
        Assert.Equal(JournalProjectionReadStatus.ResetRequired, reset.Status);
        Assert.Equal(new[] { "bag", "skills" }, reset.Sections.Select(section => section.SectionName));
        Assert.Equal(new byte[] { 9 }, reset.Sections[0].Data.ToArray());
        Assert.Equal(new byte[] { 2 }, reset.Sections[1].Data.ToArray());
        Assert.Equal(rotatedEpoch, JournalProjectionCursor.DecodeForTest(reset.Cursor!).Epoch);
        Assert.Equal(1, reset.HeadVersion);
        Assert.Equal(new byte[] { 7 }, (await store.LoadSnapshotAsync(StreamKey))!.Data.ToArray());
        Assert.Single((await ReadAllAsync(store)).Events);
        Assert.Equal(new byte[] { 3 }, (await ReadAllAsync(store)).Events[0].Payload.ToArray());
    }
}
