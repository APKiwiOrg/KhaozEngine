using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// One race participant over a provider store: the whole text companion, the upgrade ledger and the guarded freeze
/// forwarded, its freeze and release lifecycle counted and observed, and the one-shot gates a conformance fact arms
/// on it. A publish through it is built over itself, so its freeze, its commit and its release all come back
/// through the members here, and a store whose commit must check the marker really reaches
/// <see cref="CommitTextPublishAsync"/>.
/// <para>
/// <b>Every pause is outside the provider's own scope.</b> A gate is awaited before a call starts or after it has
/// returned, so a SQL Server call has already opened and disposed its own per-call scope and holds no lease, lock or
/// transaction while it is parked.
/// </para>
/// <para>
/// It is here rather than shared with <c>KhaozEngine.Catalog.Tests</c> because the reference graph is how push CI
/// selects test projects, and a reference across that seam would pull the SQL Server suites into every catalog
/// change.
/// </para>
/// </summary>
internal sealed class ConditionalFreezeRaceStore : ForwardingContentAuthoringStore, IContentTextAuthoringStore, IContentUpgradeLedger
{
    readonly IContentTextAuthoringStore _text;
    readonly IContentUpgradeLedger _ledger;
    readonly IContentIdPersistence _ids;
    readonly ContentTypeRegistry _registry;
    readonly IPackStore _packs;
    readonly List<int> _releaseBases = [];
    readonly List<bool> _releaseResults = [];
    readonly List<CancellationToken> _releaseTokens = [];

    (OnceGate Gate, int Base)? _freezeEntry;
    (OnceGate Gate, int Base, string? Upgrade)? _commitEntry;
    OnceGate? _firstReleaseExit;
    OnceGate? _recoveryRead;
    bool _recoveryLive;

    /// <summary>Wraps one participant's provider store.</summary>
    /// <param name="inner">The store, which must declare the text companion, the ledger and the guarded freeze.</param>
    /// <param name="ids">The id persistence matching <paramref name="inner"/>.</param>
    /// <param name="registry">The catalog's registry.</param>
    /// <param name="packs">The pack target a publish built over this participant writes to.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> lacks a required companion.</exception>
    public ConditionalFreezeRaceStore(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(packs);

        _text = inner as IContentTextAuthoringStore
            ?? throw new ArgumentException("a race participant's store must carry the text companion.", nameof(inner));
        _ledger = inner as IContentUpgradeLedger
            ?? throw new ArgumentException("a race participant's store must keep the upgrade ledger.", nameof(inner));
        _ids = ids;
        _registry = registry;
        _packs = packs;
    }

    /// <summary>Calls that reached the complete text commit, counted at entry whatever they answered.</summary>
    public int TextCommitCalls { get; private set; }

    /// <summary>Calls that reached the row-only commit, counted at entry whatever they answered.</summary>
    public int RowCommitCalls { get; private set; }

    /// <summary>Calls to the guarded freeze.</summary>
    public int GuardedFreezeCalls { get; private set; }

    /// <summary>Calls to the released freeze, which overwrites whatever marker stands.</summary>
    public int LegacyFreezeCalls { get; private set; }

    /// <summary>Calls to the released clear, which clears whatever marker stands.</summary>
    public int LegacyClearCalls { get; private set; }

    /// <summary>The base each guarded release named.</summary>
    public IReadOnlyList<int> ReleaseBases => _releaseBases;

    /// <summary>What each guarded release answered.</summary>
    public IReadOnlyList<bool> ReleaseResults => _releaseResults;

    /// <summary>The token each guarded release was given.</summary>
    public IReadOnlyList<CancellationToken> ReleaseTokens => _releaseTokens;

    /// <summary>Parks this participant at the entry of its guarded freeze for <paramref name="baseVersion"/>.</summary>
    /// <param name="gate">The gate.</param>
    /// <param name="baseVersion">The base the freeze is called with.</param>
    public void ArmRunnerFreezeEntry(OnceGate gate, int baseVersion) => _freezeEntry = (gate, baseVersion);

