using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.ConditionalFreeze;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Publishing requires the guarded freeze, plans the draft that guarded freeze returned, and releases only the
/// base its own request recorded. A store without the companion is refused before the pipeline calls it at all,
/// and every publish exit hands back exactly the marker it could have set, never a newer publisher's.
/// <para>
/// The lifecycle cases run on the in-memory store and on SQLite. Their doubles forward the real store operation
/// first and only then lose its acknowledgement or fault, so the store state they leave is real.
/// </para>
/// </summary>
public sealed class ConditionalFreezeCapabilityPublishTests
{
    /// <summary>
    /// A publish over a store that declares only the released seam is refused with the stable reason before it
    /// reaches a single store member or pack write, and the catalog is untouched. The commit itself still
    /// constructs, sweeps and writes, because only publishing needs the companion.
    /// </summary>
    [Fact]
    public async Task IncapablePublish_RefusesBeforeAnyStoreCall()
    {
        await using var fixture = new FreezeRaceFixture(sqlite: false);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        var view = new LegacyFreezeStoreView(inner);
        var packs = new FreezeRacePackStore(Assert.IsType<FileSystemPackStore>(fixture.Packs));
        var ids = (IContentIdPersistence)inner;
        var commit = new ContentPublishCommit(view, packs, new ContentPublisher(view, ids, fixture.Registry));
        var before = await UpgradeFixtures.FootprintAsync(inner);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => commit.PublishAsync(PublishFixtures.Request(1)));

        Assert.Equal("conditional-freeze-unavailable", refused.Reason);
        Assert.Empty(view.Calls);
        Assert.Empty(packs.Writes);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
        Assert.False((await inner.GetOpenDraftAsync())!.IsFrozen);

