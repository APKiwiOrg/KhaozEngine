using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalValueOwnershipTests
{
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
    public void Commit_owns_caller_collections_and_result_bytes()
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
