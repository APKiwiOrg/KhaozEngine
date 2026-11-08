using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalValueOwnershipTests
{
    [Fact]
    public void Refusal_stream_keys_are_owned_sorted_read_only_snapshots()
    {
        string[] source = ["player/b", "player/a"];
        var error = new JournalStoreException(JournalStoreFailureKind.Unavailable,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.OperationStreams,
            source, "refused");
        source[0] = "changed";

        Assert.Equal(new[] { "player/a", "player/b" }, error.StreamKeys);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)error.StreamKeys)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)error.StreamKeys).SyncRoot);
        var wholeStore = new JournalStoreException(JournalStoreFailureKind.Unavailable,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.WholeStore, null, "refused");
        Assert.Empty(wholeStore.StreamKeys);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)wholeStore.StreamKeys).SyncRoot);
    }

    [Fact]
    public void Public_byte_accessors_do_not_expose_mutable_backing_arrays()
    {
        var identity = new JournalOperationIdentity(Guid.NewGuid(), "scope", "action", new byte[] { 1 });
        var journalEvent = new JournalEvent("event", 1, new byte[] { 2 });
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, new byte[] { 3 });
        var commit = new JournalCommit(identity, new[] { new JournalStreamMutation("stream", 0, new[] { journalEvent }) }, new[] { projection }, "result", 1, new byte[] { 4 });
        var initialization = new JournalInitialization(identity, "stream", "snapshot", 1, new byte[] { 5 }, new[] { projection }, "result", 1, new byte[] { 6 });
        var receipt = new JournalCommitReceipt(Guid.NewGuid(), DateTimeOffset.UnixEpoch, new[] { new JournalStreamVersionRange("stream", 0, 1, 1) }, "result", 1, new byte[] { 7 });
        var snapshot = new JournalSnapshot("stream", 1, "snapshot", 1, new byte[] { 8 }, DateTimeOffset.UnixEpoch);
        var storedEvent = new JournalStoredEvent("stream", 1, Guid.NewGuid(), 0, "event", 1, new byte[] { 9 }, DateTimeOffset.UnixEpoch);
        var section = new JournalProjectionSection("stream", "section", 1, "schema", 1, new byte[] { 10 }, DateTimeOffset.UnixEpoch);
        var compaction = new JournalCompaction("stream", 1, "snapshot", 1, new byte[] { 11 }, null);
        JournalFingerprint fingerprint = JournalCanonicalizer.CreateCommitFingerprint(commit);
        (string Name, Func<ReadOnlyMemory<byte>> Read)[] accessors =
        {
            ("JournalOperationIdentity.NormalizedIntent", () => identity.NormalizedIntent),
            ("JournalEvent.Payload", () => journalEvent.Payload),
            ("JournalEvent.PayloadChecksum", () => journalEvent.PayloadChecksum),
            ("JournalProjectionWrite.Data", () => projection.Data),
            ("JournalProjectionWrite.DataChecksum", () => projection.DataChecksum),
            ("JournalCommit.ResultData", () => commit.ResultData),
            ("JournalCommit.ResultChecksum", () => commit.ResultChecksum),
            ("JournalInitialization.SnapshotData", () => initialization.SnapshotData),
            ("JournalInitialization.SnapshotChecksum", () => initialization.SnapshotChecksum),
            ("JournalInitialization.ResultData", () => initialization.ResultData),
            ("JournalInitialization.ResultChecksum", () => initialization.ResultChecksum),
            ("JournalCommitReceipt.ResultData", () => receipt.ResultData),
            ("JournalCommitReceipt.ResultChecksum", () => receipt.ResultChecksum),
            ("JournalSnapshot.Data", () => snapshot.Data),
            ("JournalSnapshot.DataChecksum", () => snapshot.DataChecksum),
            ("JournalStoredEvent.Payload", () => storedEvent.Payload),
            ("JournalStoredEvent.PayloadChecksum", () => storedEvent.PayloadChecksum),
            ("JournalProjectionSection.Data", () => section.Data),
            ("JournalProjectionSection.DataChecksum", () => section.DataChecksum),
            ("JournalCompaction.SnapshotData", () => compaction.SnapshotData),
            ("JournalCompaction.SnapshotChecksum", () => compaction.SnapshotChecksum),
            ("JournalFingerprint.CanonicalBytes", () => fingerprint.CanonicalBytes),
            ("JournalFingerprint.Digest", () => fingerprint.Digest),
        };
        var leaked = new List<string>();

        foreach ((string name, Func<ReadOnlyMemory<byte>> read) in accessors)
        {
            ReadOnlyMemory<byte> exposed = read();
            Assert.True(MemoryMarshal.TryGetArray(exposed, out ArraySegment<byte> segment));
            Assert.NotNull(segment.Array);
            byte expected = exposed.Span[0];
            segment.Array[segment.Offset] ^= 0xFF;
            if (read().Span[0] != expected) leaked.Add(name);
        }

        Assert.Empty(leaked);
    }

    [Fact]
    public void Operation_identity_owns_intent_bytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var identity = new JournalOperationIdentity(Guid.NewGuid(), "world/account", "bank.deposit", bytes);
        bytes[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, identity.NormalizedIntent.ToArray());
        byte[] returned = identity.NormalizedIntent.ToArray();
        returned[1] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, identity.NormalizedIntent.ToArray());
    }

    [Fact]
    public void Event_projection_snapshot_and_compaction_own_bytes()
    {
        var bytes = new byte[] { 4, 5, 6 };
        var journalEvent = new JournalEvent("event", 1, bytes);
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, bytes);
        var snapshot = new JournalSnapshot("stream", 3, "schema", 1, bytes, DateTimeOffset.UnixEpoch);
        var compaction = new JournalCompaction("stream", 3, "schema", 1, bytes, pruneThroughVersion: null);
        bytes[0] = 9;

        Assert.Equal(new byte[] { 4, 5, 6 }, journalEvent.Payload.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, projection.Data.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, snapshot.Data.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, compaction.SnapshotData.ToArray());
    }

    [Fact]
    public async Task Commit_owns_caller_collections_and_result_bytes()
    {
        var streams = new List<JournalStreamMutation> { JournalTestData.Mutation("stream/a") };
        var projections = new List<JournalProjectionWrite> { JournalTestData.Projection("stream/a") };
        var result = new byte[] { 7, 8 };
        var commit = new JournalCommit(JournalTestData.Identity(), streams, projections, "result", 1, result);

        streams.Clear();
        projections.Clear();
        result[0] = 9;

        Assert.Single(commit.StreamMutations);
        Assert.Single(commit.ProjectionWrites);
        Assert.Equal(new byte[] { 7, 8 }, commit.ResultData.ToArray());
        // W7-B18b: replacing a validated projection through SyncRoot bypassed membership checks.
        AssertReadOnly(commit.StreamMutations, JournalTestData.Mutation("untouched"));
        AssertReadOnly(commit.ProjectionWrites, JournalTestData.Projection("untouched"));
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(new JournalInitialization(new(Guid.NewGuid(), "scope", "initialize", []),
            "stream/a", "snapshot", 1, [], [], "result", 1, []));
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(commit)).Status);
        JournalProjectionSection saved = Assert.Single((await store.ReadProjectionsAsync(new("stream/a"))).Sections);
        Assert.Equal("stream/a", saved.StreamKey);
        Assert.Equal(new byte[] { 2 }, saved.Data.ToArray());
        Assert.Empty((await store.ReadProjectionsAsync(new("untouched"))).Sections);
    }

    [Fact]
    public async Task Initialization_projection_lists_cannot_change_after_validation()
    {
        JournalProjectionWrite[] source = [JournalTestData.Projection()];
        var initialization = JournalTestData.Initialization(projections: source);
        source[0] = JournalTestData.Projection("untouched");
        AssertReadOnly(initialization.ProjectionWrites, source[0]);
        var store = new InMemoryMutationJournalStore();
        Assert.Equal(JournalInitializeStatus.Initialized, (await store.InitializeAsync(initialization)).Status);
        JournalProjectionSection saved = Assert.Single((await store.ReadProjectionsAsync(new("player/1"))).Sections);
        Assert.Equal("player/1", saved.StreamKey);
        Assert.Equal(new byte[] { 2 }, saved.Data.ToArray());
        Assert.Empty((await store.ReadProjectionsAsync(new("untouched"))).Sections);
    }

    [Fact]
    public async Task Mutation_event_lists_cannot_change_the_payload_written_to_storage()
    {
        JournalEvent[] source = [JournalTestData.Event(payload: [3])];
        var mutation = JournalTestData.Mutation(events: source);
        source[0] = JournalTestData.Event(payload: [9]);
        AssertReadOnly(mutation.Events, source[0]);
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(JournalTestData.Initialization());
        var commit = new JournalCommit(new(Guid.NewGuid(), "scope", "commit", []), [mutation], [], "result", 1, []);
        Assert.Equal(JournalCommitStatus.Applied, (await store.CommitAsync(commit)).Status);
        JournalStoredEvent saved = Assert.Single((await store.ReadEventsAsync(new("player/1", 0, null, 128, 1024))).Events);
        Assert.Equal(new byte[] { 3 }, saved.Payload.ToArray());
    }

    [Fact]
    public void Receipt_and_read_values_own_store_buffers_and_collections()
    {
        var result = new byte[] { 1, 2 };
        var ranges = new List<JournalStreamVersionRange> { new("stream", 0, 1, 1) };
        var receipt = new JournalCommitReceipt(Guid.NewGuid(), DateTimeOffset.UnixEpoch, ranges, "result", 1, result);
        var eventPayload = new byte[] { 3, 4 };
        var events = new List<JournalStoredEvent>
        {
            new("stream", 1, Guid.NewGuid(), 0, "event", 1, eventPayload, DateTimeOffset.UnixEpoch),
        };
        var page = new JournalEventPage(JournalEventPageStatus.Success, "stream", 1, events, reachedThroughVersion: true);
        var projectionData = new byte[] { 5, 6 };
        var sections = new List<JournalProjectionSection>
        {
            new("stream", "bag", 1, "bag", 1, projectionData, DateTimeOffset.UnixEpoch),
        };
        var read = new JournalProjectionRead(JournalProjectionReadStatus.Success, "stream", 1, sections, "cursor");

        result[0] = 9;
        ranges.Clear();
        eventPayload[0] = 9;
        events.Clear();
        projectionData[0] = 9;
        sections.Clear();

        Assert.Equal(new byte[] { 1, 2 }, receipt.ResultData.ToArray());
        Assert.Single(receipt.Streams);
        Assert.Equal(new byte[] { 3, 4 }, page.Events.Single().Payload.ToArray());
        Assert.Equal(new byte[] { 5, 6 }, read.Sections.Single().Data.ToArray());
        AssertReadOnly(receipt.Streams, new JournalStreamVersionRange("untouched", 0, 1, 1));
        AssertReadOnly(page.Events, new JournalStoredEvent("untouched", 1, Guid.NewGuid(), 0, "event", 1, [9], DateTimeOffset.UnixEpoch));
        AssertReadOnly(read.Sections, new JournalProjectionSection("untouched", "bag", 1, "bag", 1, [9], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Stream_page_order_cannot_change_after_validation()
    {
        JournalStreamEntry[] source = [new("stream", 1)];
        var page = new JournalStreamPage(source, "stream");
        source[0] = new("untouched", 2);
        AssertReadOnly(page.Streams, source[0]);
        Assert.Equal("stream", Assert.Single(page.Streams).StreamKey);
        Assert.Equal("stream", page.ContinuationKey);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.NotEmpty(values);
        T[] before = values.ToArray();
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)values).SyncRoot);
        Assert.Throws<NotSupportedException>(() => ((IList<T>)values)[0] = replacement);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)values)[0] = replacement);
        Assert.Equal(before, values);
    }

    [Fact]
    public void Fingerprint_buffers_are_independent_from_returned_arrays()
    {
        JournalFingerprint fingerprint = JournalCanonicalizer.CreateIntentFingerprint(JournalTestData.Identity(new byte[] { 1 }));
        byte[] bytes = fingerprint.CanonicalBytes.ToArray();
        byte[] digest = fingerprint.Digest.ToArray();
        bytes[0] = 0;
        digest[0] = 0;

        Assert.Equal((byte)'K', fingerprint.CanonicalBytes.Span[0]);
        Assert.NotEqual(0, fingerprint.Digest.Span[0]);
    }
}
