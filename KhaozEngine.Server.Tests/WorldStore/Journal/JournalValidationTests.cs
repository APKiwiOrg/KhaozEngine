using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalValidationTests
{
    [Fact]
    public void Engine_limits_match_the_version_one_contract()
    {
        Assert.Equal(16, JournalLimits.Maximum.StreamsPerOperation);
        Assert.Equal(128, JournalLimits.Maximum.EventsPerOperation);
        Assert.Equal(64, JournalLimits.Maximum.ProjectionWritesPerOperation);
        Assert.Equal(64, JournalLimits.Maximum.ProjectionSectionsPerStream);
        Assert.Equal(64 * 1024, JournalLimits.Maximum.NormalizedIntentBytes);
        Assert.Equal(256 * 1024, JournalLimits.Maximum.EventPayloadBytes);
        Assert.Equal(64 * 1024, JournalLimits.Maximum.ResultBytes);
        Assert.Equal(2 * 1024 * 1024, JournalLimits.Maximum.ProjectionSectionBytes);
        Assert.Equal(8 * 1024 * 1024, JournalLimits.Maximum.SnapshotBytes);
        Assert.Equal(2_048, JournalLimits.Maximum.EventsPerReadPage);
        Assert.Equal(8 * 1024 * 1024, JournalLimits.Maximum.AggregateCommitBytes);
        Assert.Equal(8 * 1024 * 1024, JournalLimits.Maximum.AggregateEventReadBytes);
        Assert.Equal(8 * 1024 * 1024, JournalLimits.Maximum.AggregateProjectionBytesPerStream);
        Assert.Equal(256, JournalLimits.Maximum.StreamKeyCharacters);
        Assert.Equal(128, JournalLimits.Maximum.IdentityCharacters);
    }

    [Fact]
    public void Host_limits_can_only_configure_engine_limits_downward()
    {
        var limits = new JournalLimits(streamsPerOperation: 2, eventPayloadBytes: 100);
        Assert.Equal(2, limits.StreamsPerOperation);
        Assert.Equal(100, limits.EventPayloadBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalLimits(streamsPerOperation: 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalLimits(eventPayloadBytes: 0));
    }

    [Fact]
    public void Commit_stream_limit_is_inclusive()
    {
        JournalTestData.Commit(streamCount: 16).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Commit(streamCount: 17));
    }

    [Fact]
    public void Commit_event_limit_is_inclusive()
    {
        JournalEvent[] accepted = Enumerable.Range(0, 128).Select(i => JournalTestData.Event($"e/{i}")).ToArray();
        JournalTestData.Commit(streams: new[] { JournalTestData.Mutation(events: accepted) }).Validate();

        JournalEvent[] rejected = Enumerable.Range(0, 129).Select(i => JournalTestData.Event($"e/{i}")).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Commit(streams: new[] { JournalTestData.Mutation(events: rejected) }));
    }

    [Fact]
    public void Commit_projection_write_limit_is_inclusive()
    {
        JournalProjectionWrite[] accepted = Enumerable.Range(0, 64).Select(i => JournalTestData.Projection(sectionName: $"s/{i}")).ToArray();
        JournalTestData.Commit(projections: accepted).Validate();

        JournalProjectionWrite[] rejected = Enumerable.Range(0, 65).Select(i => JournalTestData.Projection(sectionName: $"s/{i}")).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Commit(projections: rejected));
    }

    [Fact]
    public void Projection_section_count_and_aggregate_limits_are_inclusive()
    {
        JournalProjectionWrite[] acceptedCount = Enumerable.Range(0, 64).Select(i => JournalTestData.Projection(sectionName: $"s/{i}", data: Array.Empty<byte>())).ToArray();
        JournalTestData.Initialization(projections: acceptedCount).Validate();

        JournalProjectionWrite[] acceptedBytes = Enumerable.Range(0, 4).Select(i => JournalTestData.Projection(sectionName: $"b/{i}", data: new byte[2 * 1024 * 1024])).ToArray();
        JournalTestData.Initialization(projections: acceptedBytes).Validate();

        var rejectedBytes = new List<JournalProjectionWrite>(acceptedBytes)
        {
            JournalTestData.Projection(sectionName: "b/4", data: new byte[] { 1 }),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Initialization(projections: rejectedBytes));
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    public void Normalized_intent_limit_is_inclusive(int length)
    {
        if (length == JournalLimits.Maximum.NormalizedIntentBytes)
            _ = JournalTestData.Identity(new byte[length]);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Identity(new byte[length]));
    }

    [Theory]
    [InlineData(256 * 1024)]
    [InlineData(256 * 1024 + 1)]
    public void Event_payload_limit_is_inclusive(int length)
    {
        if (length == JournalLimits.Maximum.EventPayloadBytes)
            _ = JournalTestData.Event(payload: new byte[length]);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Event(payload: new byte[length]));
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    public void Result_limit_is_inclusive(int length)
    {
        if (length == JournalLimits.Maximum.ResultBytes)
            _ = JournalTestData.Commit(result: new byte[length]);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Commit(result: new byte[length]));
    }

    [Theory]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(2 * 1024 * 1024 + 1)]
    public void Projection_section_limit_is_inclusive(int length)
    {
        if (length == JournalLimits.Maximum.ProjectionSectionBytes)
            _ = JournalTestData.Projection(data: new byte[length]);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Projection(data: new byte[length]));
    }

    [Theory]
    [InlineData(8 * 1024 * 1024)]
    [InlineData(8 * 1024 * 1024 + 1)]
    public void Snapshot_limit_is_inclusive(int length)
    {
        if (length == JournalLimits.Maximum.SnapshotBytes)
            _ = JournalTestData.Initialization(snapshot: new byte[length]);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Initialization(snapshot: new byte[length]));
    }

    [Fact]
    public void Event_read_page_limits_are_inclusive()
    {
        _ = new JournalEventRead("player/1", 0, null, 2_048, 8 * 1024 * 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalEventRead("player/1", 0, null, 2_049, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalEventRead("player/1", 0, null, 1, 8 * 1024 * 1024 + 1));
    }

    [Fact]
    public void Aggregate_commit_limit_is_inclusive()
    {
        JournalProjectionWrite[] exact =
        {
            JournalTestData.Projection(sectionName: "p/0", data: new byte[2 * 1024 * 1024]),
            JournalTestData.Projection(sectionName: "p/1", data: new byte[2 * 1024 * 1024]),
            JournalTestData.Projection(sectionName: "p/2", data: new byte[2 * 1024 * 1024]),
            JournalTestData.Projection(sectionName: "p/3", data: new byte[(2 * 1024 * 1024) - 1]),
        };
        JournalTestData.Commit(projections: exact).Validate();

        JournalProjectionWrite[] tooLarge = exact.Concat(new[] { JournalTestData.Projection(sectionName: "p/4", data: new byte[] { 1 }) }).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTestData.Commit(projections: tooLarge));
    }

    [Fact]
    public void Identity_text_limits_are_inclusive_and_ascii_only()
    {
        _ = new JournalOperationIdentity(Guid.NewGuid(), new string('a', 128), new string('b', 128), Array.Empty<byte>());
        _ = new JournalStreamMutation(new string('s', 256), 0, Array.Empty<JournalEvent>());
        _ = new JournalEvent(new string('e', 128), 1, Array.Empty<byte>());
        _ = new JournalProjectionWrite("player/1", new string('n', 128), new string('c', 128), 1, Array.Empty<byte>());

        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalOperationIdentity(Guid.NewGuid(), new string('a', 129), "x", Array.Empty<byte>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamMutation(new string('s', 257), 0, Array.Empty<JournalEvent>()));
        Assert.Throws<ArgumentException>(() => new JournalOperationIdentity(Guid.NewGuid(), "wørld", "x", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new JournalEvent("bad value", 1, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new JournalEvent("bad\ud800", 1, Array.Empty<byte>()));
    }

    [Fact]
    public void Null_empty_and_invalid_identity_values_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new JournalOperationIdentity(Guid.Empty, "scope", "action", Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => new JournalOperationIdentity(Guid.NewGuid(), null!, "action", Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new JournalOperationIdentity(Guid.NewGuid(), "", "action", Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => new JournalOperationIdentity(Guid.NewGuid(), "scope", "action", null!));
        Assert.Throws<ArgumentNullException>(() => new JournalCommit(JournalTestData.Identity(), null!, Array.Empty<JournalProjectionWrite>(), "r", 1, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new JournalCommit(JournalTestData.Identity(), Array.Empty<JournalStreamMutation>(), Array.Empty<JournalProjectionWrite>(), "r", 1, Array.Empty<byte>()));
    }

    [Fact]
    public void Duplicate_stream_and_projection_keys_are_rejected()
    {
        JournalStreamMutation[] duplicateStreams = { JournalTestData.Mutation(), JournalTestData.Mutation() };
        Assert.Throws<ArgumentException>(() => JournalTestData.Commit(streams: duplicateStreams));

        JournalProjectionWrite[] duplicateSections = { JournalTestData.Projection(), JournalTestData.Projection() };
        Assert.Throws<ArgumentException>(() => JournalTestData.Commit(projections: duplicateSections));
    }

    [Fact]
    public void Caller_collection_order_is_canonicalized_but_event_order_is_preserved()
    {
        JournalEvent first = JournalTestData.Event("event/first");
        JournalEvent second = JournalTestData.Event("event/second");
        var commit = JournalTestData.Commit(
            streams: new[]
            {
                JournalTestData.Mutation("stream/z", events: new[] { first, second }),
                JournalTestData.Mutation("stream/a"),
            },
            projections: new[]
            {
                JournalTestData.Projection("stream/z", "z"),
                JournalTestData.Projection("stream/a", "a"),
            });

        Assert.Equal(new[] { "stream/a", "stream/z" }, commit.StreamMutations.Select(x => x.StreamKey));
        Assert.Equal(new[] { "stream/a:a", "stream/z:z" }, commit.ProjectionWrites.Select(x => $"{x.StreamKey}:{x.SectionName}"));
        Assert.Equal(new[] { "event/first", "event/second" }, commit.StreamMutations[1].Events.Select(x => x.EventType));
    }

    [Fact]
    public void Shared_test_hook_phase_vocabulary_covers_all_recovery_boundaries()
    {
        Assert.Equal(
            new[]
            {
                "BeforeTransaction",
                "AfterOperationResolution",
                "AfterHeadValidation",
                "AfterEventWrites",
                "AfterProjectionWrites",
                "BeforeCommit",
                "AfterCommitBeforeResponse",
                "SnapshotWrittenBeforeVerification",
                "SnapshotVerifiedBeforePrune",
            },
            Enum.GetNames<JournalTestHookPhase>());
    }
}
