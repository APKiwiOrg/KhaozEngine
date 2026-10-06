using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Upgrade;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// What every race participant's decorator shares: the upgrade ledger forwarded, the draft freeze lifecycle
/// counted and observed, and the gates a test arms on it. One instance belongs to one participant, so a
/// pause and a counter always name the publisher or runner that reached them.
/// <para>
/// <b>Gates are named by the step they stop, not by the call that implements it.</b> On the released API the
/// runner's freeze is <see cref="IContentAuthoringStore.FreezeDraftAsync"/>, its release is
/// <see cref="IContentAuthoringStore.ClearDraftFreezeAsync"/>, and the row publish route's freeze step starts
/// with the draft read inside Prepare. On the guarded API the freeze is
/// <see cref="IContentConditionalDraftFreeze.FreezeDraftForBaseAsync"/>, which is also where the row publish
/// route's freeze step starts, and the release is
/// <see cref="IContentConditionalDraftFreeze.ReleaseDraftFreezeForBaseAsync"/>. Both sets of gates live here,
/// in the decorator, so the test bodies keep their sequence and their assertions whichever call a pipeline
/// makes.
/// </para>
/// <para>
/// <b>A release is observed with the arguments the call actually carried.</b> The released clear takes no base
/// and answers nothing, so it records null for both and the token it was given. The guarded release records
/// its base, its answer and its token.
/// </para>
/// </summary>
internal abstract class FreezeRaceParticipant : ForwardingContentAuthoringStore, IContentUpgradeLedger
{
    readonly IContentUpgradeLedger _ledger;
    readonly IContentIdPersistence _ids;
    readonly ContentTypeRegistry _registry;
    readonly IPackStore _packs;
    readonly List<int?> _releaseBases = [];
    readonly List<bool?> _releaseResults = [];
    readonly List<CancellationToken> _releaseTokens = [];
    readonly List<string?> _commitRefusals = [];

    ContentPublishRequest? _publishing;
    (OnceGate Gate, int Base)? _prepareFreezeEntry;
    (OnceGate Gate, int Base)? _freezeEntry;
    (OnceGate Gate, int Base)? _freezeExit;
    OnceGate? _firstReleaseExit;
    OnceGate? _recoveryRead;
    bool _recoveryLive;

    /// <summary>Wraps one participant's store.</summary>
    /// <param name="inner">The participant's engine store, which must keep the upgrade ledger.</param>
    /// <param name="ids">The id persistence matching <paramref name="inner"/>.</param>
    /// <param name="registry">The catalog's registry.</param>
    /// <param name="packs">The pack target a publish built over this decorator writes to.</param>
    protected FreezeRaceParticipant(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(packs);

        _ledger = inner as IContentUpgradeLedger
            ?? throw new ArgumentException("a race participant's store must keep the upgrade ledger.", nameof(inner));
        _ids = ids;
        _registry = registry;
        _packs = packs;
    }

    /// <summary>The pack target a publish built over this decorator writes to.</summary>
    public IPackStore Packs => _packs;

    /// <summary>Calls that reached the complete text commit, counted at entry whatever they answered.</summary>
    public int TextCommitCalls { get; protected set; }

    /// <summary>Calls that reached the row-only commit, counted at entry whatever they answered.</summary>
    public int RowCommitCalls { get; private set; }

    /// <summary>Calls to the released row-only freeze, which overwrites whatever marker stands.</summary>
    public int LegacyFreezeCalls { get; private set; }

    /// <summary>Calls to the released release, which clears whatever marker stands.</summary>
    public int LegacyClearCalls { get; private set; }

    /// <summary>Calls to the guarded freeze, which refuses a base the store has moved past.</summary>
    public int GuardedFreezeCalls { get; private set; }

    /// <summary>Reads of the open draft.</summary>
    public int DraftReadCalls { get; private set; }

    /// <summary>The base each release named, or null for a release that takes none.</summary>
    public IReadOnlyList<int?> ReleaseBases => _releaseBases;

    /// <summary>What each release answered, or null for a release that answers nothing.</summary>
    public IReadOnlyList<bool?> ReleaseResults => _releaseResults;

    /// <summary>The token each release was actually given.</summary>
    public IReadOnlyList<CancellationToken> ReleaseTokens => _releaseTokens;

