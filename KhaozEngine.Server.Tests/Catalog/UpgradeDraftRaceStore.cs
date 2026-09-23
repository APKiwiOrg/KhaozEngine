using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// A store that lands ONE rival write into the window between an upgrade runner's no-draft check and its own
/// <see cref="IContentAuthoringStore.ApplyEditsAsync"/>, so the interleaving two runners on one catalog
/// produce by luck happens on every provider on demand.
/// <para>
/// The rival is the runner's OWN earlier plan, replayed: the double captures the edits of the first write
/// carrying <paramref name="captureNote"/> and writes them again once the catalog has moved past them, which
/// is what a second runner that planned against the older baseline and was slow to reach its write does.
/// Nothing here invents content the runner could not have produced.
/// </para>
/// <para>
/// It is here rather than shared with <c>KhaozEngine.Catalog.Tests</c> because the reference graph is how
/// push CI selects test projects, and a reference across that seam for one double would pull the SQL Server
/// suites into every catalog change.
/// </para>
/// </summary>
/// <para>
/// It also lands an OPERATOR's edit on the far side of that write, which is the PUBLISH window: the runner
/// has already inspected what its own write returned, and nothing looks at the draft again until the publish
/// freezes it. That rival is content no shipped definition plans, under an identity that is nobody's runner.
/// </para>
/// <param name="inner">The store behind the double, which must keep the upgrade ledger.</param>
/// <param name="captureNote">The note whose write is captured to be replayed later, empty under an operator edit.</param>
/// <param name="targetNote">The note of the write the rival's write lands around.</param>
/// <param name="operatorEdit">The operator's edit to land AFTER the targeted write, or null to replay the capture before it.</param>
/// <param name="operatorActor">The identity the operator's edit is written under.</param>
internal sealed class UpgradeDraftRaceStore(
    IContentAuthoringStore inner,
    string captureNote,
    string targetNote,
    ContentEdit? operatorEdit = null,
    string operatorActor = "")
    : IContentAuthoringStore, IContentUpgradeLedger
{
    readonly IContentUpgradeLedger _ledger = inner as IContentUpgradeLedger
        ?? throw new ArgumentException(
            "The race store decorates a catalog store that keeps an upgrade ledger.", nameof(inner));

    IReadOnlyList<ContentEdit>? _captured;
    string _capturedActor = string.Empty;
    bool _armed = true;

    /// <summary>Whether the rival write went in, which a test asserts the interleaving really happened by.</summary>
    public bool Raced { get; private set; }

    /// <inheritdoc />
    public IPackStore? PackStore => inner.PackStore;

    /// <inheritdoc />
    public async Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (_captured is null && captureNote.Length > 0
            && string.Equals(note, captureNote, StringComparison.Ordinal))
        {
            _captured = edits;
            _capturedActor = actor;
        }

        if (_armed && operatorEdit is not null && string.Equals(note, targetNote, StringComparison.Ordinal))
        {
            _armed = false;
            Raced = true;

            // The runner's own write returned the draft as it stood THEN, so what it holds is exactly its
            // plan and the operator's edit arrives after it has stopped looking.
            ContentDraft written = await inner.ApplyEditsAsync(
                edits, actor, operatorId, note, cancellationToken);
            await inner.ApplyEditsAsync(
                [operatorEdit], operatorActor, "oid:operator", "an operator's own afternoon", cancellationToken);
            return written;
        }

        if (_armed && _captured is not null && string.Equals(note, targetNote, StringComparison.Ordinal))
        {
            _armed = false;
            Raced = true;
            await inner.ApplyEditsAsync(
                _captured, _capturedActor, "oid:rival", captureNote, cancellationToken);
        }

        return await inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
        => inner.InitializeAsync(mode, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetSchemaVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => inner.GetStoreEpochAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetActiveVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => inner.GetPinnedVersionAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.GetVersionAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => inner.GetOpenDraftAsync(cancellationToken);

    /// <inheritdoc />
    public Task DiscardDraftAsync(
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.DiscardDraftAsync(actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
        => inner.FreezeDraftAsync(baseVersion, cancellationToken);

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => inner.ClearDraftFreezeAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => inner.ReadPublishBaselineAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
        => inner.CommitPublishAsync(plan, request, pointers, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
        => inner.PublishAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default)
        => inner.GetRowHistoryAsync(type, definitionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
        => inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task AppendOperationalAuditAsync(
        string action,
        string actor,
        string operatorId,
        string fieldName,
        string? value,
        string note,
        CancellationToken cancellationToken = default)
        => inner.AppendOperationalAuditAsync(
            action, actor, operatorId, fieldName, value, note, cancellationToken);

    /// <inheritdoc />
    public Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
        => inner.AllocateAsync(type, count, cancellationToken);

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => inner.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => inner.ListFamiliesAsync(type, cancellationToken);

    /// <inheritdoc />
    public Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
        => inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => _ledger.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => _ledger.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}