        await commit.SweepAsync();
        Assert.Equal(new[] { nameof(IContentAuthoringStore.ListVersionsAsync) }, view.Calls);
        ContentPublishPlan plan = await new ContentPublisher(inner, ids, fixture.Registry)
            .PrepareAsync(PublishFixtures.Request(1), await inner.ReadPublishBaselineAsync());
        ContentPackWrite wrote = await commit.WriteAsync(plan);
        Assert.True(wrote.ObjectsWritten > 0);
    }

    /// <summary>
    /// A standalone Prepare over a store without the companion is refused before its draft read, so it neither
    /// reads nor freezes. Every decorator that declares the companion refuses such an inner store when it is
    /// built, not at its first freeze.
    /// </summary>
    [Fact]
    public async Task IncapablePrepare_RefusesBeforeReadingDraft()
    {
        await using var fixture = new FreezeRaceFixture(sqlite: false);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        var view = new LegacyFreezeStoreView(inner);
        var publisher = new ContentPublisher(view, (IContentIdPersistence)inner, fixture.Registry);
        ContentPublishBaseline baselineFromInner = await inner.ReadPublishBaselineAsync();
        var before = await UpgradeFixtures.FootprintAsync(inner);

        var prepareRefused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => publisher.PrepareAsync(PublishFixtures.Request(1), baselineFromInner));

        Assert.Equal("conditional-freeze-unavailable", prepareRefused.Reason);
        Assert.Empty(view.Calls);
        Assert.Equal(before, await UpgradeFixtures.FootprintAsync(inner));
        Assert.False((await inner.GetOpenDraftAsync())!.IsFrozen);
        Assert.Throws<ArgumentException>(() => new ConditionalRowOnlyStoreView(view));
        Assert.Throws<ArgumentException>(() => new LedgerlessStore(view));
        Assert.Empty(view.Calls);
    }

    /// <summary>
    /// Value 22 is staged, then value 33 lands in the gap a separate read and freeze would leave: at the exit of a
    /// draft read, or at the entry of the guarded freeze. Prepare must plan the draft the atomic freeze returned,
    /// so the candidate carries 33, and it reaches the store through that one call alone.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowPrepare_IncludesAnEditBeforeAtomicFreeze(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        var safeView = new GapEditView(inner, () => fixture.EditAsync(fixture.OldRow(33)));
        var publisher = new ContentPublisher(safeView, (IContentIdPersistence)inner, fixture.Registry);

        ContentPublishPlan plan = await publisher.PrepareAsync(
            PublishFixtures.Request(1), await inner.ReadPublishBaselineAsync());

        Assert.Equal(33, plan.Candidate.Rows(UpgradeFixtures.Thing)[0].Fields[0].Number);
        Assert.Equal(1, safeView.GuardedFreezeCalls);
        Assert.Equal(0, safeView.LegacyFreezeCalls);
        Assert.Equal(0, safeView.DraftReadCalls);
        Assert.Equal(1, (await inner.GetOpenDraftAsync())!.FrozenForBaseVersion);
        Assert.True(await safeView.ReleaseDraftFreezeForBaseAsync(1));
        Assert.False(await safeView.ReleaseDraftFreezeForBaseAsync(1));
        Assert.False((await inner.GetOpenDraftAsync())!.IsFrozen);
    }

    /// <summary>
    /// The guarded freeze writes its marker and its acknowledgement is lost. The publish fails with that loss,
    /// releases the base its request recorded with a token nothing can cancel, and leaves version 1 and the
    /// staged edit exactly as they were.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_LostFreezeAcknowledgementReleasesRecordedBase(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        var lost = new IOException("the guarded freeze's acknowledgement was lost");
        var older = new FaultingRowParticipant(inner, (IContentIdPersistence)inner, fixture.Registry, fixture.Packs)
        {
            FreezeAcknowledgementLoss = lost,
        };

        Exception observed = await Assert.ThrowsAnyAsync<Exception>(() => older.PublishAsync(PublishFixtures.Request(1)));
        ContentDraft held = Assert.IsType<ContentDraft>(await inner.GetOpenDraftAsync());

        Assert.Same(lost, observed);
        Assert.Equal(new int?[] { 1 }, older.ReleaseBases);
        Assert.Equal(new bool?[] { true }, older.ReleaseResults);
        Assert.All(older.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.All(older.ReleaseTokens, token => Assert.False(token.CanBeCanceled));
        Assert.Null(held.FrozenForBaseVersion);
        Assert.Equal(1, older.GuardedFreezeCalls);
        Assert.Equal(0, older.LegacyFreezeCalls);
        Assert.Equal(0, older.LegacyClearCalls);
        Assert.Equal(1, await inner.GetActiveVersionAsync());
        Assert.Single(await inner.ListVersionsAsync());
        Assert.Equal(22, Assert.Single(held.Changes.Edits).Fields[0].Value.Number);
    }

    /// <summary>
    /// A text publish cancelled inside its pack write keeps its cancellation and releases its recorded base with
    /// <see cref="CancellationToken.None"/>, which clears the marker it set and nothing else.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_CancellationReleasesWithNone(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        FreezeRaceStore older = await fixture.TextParticipantAsync();
        FreezeRacePackStore pack = Assert.IsType<FreezeRacePackStore>(older.Packs);
        OnceGate put = fixture.Gate();
        pack.ArmPut(put);

        using var cancel = new CancellationTokenSource();
        Task<ContentPublishResult> publish = fixture.Start(
            () => older.PublishAsync(PublishFixtures.Request(1), cancel.Token));
        await FreezeRaceFixture.ReachAsync(put, publish, "the pack write at base 1");
        await cancel.CancelAsync();
        (_, Exception? failure) = await FreezeRaceFixture.OutcomeAsync(publish);
        ContentDraft held = Assert.IsType<ContentDraft>(await inner.GetOpenDraftAsync());

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, pack.CancelledPuts);
        Assert.Equal(new int?[] { 1 }, older.ReleaseBases);
        Assert.Equal(new bool?[] { true }, older.ReleaseResults);
        Assert.All(older.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, older.LegacyFreezeCalls);
        Assert.Equal(0, older.LegacyClearCalls);
        Assert.Null(held.FrozenForBaseVersion);
        Assert.Equal(1, await inner.GetActiveVersionAsync());
        Assert.Single(await inner.ListVersionsAsync());
        Assert.Equal(22, Assert.Single(held.Changes.Edits).Fields[0].Value.Number);
    }

    /// <summary>
    /// A row publish ends with a pack-write fault or an invalid candidate, and its release then faults too. The
    /// caller still reads the publish's own failure, and the release named only the recorded base.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    /// <param name="invalidCandidate">Whether the publish ends on an invalid candidate rather than a pack-write fault.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Publish_ReleaseFailurePreservesOriginalFailure(bool sqlite, bool invalidCandidate)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        var originalPublishFailure = new IOException("the pack write faulted");
        var releaseFault = new IOException("the release faulted");
        if (invalidCandidate)
        {
            // Two rows under one key, which the validator refuses, so the plan never reaches the pack.
            await fixture.EditAsync(
                ContentEdit.Import(UpgradeFixtures.Thing, 50, new ContentKey("same"), PublishFixtures.Fields(1)),
                ContentEdit.Import(UpgradeFixtures.Thing, 51, new ContentKey("same"), PublishFixtures.Fields(2)));
        }
        else
        {
            await fixture.EditAsync(fixture.OldRow(22));
        }

        IPackStore packs = invalidCandidate
            ? fixture.Packs
            : new FaultingPackStore(Assert.IsType<FileSystemPackStore>(fixture.Packs), originalPublishFailure);
        var older = new FaultingRowParticipant(inner, (IContentIdPersistence)inner, fixture.Registry, packs)
        {
            ReleaseFault = releaseFault,
        };

        Exception observedPublishFailure = await Assert.ThrowsAnyAsync<Exception>(
            () => older.PublishAsync(PublishFixtures.Request(1)));

        Assert.Equal(new int?[] { 1 }, older.ReleaseBases);
        Assert.Equal(new bool?[] { true }, older.ReleaseResults);
        Assert.All(older.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.NotSame(releaseFault, observedPublishFailure);
        if (invalidCandidate)
        {
            Assert.Equal(
                ContentAuthoringException.CandidateInvalidReason,
                Assert.IsType<ContentAuthoringException>(observedPublishFailure).Reason);
        }
        else
        {
            Assert.Same(originalPublishFailure, observedPublishFailure);
        }

        Assert.Equal(0, older.LegacyFreezeCalls);
        Assert.Equal(0, older.LegacyClearCalls);
        Assert.Equal(1, await inner.GetActiveVersionAsync());
        Assert.False((await inner.GetOpenDraftAsync())!.IsFrozen);
    }

    /// <summary>
    /// The text commit lands version 2 and its acknowledgement is lost after a newer row draft was staged and
    /// frozen at 2. The older publish's cleanup still names base 1, so it releases nothing, and the newer marker
    /// and the active version survive it.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a SQLite file rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_LostCommitAcknowledgementPreservesNewerMarker(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));
        var lost = new IOException("the text commit's acknowledgement was lost");
        var older = new CommitAcknowledgementLostStore(
            inner,
            (IContentIdPersistence)inner,
            fixture.Registry,
            fixture.Packs,
            async () =>
            {
                await fixture.EditAsync(fixture.OldRow(33));
                await ((IContentConditionalDraftFreeze)inner).FreezeDraftForBaseAsync(2);
            },
            lost);

        Exception observed = await Assert.ThrowsAnyAsync<Exception>(() => older.PublishAsync(PublishFixtures.Request(1)));
        ContentDraft newer = Assert.IsType<ContentDraft>(await inner.GetOpenDraftAsync());

        Assert.Same(lost, observed);
        Assert.Equal(1, older.TextCommitCalls);
        Assert.Equal(new int?[] { 1 }, older.ReleaseBases);
        Assert.Equal(new bool?[] { false }, older.ReleaseResults);
        Assert.All(older.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, older.LegacyFreezeCalls);
        Assert.Equal(0, older.LegacyClearCalls);
        Assert.Equal(2, await inner.GetActiveVersionAsync());
        Assert.Equal(2, newer.FrozenForBaseVersion);
        Assert.Equal(33, Assert.Single(newer.Changes.Edits).Fields[0].Value.Number);
    }

    /// <summary>
    /// A capable row-only view that lands one edit in the gap between a draft read and a freeze: at the exit of a
    /// draft read, or at the entry of the guarded freeze, whichever a Prepare reaches first.
    /// </summary>
    sealed class GapEditView(IContentAuthoringStore inner, Func<Task> landGapEdit) : ConditionalRowOnlyStoreView(inner)
    {
        Func<Task>? _gap = landGapEdit;

        public int DraftReadCalls { get; private set; }

        public int LegacyFreezeCalls { get; private set; }

        public int GuardedFreezeCalls { get; private set; }

        public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        {
            DraftReadCalls++;
            ContentDraft? draft = await base.GetOpenDraftAsync(cancellationToken);
            await LandGapEditAsync();
            return draft;
        }

        public override Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        {
            LegacyFreezeCalls++;
            return base.FreezeDraftAsync(baseVersion, cancellationToken);
        }

        public override async Task<ContentDraft> FreezeDraftForBaseAsync(
            int expectedBaseVersion,
            CancellationToken cancellationToken = default)
        {
            GuardedFreezeCalls++;
            await LandGapEditAsync();
            return await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
        }

        async Task LandGapEditAsync()
        {
            if (_gap is Func<Task> gap)
            {
                _gap = null;
                await gap();
            }
        }
    }

    /// <summary>
    /// A row-only participant that publishes over itself and can lose its guarded freeze's acknowledgement once,
    /// after the real freeze ran, or fault every release after the real release ran.
    /// </summary>
    sealed class FaultingRowParticipant(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs)
        : FreezeRaceParticipant(inner, ids, registry, packs)
    {
        public Exception? FreezeAcknowledgementLoss { get; set; }

        public Exception? ReleaseFault { get; set; }

        public override Task<ContentPublishResult> PublishAsync(
            ContentPublishRequest request,
            CancellationToken cancellationToken = default)
            => PublishOverSelfAsync(request, cancellationToken);

        public override async Task<ContentDraft> FreezeDraftForBaseAsync(
            int expectedBaseVersion,
            CancellationToken cancellationToken = default)
        {
            ContentDraft frozen = await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
            if (FreezeAcknowledgementLoss is Exception lost)
            {
                FreezeAcknowledgementLoss = null;
                throw lost;
            }

            return frozen;
        }

        public override async Task<bool> ReleaseDraftFreezeForBaseAsync(
            int frozenForBaseVersion,
            CancellationToken cancellationToken = default)
        {
            bool released = await base.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);
            return ReleaseFault is Exception fault ? throw fault : released;
        }
    }

    /// <summary>
    /// A text participant whose complete text commit really commits, then runs the newer publisher's work, then
    /// loses its acknowledgement.
    /// </summary>
    sealed class CommitAcknowledgementLostStore(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs,
        Func<Task> afterCommit,
        Exception lost)
        : FreezeRaceStore(inner, ids, registry, packs)
    {
        public override async Task<ContentVersionRecord> CommitTextPublishAsync(
            ContentTextPublishPlan plan,
            ContentPublishRequest request,
            IPackVersionPointerStore? pointers,
            CancellationToken cancellationToken = default)
        {
            await base.CommitTextPublishAsync(plan, request, pointers, cancellationToken);
            await afterCommit();
            throw lost;
        }
    }

    /// <summary>The shared pack root with every object write faulting. Reads and version pointers forward.</summary>
    sealed class FaultingPackStore(FileSystemPackStore inner, Exception fault) : IPackStore, IPackVersionPointerStore
    {
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(hash, cancellationToken);

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => inner.GetAsync(hash, cancellationToken);

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => Task.FromException(fault);

        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => inner.ListAsync(versionNumber, cancellationToken);

        public Task PutVersionPointerAsync(
            int versionNumber,
            string serverManifestHash,
            string clientManifestHash,
            CancellationToken cancellationToken = default)
            => inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken);

        public Task<PackVersionPointer?> GetVersionPointerAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
            => inner.GetVersionPointerAsync(versionNumber, cancellationToken);
    }
}