    /// <summary>The refusal reason of every commit, row or text, that this participant's store refused.</summary>
    public IReadOnlyList<string?> CommitRefusals => _commitRefusals;

    /// <summary>
    /// Parks this participant at the entry of the row publish route's freeze step, for a publish expecting
    /// <paramref name="expectedBase"/>. On the released API that step starts with Prepare's draft read, which
    /// is where a rival's newer draft can be picked up before the overwriting freeze. On the guarded API it
    /// starts with the guarded freeze itself.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="expectedBase">The base the parked publish expects.</param>
    public void ArmPrepareFreezeEntry(OnceGate gate, int expectedBase) => _prepareFreezeEntry = (gate, expectedBase);

    /// <summary>Parks this participant at the entry of its runner freeze for <paramref name="baseVersion"/>.</summary>
    /// <param name="gate">The gate.</param>
    /// <param name="baseVersion">The base the freeze is called with.</param>
    public void ArmRunnerFreezeEntry(OnceGate gate, int baseVersion) => _freezeEntry = (gate, baseVersion);

    /// <summary>
    /// Parks this participant at the exit of its runner freeze for <paramref name="baseVersion"/>, after the
    /// call has returned and released everything it held.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="baseVersion">The base the freeze is called with.</param>
    public void ArmRunnerFreezeExit(OnceGate gate, int baseVersion) => _freezeExit = (gate, baseVersion);

    /// <summary>Parks this participant at the exit of its first release, after that release completed.</summary>
    /// <param name="gate">The gate.</param>
    public void ArmFirstReleaseExit(OnceGate gate) => _firstReleaseExit = gate;

    /// <summary>
    /// Parks this participant at the entry of its first draft read after a publish built over this decorator
    /// failed and unwound, which is a runner's failure resolution. Every release that publish and its runner
    /// owe has already run by then.
    /// </summary>
    /// <param name="gate">The gate.</param>
    public void ArmRecoveryRead(OnceGate gate) => _recoveryRead = gate;

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        DraftReadCalls++;
        if (TakePrepareFreezeEntry() is OnceGate prepare)
        {
            await prepare.PauseAsync(cancellationToken);
        }
        else if (_recoveryLive && _recoveryRead is OnceGate recovery)
        {
            _recoveryLive = false;
            _recoveryRead = null;
            await recovery.PauseAsync(cancellationToken);
        }

        return await base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        LegacyFreezeCalls++;
        await RunnerFreezeEntryAsync(baseVersion, cancellationToken);
        await base.FreezeDraftAsync(baseVersion, cancellationToken);
        await RunnerFreezeExitAsync(baseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<ContentDraft> FreezeDraftForBaseAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        GuardedFreezeCalls++;
        if (TakePrepareFreezeEntry() is OnceGate prepare)
        {
            await prepare.PauseAsync(cancellationToken);
        }

        await RunnerFreezeEntryAsync(expectedBaseVersion, cancellationToken);
        ContentDraft frozen = await base.FreezeDraftForBaseAsync(expectedBaseVersion, cancellationToken);
        await RunnerFreezeExitAsync(expectedBaseVersion, cancellationToken);
        return frozen;
    }