    /// <summary>
    /// Parks this participant at the entry of its complete text commit for a plan on <paramref name="baseVersion"/>,
    /// and for <paramref name="upgradeId"/>'s publish when one is named.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="baseVersion">The plan's base version.</param>
    /// <param name="upgradeId">The upgrade the publish is stamped with, or null for any publish.</param>
    public void ArmTextCommitEntry(OnceGate gate, int baseVersion, string? upgradeId = null)
        => _commitEntry = (gate, baseVersion, upgradeId);

    /// <summary>Parks this participant at the exit of its first release, after that release completed.</summary>
    /// <param name="gate">The gate.</param>
    public void ArmFirstReleaseExit(OnceGate gate) => _firstReleaseExit = gate;

    /// <summary>
    /// Parks this participant at its first draft read after a publish built over it failed and unwound, which is a
    /// runner's failure resolution. Every release that publish and its runner owe has already run by then.
    /// </summary>
    /// <param name="gate">The gate.</param>
    public void ArmRecoveryRead(OnceGate gate) => _recoveryRead = gate;

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_recoveryLive && _recoveryRead is OnceGate recovery)
        {
            _recoveryLive = false;
            _recoveryRead = null;
            await recovery.PauseAsync(cancellationToken);
        }

        return await base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        LegacyFreezeCalls++;
        return base.FreezeDraftAsync(baseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public override Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        LegacyClearCalls++;
        return base.ClearDraftFreezeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        GuardedFreezeCalls++;
        if (_freezeEntry is (OnceGate entry, int entryBase) && entryBase == expectedBaseVersion)
        {
            _freezeEntry = null;
            await entry.PauseAsync(cancellationToken);
        }

        return await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<bool> ReleaseDraftFreezeForBaseAsync(
        int frozenForBaseVersion,
        CancellationToken cancellationToken = default)
    {
        _releaseBases.Add(frozenForBaseVersion);
        _releaseTokens.Add(cancellationToken);
        bool released = await base.ReleaseDraftFreezeForBaseAsync(frozenForBaseVersion, cancellationToken);
        _releaseResults.Add(released);
        if (_releaseTokens.Count == 1 && _firstReleaseExit is OnceGate exit)
        {
            _firstReleaseExit = null;
            await exit.PauseAsync(cancellationToken);
        }

        return released;
    }

    /// <inheritdoc />
    public override Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        RowCommitCalls++;
        return base.CommitPublishAsync(plan, request, pointers, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await new ContentPublishCommit(this, _packs, new ContentPublisher(this, _ids, _registry))
                .PublishAsync(request, cancellationToken);
        }
        catch (Exception)
        {
            // Its own finally has released by now, so the next draft read is the runner's resolution.
            _recoveryLive = _recoveryRead is not null;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        TextCommitCalls++;
        if (_commitEntry is (OnceGate entry, int entryBase, var upgrade)
            && plan.BaseVersion == entryBase
            && (upgrade is null || string.Equals(request.Upgrade?.Id, upgrade, StringComparison.Ordinal)))
        {
            _commitEntry = null;
            await entry.PauseAsync(cancellationToken);
        }

        return await _text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => _text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentTextPublishSnapshot> FreezeChangesAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
        => _text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => _text.ReadTextSnapshotAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryDiscardChangesAsync(
        ContentDraft expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => _text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => _text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => _text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(CancellationToken cancellationToken = default)
        => _ledger.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => _ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);

    /// <summary>
    /// One forced pause. The participant parks at the armed point the first time only, the fact awaits
    /// <see cref="Entered"/>, moves the others on and calls <see cref="Resume"/>. Nothing measures time, so an
    /// interleaving is forced exactly once rather than hoped for.
    /// </summary>
    internal sealed class OnceGate : IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed = 1;

        /// <summary>Completes when a participant has reached the gate and is parked there.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Parks the caller until <see cref="Resume"/>, the first time only.</summary>
        /// <param name="cancellationToken">The participant's token.</param>
        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return Task.CompletedTask;
            }

            _entered.TrySetResult();
            return _resumed.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Lets the parked participant continue.</summary>
        public void Resume() => _resumed.TrySetResult();

        /// <summary>Disarms the gate and resumes anything parked on it, which is the fact's cleanup.</summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _armed, 0);
            _resumed.TrySetResult();
            _entered.TrySetCanceled();
        }
    }
}
