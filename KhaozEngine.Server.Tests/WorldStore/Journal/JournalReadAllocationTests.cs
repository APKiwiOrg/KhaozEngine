using System;
using System.Security.Cryptography;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[Collection("AllocSensitive")]
public sealed class JournalReadAllocationTests
{
    [Fact]
    public void Defensive_memory_property_allocates_one_payload_copy_per_read()
    {
        const int reads = 64;
        var payload = new byte[4_096];
        payload[0] = 7;
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, payload);

        _ = projection.Data.Span[0];
        long before = GC.GetAllocatedBytesForCurrentThread();
        int observed = 0;
        for (int i = 0; i < reads; i++) observed += projection.Data.Span[0];
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(reads * 7, observed);
        Assert.True(allocated >= reads * payload.Length,
            $"{nameof(JournalProjectionWrite.Data)} allocated {allocated} bytes across {reads} reads");
    }

    [Fact]
    public void Span_accessors_return_the_owned_bytes_and_checksums()
    {
        JournalOperationIdentity identity = Identity(new byte[] { 1, 2, 3 });
        var journalEvent = new JournalEvent("event", 1, new byte[] { 4, 5, 6 });
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, new byte[] { 7, 8, 9 });
        JournalCommit commit = Commit(identity, journalEvent, projection, new byte[] { 10, 11, 12 });
        var compaction = new JournalCompaction("stream", 1, "snapshot", 1, new byte[] { 13, 14, 15 }, null);

        Assert.Equal(new byte[] { 1, 2, 3 }, identity.NormalizedIntentSpan.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, journalEvent.PayloadSpan.ToArray());
        Assert.Equal(SHA256.HashData(new byte[] { 4, 5, 6 }), journalEvent.PayloadChecksumSpan.ToArray());
        Assert.Equal(new byte[] { 7, 8, 9 }, projection.DataSpan.ToArray());
        Assert.Equal(SHA256.HashData(new byte[] { 7, 8, 9 }), projection.DataChecksumSpan.ToArray());
        Assert.Equal(new byte[] { 10, 11, 12 }, commit.ResultDataSpan.ToArray());
        Assert.Equal(SHA256.HashData(new byte[] { 10, 11, 12 }), commit.ResultChecksumSpan.ToArray());
        Assert.Equal(new byte[] { 13, 14, 15 }, compaction.SnapshotDataSpan.ToArray());
        Assert.Equal(SHA256.HashData(new byte[] { 13, 14, 15 }), compaction.SnapshotChecksumSpan.ToArray());
    }

    [Fact]
    public void Span_accessors_do_not_allocate_after_warmup()
    {
        JournalOperationIdentity identity = Identity(new byte[] { 1, 2, 3 });
        var journalEvent = new JournalEvent("event", 1, new byte[] { 4, 5, 6 });
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, new byte[] { 7, 8, 9 });
        JournalCommit commit = Commit(identity, journalEvent, projection, new byte[] { 10, 11, 12 });
        var compaction = new JournalCompaction("stream", 1, "snapshot", 1, new byte[] { 13, 14, 15 }, null);

        int observed = 0;
        for (int i = 0; i < 16; i++) observed = ReadHotBytes(identity, journalEvent, projection, commit, compaction);

        AllocAssert.NoPerCallAllocation("journal span accessors", () =>
        {
            int total = 0;
            for (int i = 0; i < 256; i++) total += ReadHotBytes(identity, journalEvent, projection, commit, compaction);
            observed = total;
        });

        Assert.NotEqual(0, observed);
    }

    [Fact]
    public void Span_accessors_keep_constructor_buffers_defensively_owned()
    {
        var intent = new byte[] { 1, 2 };
        var payload = new byte[] { 3, 4 };
        var projectionData = new byte[] { 5, 6 };
        var resultData = new byte[] { 7, 8 };
        var snapshotData = new byte[] { 9, 10 };
        JournalOperationIdentity identity = Identity(intent);
        var journalEvent = new JournalEvent("event", 1, payload);
        var projection = new JournalProjectionWrite("stream", "section", "schema", 1, projectionData);
        JournalCommit commit = Commit(identity, journalEvent, projection, resultData);
        var compaction = new JournalCompaction("stream", 1, "snapshot", 1, snapshotData, null);

        intent[0] = 20;
        payload[0] = 21;
        projectionData[0] = 22;
        resultData[0] = 23;
        snapshotData[0] = 24;

        Assert.Equal(1, identity.NormalizedIntentSpan[0]);
        Assert.Equal(3, journalEvent.PayloadSpan[0]);
        Assert.Equal(5, projection.DataSpan[0]);
        Assert.Equal(7, commit.ResultDataSpan[0]);
        Assert.Equal(9, compaction.SnapshotDataSpan[0]);
    }

    private static JournalOperationIdentity Identity(byte[] intent)
        => new(Guid.NewGuid(), "scope", "action", intent);

    private static JournalCommit Commit(
        JournalOperationIdentity identity,
        JournalEvent journalEvent,
        JournalProjectionWrite projection,
        byte[] resultData)
        => new(
            identity,
            new[] { new JournalStreamMutation("stream", 0, new[] { journalEvent }) },
            new[] { projection },
            "result",
            1,
            resultData);

    private static int ReadHotBytes(
        JournalOperationIdentity identity,
        JournalEvent journalEvent,
        JournalProjectionWrite projection,
        JournalCommit commit,
        JournalCompaction compaction)
        => identity.NormalizedIntentSpan[0]
            + journalEvent.PayloadSpan[0]
            + journalEvent.PayloadChecksumSpan[0]
            + projection.DataSpan[0]
            + projection.DataChecksumSpan[0]
            + commit.ResultDataSpan[0]
            + commit.ResultChecksumSpan[0]
            + compaction.SnapshotDataSpan[0]
            + compaction.SnapshotChecksumSpan[0];
}