    /// <inheritdoc />
    public override async Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        LegacyClearCalls++;
        _releaseBases.Add(null);
        _releaseTokens.Add(cancellationToken);
        await base.ClearDraftFreezeAsync(cancellationToken);
        _releaseResults.Add(null);
        await AfterReleaseAsync(cancellationToken);
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
        await AfterReleaseAsync(cancellationToken);
        return released;
    }

    /// <inheritdoc />
    public override async Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        RowCommitCalls++;
        try
        {
            return await base.CommitPublishAsync(plan, request, pointers, cancellationToken);
        }
        catch (ContentAuthoringException refused)
        {
            _commitRefusals.Add(refused.Reason);
            throw;
        }
    }

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

    /// <summary>A refused commit's reason, recorded so a report can name what the store said.</summary>
    /// <param name="reason">The refusal reason.</param>
    protected void RecordCommitRefusal(string? reason) => _commitRefusals.Add(reason);

    /// <summary>
    /// The whole publish pipeline built over THIS decorator, so its freeze, its commit and its release all
    /// come through the members above. A decorator that is a text store therefore takes the text route.
    /// </summary>
    /// <param name="request">The publish request.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    protected async Task<ContentPublishResult> PublishOverSelfAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken)
    {
        _publishing = request;
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
        finally
        {
            _publishing = null;
        }
    }

    /// <summary>
    /// The row publish route's freeze entry gate, taken once by a publish built over this decorator that expects
    /// the armed base, or null.
    /// </summary>
    OnceGate? TakePrepareFreezeEntry()
    {
        if (_publishing is ContentPublishRequest publishing
            && _prepareFreezeEntry is (OnceGate prepare, int expected)
            && publishing.ExpectedBaseVersion == expected)
        {
            _prepareFreezeEntry = null;
            return prepare;
        }

        return null;
    }

    /// <summary>The runner freeze's entry gate for <paramref name="baseVersion"/>, once.</summary>
    async Task RunnerFreezeEntryAsync(int baseVersion, CancellationToken cancellationToken)
    {
        if (_freezeEntry is (OnceGate entry, int entryBase) && entryBase == baseVersion)
        {
            _freezeEntry = null;
            await entry.PauseAsync(cancellationToken);
        }
    }

    /// <summary>The runner freeze's exit gate for <paramref name="baseVersion"/>, once, after the call returned.</summary>
    async Task RunnerFreezeExitAsync(int baseVersion, CancellationToken cancellationToken)
    {
        if (_freezeExit is (OnceGate exit, int exitBase) && exitBase == baseVersion)
        {
            _freezeExit = null;
            await exit.PauseAsync(cancellationToken);
        }
    }

    /// <summary>The first release's exit gate, reached only once the release itself has completed.</summary>
    /// <param name="cancellationToken">The release's token.</param>
    protected async Task AfterReleaseAsync(CancellationToken cancellationToken)
    {
        if (_releaseTokens.Count == 1 && _firstReleaseExit is OnceGate exit)
        {
            _firstReleaseExit = null;
            await exit.PauseAsync(cancellationToken);
        }
    }
}

/// <summary>
/// A race participant whose commit MUST check the freeze marker: it declares and forwards the whole
/// <see cref="IContentTextAuthoringStore"/> and the upgrade ledger, and builds its publish over itself, so a
/// publish through it reaches <see cref="CommitTextPublishAsync"/> here. The counters prove it did, which is
/// what stops a reproduction from passing because nothing compared the marker.
/// <para>
/// A lifecycle test may derive from it to lose a text commit's acknowledgement after the real commit ran.
/// </para>
/// </summary>
internal class FreezeRaceStore : FreezeRaceParticipant, IContentTextAuthoringStore
{
    readonly IContentTextAuthoringStore _text;
    (OnceGate Gate, int Base, string? Upgrade)? _commitEntry;

    /// <summary>Wraps one participant's text capable store.</summary>
    /// <param name="inner">The participant's engine store, which must carry the text companion and the ledger.</param>
    /// <param name="ids">The id persistence matching <paramref name="inner"/>.</param>
    /// <param name="registry">The catalog's registry.</param>
    /// <param name="packs">The pack target this participant's publish writes to.</param>
    public FreezeRaceStore(
        IContentAuthoringStore inner,
        IContentIdPersistence ids,
        ContentTypeRegistry registry,
        IPackStore packs)
        : base(inner, ids, registry, packs)
    {
        _text = inner as IContentTextAuthoringStore
            ?? throw new ArgumentException("a text race participant's store must carry the text companion.", nameof(inner));
    }

    /// <summary>
    /// Parks this participant at the entry of its complete text commit for a plan standing on
    /// <paramref name="baseVersion"/>, and for <paramref name="upgradeId"/>'s publish when one is named.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="baseVersion">The plan's base version.</param>
    /// <param name="upgradeId">The upgrade the publish is stamped with, or null for any publish.</param>
    public void ArmTextCommitEntry(OnceGate gate, int baseVersion, string? upgradeId = null)
        => _commitEntry = (gate, baseVersion, upgradeId);

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => PublishOverSelfAsync(request, cancellationToken);

    /// <inheritdoc />
    public virtual async Task<ContentVersionRecord> CommitTextPublishAsync(
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

        try
        {
            return await _text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);
        }
        catch (ContentAuthoringException refused)
        {
            RecordCommitRefusal(refused.Reason);
            throw;
        }
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
}
