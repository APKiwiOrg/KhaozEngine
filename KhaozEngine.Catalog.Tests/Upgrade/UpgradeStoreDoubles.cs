using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Every member of <see cref="IContentAuthoringStore"/> forwarded to an inner store, so a double overrides
/// the one member it is about and says nothing about the other twenty-five.
/// </summary>
internal abstract class ForwardingContentAuthoringStore(IContentAuthoringStore inner) : IContentAuthoringStore
{
    /// <summary>The store behind the decorator, which a double also reads directly.</summary>
    protected IContentAuthoringStore Inner => inner;

    /// <inheritdoc />
    public IPackStore? PackStore => inner.PackStore;

    /// <inheritdoc />
    public virtual Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
        => inner.InitializeAsync(mode, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetSchemaVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => inner.GetStoreEpochAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetActiveVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetPinnedVersionAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.GetVersionAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => inner.GetOpenDraftAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.DiscardDraftAsync(actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        => inner.FreezeDraftAsync(baseVersion, cancellationToken);

    /// <inheritdoc />
    public virtual Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => inner.ClearDraftFreezeAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => inner.ReadPublishBaselineAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => inner.CommitPublishAsync(plan, request, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => inner.PublishAsync(request, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
        => inner.GetRowHistoryAsync(type, definitionId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);

    /// <inheritdoc />
    public virtual Task AppendOperationalAuditAsync(
        string action,
        string actor,
        string operatorId,
        string fieldName,
        string? value,
        string note,
        CancellationToken cancellationToken = default)
        => inner.AppendOperationalAuditAsync(action, actor, operatorId, fieldName, value, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
        => inner.AllocateAsync(type, count, cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => inner.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => inner.ListFamiliesAsync(type, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);
}

/// <summary>
/// A store that keeps NO upgrade ledger, which is a pre-schema-version-2 catalog from the runner's side. It
/// forwards everything and simply does not implement <see cref="IContentUpgradeLedger"/>.
/// </summary>
internal sealed class LedgerlessStore(IContentAuthoringStore inner) : ForwardingContentAuthoringStore(inner)
{
}

/// <summary>
/// Counts the four writes a run can make to the one draft, so a test can say what the runner did rather than
/// only what the catalog ended up holding.
/// <para>
/// The counters see the RUNNER's calls only. A store's own publish pipeline runs against the inner store, so
/// the freeze it sets and releases for itself never reaches this decorator, which is exactly the separation
/// the freeze assertion needs.
/// </para>
/// </summary>
internal sealed class CountingContentAuthoringStore(InMemoryContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    /// <summary>How many times the runner wrote edits into the draft.</summary>
    public int EditWrites { get; private set; }

    /// <summary>How many times the runner discarded the draft.</summary>
    public int Discards { get; private set; }

    /// <summary>How many times the runner cleared a freeze marker.</summary>
    public int FreezeClears { get; private set; }

    /// <summary>How many publishes the runner started.</summary>
    public int Publishes { get; private set; }

    /// <summary>
    /// What <see cref="FreezeClears"/> stood at when the runner started its FIRST publish, which is the
    /// number that has to be zero: a freeze cleared before the runner has published the draft itself is a
    /// live publisher's marker being taken away.
    /// </summary>
    public int FreezeClearsBeforeFirstPublish { get; private set; }

    /// <inheritdoc />
    public override Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        EditWrites++;
        return base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public override Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        Discards++;
        return base.DiscardDraftAsync(actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public override Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
    {
        FreezeClears++;
        return base.ClearDraftFreezeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Publishes == 0)
        {
            FreezeClearsBeforeFirstPublish = FreezeClears;
        }

        Publishes++;
        return base.PublishAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}

/// <summary>
/// A store where a draft appears AFTER the runner's step 5 gate, which is the window an operator's console
/// and a second runner both really open one in. The draft is injected on the second look at the draft, which
/// is the publish pre-flight, and optionally withdrawn on a later one the way a rival publisher's goes away.
/// </summary>
/// <param name="inner">The store behind the double.</param>
/// <param name="actor">The identity that opens the injected draft.</param>
/// <param name="note">The note the injected draft carries.</param>
/// <param name="edit">The edit the injected draft holds.</param>
/// <param name="withdrawAtLook">The look the draft is discarded on, or 0 to leave it standing.</param>
internal sealed class DraftAppearsAfterTheGateStore(
    InMemoryContentAuthoringStore inner,
    string actor,
    string note,
    ContentEdit edit,
    int withdrawAtLook = 0)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    /// <summary>How many times the RUNNER looked at the open draft, which is how a stand-off is counted.</summary>
    public int Looks { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        Looks++;
        if (Looks == 2)
        {
            await inner.ApplyEditsAsync([edit], actor, "oid:injected", note, cancellationToken);
        }
        else if (withdrawAtLook > 0 && Looks == withdrawAtLook)
        {
            await inner.DiscardDraftAsync(actor, "oid:injected", cancellationToken);
        }

        return await base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}

/// <summary>
/// A store whose first publish is refused because the base version MOVED, and whose active version then
/// reads as one higher, which is what a rival deploy leaves behind between two attempts. It is the state a
/// run given an expected version must refuse to publish onto.
/// </summary>
internal sealed class BaseMovesBetweenAttemptsStore(InMemoryContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    bool _armed = true;
    int _bump;

    /// <inheritdoc />
    public override async Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => await base.GetActiveVersionAsync(cancellationToken) + _bump;

    /// <inheritdoc />
    public override async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_armed)
        {
            return await base.PublishAsync(request, cancellationToken);
        }

        _armed = false;
        _bump = 1;
        throw new ContentAuthoringException(
            "another publisher committed onto this base version first.",
            default,
            0,
            ContentAuthoringException.BaseVersionMovedReason);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}

/// <summary>
/// A store whose publish throws AFTER the commit transaction returns, which is the interruption design step 9
/// exists for: the version is live, the ledger row is written, the draft is deleted, and the caller sees an
/// exception. A runner that assumed the upgrade did not land would publish it a second time.
/// <para>
/// It builds the publish pipeline over ITSELF rather than delegating to the inner store's own
/// <c>PublishAsync</c>, so the throw really is after <see cref="CommitPublishAsync"/> returned rather than
/// around a publish that ran somewhere else.
/// </para>
/// </summary>
internal sealed class CrashAfterCommitStore(
    InMemoryContentAuthoringStore inner,
    IPackStore packs,
    ContentTypeRegistry registry)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    /// <summary>Whether the NEXT commit throws after it returns. It disarms itself, so a retry gets through.</summary>
    public bool Armed { get; set; } = true;

    /// <summary>How many commits ran to completion, which proves the second publish never happened.</summary>
    public int Commits { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ContentVersionRecord record = await Inner.CommitPublishAsync(plan, request, cancellationToken);
        Commits++;
        if (Armed)
        {
            Armed = false;
            throw new IOException("the host died after the commit point and before the sweep.");
        }

        return record;
    }

    /// <inheritdoc />
    public override Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => new ContentPublishCommit(this, packs, new ContentPublisher(this, inner, registry))
            .PublishAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}
