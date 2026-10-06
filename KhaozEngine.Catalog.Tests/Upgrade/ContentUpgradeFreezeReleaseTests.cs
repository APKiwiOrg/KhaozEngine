using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The freeze the publish window is closed with, and the one thing that must hold however the attempt ends:
/// a marker THIS attempt set never outlives the attempt unless the store's own publish took it over.
/// <para>
/// <b>A marker left standing is not a slow run, it is a wedged catalog.</b> It names the ACTIVE version, so
/// the stale-marker sweep every publish starts with will never clear it, and while it stands the store
/// refuses both <c>ApplyEditsAsync</c> and <c>DiscardDraftAsync</c>. An operator is then told to publish or
/// discard a draft they can do neither to, and no publisher is coming to recover it.
/// </para>
/// <para>
/// The three ways out of the frozen region that are NOT a failed re-proof are the ones covered here, each
/// raised at the exit of the guarded freeze after its marker committed, which is where its snapshot boundary
/// is: a cancelled token, a handled fault, and a fault of a type this package does not answer with a report
/// at all. Each one releases the base the run recorded, on <see cref="CancellationToken.None"/>. Each runs
/// against the reference store and against a real SQLite file, because the marker is a field on one and a
/// column on the other.
/// </para>
/// </summary>
public sealed class ContentUpgradeFreezeReleaseTests
{
    /// <summary>The deadline a run is given, well under the stand-off budget, so a stall fails fast.</summary>
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>An edit no definition plans, which is what an operator's own write into the draft looks like.</summary>
    static ContentEdit OperatorEdit
        => ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("operator_row"), PublishFixtures.Fields(77));

    /// <summary>
    /// (a) The token is cancelled at the exit of the guarded freeze, after its marker committed and before its
    /// snapshot reached the run. The cancellation still propagates, because a host that cancelled its own boot
    /// is not waiting for a report, and the draft is left UNFROZEN, so the operator it is handed back to can
    /// edit it and discard it.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a real SQLite file rather than the reference store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationAtTheFreezeExitStillLeavesTheDraftTheOperatorCanUse(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await OlderCatalogAsync(files, registry, sqlite);
        using var cancel = new CancellationTokenSource(Deadline);
        var store = new CancelsAtTheFreezeExitStore(lease.Store, cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ContentUpgradeRunner.RunAsync(
                store, registry, SetFor(registry), UpgradeFixtures.Apply(), cancel.Token));

        Assert.True(store.Cancelled, "the freeze exit never ran, so the interleaving was not exercised.");
        AssertReleasedOnlyBaseOne(store, released: true);
        ContentDraft? draft = await lease.Store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        Assert.False(draft.IsFrozen, "the run left a marker standing that nothing will ever clear.");

        // What UNFROZEN means to the operator the draft was handed back to, rather than only to a field.
        await lease.Store.ApplyEditsAsync(
            [OperatorEdit], "an-operator", "oid:human", "an operator's own afternoon");
        Assert.Equal(2, (await lease.Store.GetOpenDraftAsync())!.EditCount);
        await lease.Store.DiscardDraftAsync("an-operator", "oid:human");
        Assert.Null(await lease.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// (b) The freeze COMMITS its marker and then reports a failure, which is a dropped connection on the
    /// acknowledgement rather than on the write. The marker is durable and the call said it failed, so the
    /// attempt owes a release and the failure path finds an unfrozen draft.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a real SQLite file rather than the reference store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFreezeThatCommittedItsMarkerAndThenFailedIsStillReleased(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await OlderCatalogAsync(files, registry, sqlite);
        var store = new FreezeCommitsThenFailsStore(lease.Store);

        ContentUpgradeReport report = await RunAsync(store, registry);

        Assert.True(store.Faulted, "the freeze never faulted, so the interleaving was not exercised.");
        Assert.Equal(1, store.ReleaseBases[0]);
        Assert.True(store.ReleaseResults[0], "the release after the lost answer cleared nothing.");
        Assert.All(store.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, store.LegacyFreezeClears);
        Assert.Equal(0, store.LegacyFreezes);
        Assert.NotNull(store.DraftAfterTheFault);
        Assert.False(
            store.DraftAfterTheFault.IsFrozen,
            "the failure path met a marker this attempt set and did not release.");

        // The run itself is unharmed: a freeze that failed is contention, so it is waited out and retried.
        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Null(await lease.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// (c) A fault of a type this package does not answer with a report, raised at the exit of the guarded
    /// freeze after its marker committed. It propagates, because an unknown fault is a defect worth a stack
    /// trace, and the marker is still released on the way out.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a real SQLite file rather than the reference store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnhandledFaultAtTheFreezeExitPropagatesAndLeavesTheDraftUnfrozen(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await OlderCatalogAsync(files, registry, sqlite);
        var store = new TheFreezeExitFaultsStore(lease.Store);

        InvalidOperationException escaped = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunAsync(store, registry));

        Assert.Contains("frozen draft", escaped.Message, StringComparison.Ordinal);
        AssertReleasedOnlyBaseOne(store, released: true);
        ContentDraft? draft = await lease.Store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        Assert.False(draft.IsFrozen, "the run left a marker standing that nothing will ever clear.");
    }

    /// <summary>
    /// (d) The ordinary successful run is unchanged. It clears no marker of its own at all, because the
    /// store's publish takes the marker over at its own first step and releases it on its own exit paths,
    /// and nothing is left frozen behind it.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is a real SQLite file rather than the reference store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASuccessfulPublishReleasesNothingItselfAndLeavesNoMarkerStanding(bool sqlite)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using ContentAuthoringStoreLease lease = await OlderCatalogAsync(files, registry, sqlite);
        var store = new CountsFreezeReleasesStore(lease.Store);

        ContentUpgradeReport report = await RunAsync(store, registry);

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(2, store.Publishes);
        Assert.Equal(2, store.GuardedFreezes);
        Assert.Empty(store.ReleaseBases);
        Assert.Equal(0, store.LegacyFreezeClears);
        Assert.Equal(0, store.LegacyFreezes);
        Assert.Null(await lease.Store.GetOpenDraftAsync());
        Assert.Equal(3, (await lease.Store.ListVersionsAsync()).Count);
    }

    /// <summary>The one release a freeze exit owes: base 1, on <see cref="CancellationToken.None"/>.</summary>
    /// <param name="store">The double that observed the release.</param>
    /// <param name="released">Whether that release cleared the marker.</param>
    static void AssertReleasedOnlyBaseOne(ForwardingUpgradeStore store, bool released)
    {
        Assert.Equal(new[] { 1 }, store.ReleaseBases);
        Assert.Equal(new[] { released }, store.ReleaseResults);
        Assert.All(store.ReleaseTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.Equal(0, store.LegacyFreezeClears);
        Assert.Equal(0, store.LegacyFreezes);
    }

    /// <summary>One apply run under a hard deadline, so a stall fails fast instead of spending the budget.</summary>
    /// <param name="store">The store under test.</param>
    /// <param name="registry">This build's registry.</param>
    static async Task<ContentUpgradeReport> RunAsync(IContentAuthoringStore store, ContentTypeRegistry registry)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        return await ContentUpgradeRunner.RunAsync(
            store, registry, SetFor(registry), UpgradeFixtures.Apply(), deadline.Token);
    }

    /// <summary>An OLDER catalog at version 1, in memory or on a real SQLite file.</summary>
    /// <param name="files">The temporary database and pack root.</param>
    /// <param name="registry">This build's registry.</param>
    /// <param name="sqlite">Whether the catalog is a real SQLite file.</param>
    static async Task<ContentAuthoringStoreLease> OlderCatalogAsync(
        TemporaryCatalogDatabase files,
        ContentTypeRegistry registry,
        bool sqlite)
    {
        IContentAuthoringStore store = sqlite
            ? new SqliteContentAuthoringStore(files.ConnectionString, registry, files.Pack())
            : new InMemoryContentAuthoringStore(registry, files.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            "seed the older catalog");
        await store.PublishAsync(PublishFixtures.Request(0));
        return new ContentAuthoringStoreLease(store);
    }

    /// <summary>The two definitions this build ships, over a registry, with their committed target bundle.</summary>
    /// <param name="registry">The registry the bundle declares its types from.</param>
    static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        return new ContentUpgradeSet(
            UpgradeFixtures.Adds(
                UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row")),
            UpgradeFixtures.Adds(
                UpgradeHarness.SecondId,
                2,
                target,
                UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));
    }
}

