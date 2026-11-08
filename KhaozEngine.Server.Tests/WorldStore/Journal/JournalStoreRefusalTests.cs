using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalTask6TestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalStoreRefusalTests
{
    [Theory]
    [InlineData("", 0, null, JournalStoreRefusal.StoredStreamKeyInvalid, "streamKey")]
    [InlineData("bad key", 0, null, JournalStoreRefusal.StoredStreamKeyInvalid, "streamKey")]
    [InlineData("stream/b", -1, null, JournalStoreRefusal.StoredHeadVersionNegative, "headVersion")]
    [InlineData("stream/b", 0, "created", JournalStoreRefusal.StoredTimestampNonUtc, "createdAtUtc")]
    [InlineData("stream/b", 0, "updated", JournalStoreRefusal.StoredTimestampNonUtc, "updatedAtUtc")]
    [InlineData("stream/a", 0, null, JournalStoreRefusal.StoredStreamOrderInvalid, null)]
    [InlineData("stream/0", 0, null, JournalStoreRefusal.StoredStreamOrderInvalid, null)]
    public void Stored_row_faults_keep_their_rule_and_row_index(
        string key, long version, string? timeField, JournalStoreRefusal reason, string? parameter)
    {
        DateTimeOffset nonUtc = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1));
        JournalStoredStreamRow[] rows =
        [
            new("stream/a", 0, null, null),
            new(key, version, timeField == "created" ? nonUtc : null, timeField == "updated" ? nonUtc : null),
        ];
        var query = new JournalStreamQuery(2);
        var refused = Assert.Throws<JournalStoreException>(() => JournalStreamPage.FromStoredRows(rows, query));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(1, refused.Index);
        Assert.Equal(JournalStoreFailureKind.CorruptData, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Empty(refused.StreamKeys);
        if (reason == JournalStoreRefusal.StoredStreamKeyInvalid) Assert.Equal(parameter, refused.ParameterName);
        if (reason == JournalStoreRefusal.StoredHeadVersionNegative) Assert.Equal(-1, refused.ActualVersion);
        if (reason == JournalStoreRefusal.StoredTimestampNonUtc) Assert.Equal(parameter, refused.FieldName);
        rows[1] = new("stream/b", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Assert.Equal(2, JournalStreamPage.FromStoredRows(rows, query).Streams.Count);
    }

    [Theory]
    [InlineData(false, JournalStoreRefusal.EventChecksumMismatch)]
    [InlineData(true, JournalStoreRefusal.ProjectionChecksumMismatch)]
    public async Task Stored_payload_corruption_names_the_event_or_section(bool projection, JournalStoreRefusal reason)
    {
        var store = new InMemoryMutationJournalStore();
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 2)));
        await store.CommitAsync(Commit(2, 0, new byte[] { 3 }, 4));
        void ToggleFault()
        {
            if (projection) store.CorruptStoredProjectionChecksumForTests(StreamKey, "bag");
            else store.CorruptStoredEventChecksumForTests(StreamKey, 1);
        }
        Task Read() => projection
            ? store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))
            : ReadAllAsync(store);
        ToggleFault();
        JournalStoreException refused = await AssertCorruptAsync(Read);
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        if (projection) Assert.Equal("bag", refused.SectionName);
        else Assert.Equal(1, refused.StreamVersion);
        ToggleFault();
        await Read();
    }

    [Theory]
    [InlineData(false, JournalStoreRefusal.ProjectionByteCountExceeded)]
    [InlineData(true, JournalStoreRefusal.ProjectionSectionCountExceeded)]
    public async Task Projection_replacement_limits_refuse_without_committing_any_content(
        bool countLimit, JournalStoreRefusal reason)
    {
        var limits = countLimit
            ? new JournalLimits(projectionSectionsPerStream: 2)
            : new JournalLimits(aggregateProjectionBytesPerStream: 2);
        var store = new InMemoryMutationJournalStore(limits);
        await store.InitializeAsync(Initialization(1, 7, Projection("bag", 1), Projection("skills", 2)));
        JournalSnapshot snapshot = (await store.LoadSnapshotAsync(StreamKey))!;
        JournalProjectionRead before = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        JournalCommit rejected = Commit(3, 0, new byte[] { 3 }, 3, Projection("extra", 3));
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => store.CommitAsync(rejected));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(StreamKey, refused.StreamKey);
        Assert.Equal(JournalStoreFailureKind.ConstraintViolation, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, refused.Scope);
        Assert.Equal(StreamKey, Assert.Single(refused.StreamKeys));
        if (countLimit)
        {
            Assert.Equal(3, refused.ActualCount);
            Assert.Equal(2, refused.MaximumCount);
        }
        else
        {
            Assert.Equal(3, refused.ActualBytes);
            Assert.Equal(2, refused.MaximumBytes);
        }
        JournalProjectionRead after = await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey));
        Assert.Equal(before.HeadVersion, after.HeadVersion);
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Equal(before.Sections.Select(x => (x.SectionName, x.SourceVersion, Convert.ToHexString(x.Data.Span))),
            after.Sections.Select(x => (x.SectionName, x.SourceVersion, Convert.ToHexString(x.Data.Span))));
        Assert.Equal(snapshot.Data.ToArray(), (await store.LoadSnapshotAsync(StreamKey))!.Data.ToArray());
        Assert.Empty((await ReadAllAsync(store)).Events);
        Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(rejected.Identity)).Status);
        var belowLimit = new InMemoryMutationJournalStore(limits);
        await belowLimit.InitializeAsync(Initialization(10));
        Assert.Equal(JournalCommitStatus.Applied,
            (await belowLimit.CommitAsync(Commit(11, 0, new byte[] { 11 }, 11, Projection("bag", 8)))).Status);
        // Replacing one section keeps the real aggregate at the reported inclusive limit.
        Assert.Equal(JournalCommitStatus.Applied,
            (await store.CommitAsync(Commit(3, 0, new byte[] { 3 }, 3, Projection("bag", 9)))).Status);
        Assert.Equal(new byte[] { 9 }, (await store.ReadProjectionsAsync(new JournalProjectionQuery(StreamKey))).Sections[0].Data.ToArray());
    }

    [Fact]
    public void Legacy_constructor_keeps_its_dimensions_and_leaves_the_new_reason_null()
    {
        var inner = new Exception();
        var refused = new JournalStoreException(JournalStoreFailureKind.Unavailable,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.WholeStore, null, "refused", inner);
        Assert.Null(refused.Reason);
        Assert.Same(inner, refused.InnerException);
        Assert.Equal(JournalStoreFailureKind.Unavailable, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Empty(refused.StreamKeys);
    }
}
