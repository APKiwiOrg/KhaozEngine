using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// What the SQLite and SQL Server reset facts share: a journal with a row in every data table, written through the
/// public store seam only, and the map from each physical table to the result count that reports it.
/// </summary>
internal static class JournalResetTestSupport
{
    /// <summary>The one table a reset keeps.</summary>
    internal const string MetadataTable = "journal_metadata";

    /// <summary>
    /// Each data table and the count a reset reports for it. A fact compares this against the tables the database
    /// actually holds, so a table added to the schema without a reset count fails there.
    /// </summary>
    internal static KeyValuePair<string, long>[] CountsByTable(JournalResetResult result)
        => Ordered(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["journal_stream"] = result.StreamsDeleted,
            ["journal_event"] = result.EventsDeleted,
            ["journal_snapshot"] = result.SnapshotsDeleted,
            ["journal_projection"] = result.ProjectionsDeleted,
            ["journal_operation"] = result.OperationsDeleted,
            ["journal_operation_stream"] = result.OperationStreamsDeleted,
        });

    /// <summary>Table counts in ordinal table order, so two readings compare element by element.</summary>
    internal static KeyValuePair<string, long>[] Ordered(IEnumerable<KeyValuePair<string, long>> counts)
        => counts.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Two streams under <paramref name="prefix"/>, each initialized with a snapshot and a section, then one commit of
    /// two events and a new section on the first and one commit across both. Every data table ends with rows in it.
    /// The operation ids are fresh, so a leftover receipt in a shared database never turns a write into a replay.
    /// </summary>
    internal static async Task<Seeded> SeedAsync(IMutationJournalStore store, string prefix)
    {
        string first = prefix + "a";
        string second = prefix + "b";
        JournalOperationIdentity[] identities = Enumerable.Range(1, 4).Select(Identity).ToArray();

        Assert.Equal(
            JournalInitializeStatus.Initialized,
            (await store.InitializeAsync(Initialization(identities[0], first, 1))).Status);
        Assert.Equal(
            JournalInitializeStatus.Initialized,
            (await store.InitializeAsync(Initialization(identities[1], second, 2))).Status);
        Assert.Equal(
            JournalCommitStatus.Applied,
            (await store.CommitAsync(new JournalCommit(
                identities[2],
                new[] { new JournalStreamMutation(first, 0, new[] { Event(3), Event(4) }) },
                new[] { Section(first, "purse", 5) },
                "result.v1",
                1,
                new byte[] { 3 }))).Status);
        Assert.Equal(
            JournalCommitStatus.Applied,
            (await store.CommitAsync(new JournalCommit(
                identities[3],
                new[]
                {
                    new JournalStreamMutation(first, 2, new[] { Event(6) }),
                    new JournalStreamMutation(second, 0, new[] { Event(7) }),
                },
                Array.Empty<JournalProjectionWrite>(),
                "result.v1",
                1,
                new byte[] { 4 }))).Status);
        return new Seeded(first, second, identities);
    }

    /// <summary>A fresh initialization of <paramref name="streamKey"/> with one section, for a journal after a reset.</summary>
    internal static JournalInitialization Initialization(JournalOperationIdentity identity, string streamKey, byte value)
        => new(
            identity,
            streamKey,
            "player.v1",
            1,
            new[] { value },
            new[] { Section(streamKey, "bag", value) },
            "result.v1",
            1,
            new[] { value });

    /// <summary>A fresh operation identity carrying <paramref name="suffix"/> in its intent.</summary>
    internal static JournalOperationIdentity Identity(int suffix)
        => new(Guid.NewGuid(), "reset/account", "inventory.grant", new[] { checked((byte)suffix) });

    private static JournalEvent Event(byte value) => new("item.granted", 1, new[] { value });

    private static JournalProjectionWrite Section(string streamKey, string section, byte value)
        => new(streamKey, section, section + ".v1", 1, new[] { value });

    /// <summary>The two stream keys a seed wrote and the four operations that wrote them, in order.</summary>
    internal sealed record Seeded(string First, string Second, IReadOnlyList<JournalOperationIdentity> Operations);
}