/// <summary>
/// A forwarding double over ANY store that also keeps the upgrade ledger, which is what these cases need:
/// the same interleaving has to run against the reference store and against a real SQLite file, so the inner
/// store cannot be typed as either one. It observes every guarded release with the base, token and answer the
/// call actually carried, and counts any unconditional clear and any unguarded freeze reaching it, neither of which
/// a run may make.
/// </summary>
/// <param name="inner">The store behind the double.</param>
internal abstract class ForwardingUpgradeStore(IContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    readonly List<int> _releaseBases = [];
    readonly List<bool> _releaseResults = [];
    readonly List<CancellationToken> _releaseTokens = [];

    /// <summary>The base each guarded release named.</summary>
    public IReadOnlyList<int> ReleaseBases => _releaseBases;

    /// <summary>What each guarded release answered.</summary>
    public IReadOnlyList<bool> ReleaseResults => _releaseResults;

    /// <summary>The token each guarded release was given.</summary>
    public IReadOnlyList<CancellationToken> ReleaseTokens => _releaseTokens;

    /// <summary>How many unconditional clears reached the store.</summary>
    public int LegacyFreezeClears { get; private set; }

    /// <summary>How many unguarded freezes reached the store.</summary>
    public int LegacyFreezes { get; private set; }

    /// <inheritdoc />
    public override async Task<bool> ReleaseDraftFreezeForBaseAsync(
        int frozenForBaseVersion,
        CancellationToken cancellationToken = default)
    {
        _releaseBases.Add(frozenForBaseVersion);
        _releaseTokens.Add(cancellationToken);
        bool released = await base.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);
        _releaseResults.Add(released);
        return released;
    }

    /// <inheritdoc />
    public override Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        LegacyFreezeClears++;
        return base.ClearDraftFreezeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        LegacyFreezes++;
        return base.FreezeDraftAsync(baseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => Ledger.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => Ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);

    /// <summary>The ledger half of the inner store, which every catalog at schema version 2 keeps.</summary>
    IContentUpgradeLedger Ledger => (IContentUpgradeLedger)Inner;
}

/// <summary>
/// A store whose guarded freeze commits its marker and whose exit then observes a CANCELLED token, which is the
/// host shutting down in the one moment the draft is frozen and nothing has been published.
/// </summary>
/// <param name="inner">The store behind the double.</param>
/// <param name="cancel">The run's own token source, cancelled at the freeze's exit.</param>
internal sealed class CancelsAtTheFreezeExitStore(
    IContentAuthoringStore inner,
    CancellationTokenSource cancel)
    : ForwardingUpgradeStore(inner)
{
    /// <summary>Whether the freeze exit really observed the cancellation.</summary>
    public bool Cancelled { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        ContentDraft frozen = await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
        Cancelled = true;
        await cancel.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return frozen;
    }
}

/// <summary>
/// A store whose FIRST guarded freeze commits its marker and then reports a failure, which is what a dropped
/// connection on the acknowledgement of a committed write looks like from the caller's side. It disarms
/// itself, so the retry the contention refusal buys gets through.
/// </summary>
/// <param name="inner">The store behind the double.</param>
internal sealed class FreezeCommitsThenFailsStore(IContentAuthoringStore inner) : ForwardingUpgradeStore(inner)
{
    bool _armed = true;
    bool _pending;

    /// <summary>Whether the freeze really faulted.</summary>
    public bool Faulted { get; private set; }

    /// <summary>The first draft read AFTER the fault, which is what the failure path met.</summary>
    public ContentDraft? DraftAfterTheFault { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        ContentDraft frozen = await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
        if (!_armed)
        {
            return frozen;
        }

        _armed = false;
        Faulted = true;
        _pending = true;
        throw new ContentAuthoringException(
            "the catalog dropped the connection after the freeze committed.",
            default,
            0,
            ContentAuthoringException.PublishInProgressReason);
    }

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        ContentDraft? draft = await base.GetOpenDraftAsync(cancellationToken);
        if (_pending)
        {
            _pending = false;
            DraftAfterTheFault = draft;
        }

        return draft;
    }
}

/// <summary>
/// A store whose guarded freeze commits its marker and then raises, at its exit, a fault this package does NOT
/// answer with a report, which is how a defect rather than a catalog refusal reaches the frozen region.
/// </summary>
/// <param name="inner">The store behind the double.</param>
internal sealed class TheFreezeExitFaultsStore(IContentAuthoringStore inner) : ForwardingUpgradeStore(inner)
{
    /// <inheritdoc />
    public override async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
        throw new InvalidOperationException("the seam faulted while returning the frozen draft.");
    }
}

/// <summary>
/// Counts the guarded freezes, the releases and the publishes the RUNNER itself made. A store's own publish
/// pipeline runs against the inner store, so the marker it sets and releases for itself never reaches this
/// decorator, which is exactly the separation the assertion needs.
/// </summary>
/// <param name="inner">The store behind the double.</param>
internal sealed class CountsFreezeReleasesStore(IContentAuthoringStore inner) : ForwardingUpgradeStore(inner)
{
    /// <summary>How many guarded freezes the runner made.</summary>
    public int GuardedFreezes { get; private set; }

    /// <summary>How many publishes the runner started.</summary>
    public int Publishes { get; private set; }

    /// <inheritdoc />
    public override Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        GuardedFreezes++;
        return base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        Publishes++;
        return base.PublishAsync(request, cancellationToken);
    }
}
