using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// An authoring store that forwards every ROW-ONLY member to a real store and implements no text companion,
/// recording each member it was asked for. It is the honest shape of a provider that cannot read or commit
/// text: the publish and rebuild code see exactly the seam such a provider exposes.
/// <para>
/// It declares no guarded freeze companion either, so a publish over it is refused. A test that publishes
/// through the row-only seam uses <see cref="ConditionalRowOnlyStoreView"/>.
/// </para>
/// </summary>
/// <param name="inner">The real store every member is answered from.</param>
internal class RowOnlyStoreView(IContentAuthoringStore inner) : IContentAuthoringStore
{
    readonly List<string> _calls = [];

    /// <summary>Every member called, in order, by name.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>The members that change store state, which a read-only recovery must never reach.</summary>
    public static IReadOnlyList<string> WriteMembers { get; } =
    [
        nameof(InitializeAsync), nameof(SetPinnedVersionAsync), nameof(ApplyEditsAsync), nameof(DiscardDraftAsync),
        nameof(FreezeDraftAsync), nameof(CommitPublishAsync), nameof(PublishAsync), nameof(RollbackToAsync),
        nameof(AppendOperationalAuditAsync), nameof(AllocateAsync), nameof(AllocateInFamilyAsync),
        nameof(CreateFamilyAsync), nameof(ImportBundleAsync), "ApplyChangesAsync", "FreezeChangesAsync",
        "CommitTextPublishAsync", "TryDiscardChangesAsync", "ImportTextBundleAsync", "RollbackTextToAsync",
    ];

    /// <summary>The store behind the view.</summary>
    protected IContentAuthoringStore Inner => inner;

    /// <inheritdoc />
    public IPackStore? PackStore => HidePackStore ? null : inner.PackStore;

    /// <summary>When true, the view answers no pack store, so nothing can be read back through it.</summary>
    public bool HidePackStore { get; set; }

    /// <summary>When set, replaces the version record the view answers.</summary>
    public Func<ContentVersionRecord, ContentVersionRecord>? RecordOverride { get; set; }

    /// <summary>Records one member call.</summary>
    protected void Record(string member)
    {
        lock (_calls)
        {
            _calls.Add(member);
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default)
    {
        Record(nameof(InitializeAsync));
        return inner.InitializeAsync(mode, cancellationToken);
    }

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
    public Task SetPinnedVersionAsync(int? version, string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(SetPinnedVersionAsync));
        return inner.SetPinnedVersionAsync(version, actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default)
        => inner.ListVersionsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default)
    {
        ContentVersionRecord? record = await inner.GetVersionAsync(versionNumber, cancellationToken).ConfigureAwait(false);
        return record is not null && RecordOverride is not null ? RecordOverride(record) : record;
    }

    /// <inheritdoc />
    public Task<ContentSnapshot> LoadSnapshotAsync(int versionNumber, ContentTypeRegistry registry, CancellationToken cancellationToken = default)
        => inner.LoadSnapshotAsync(versionNumber, registry, cancellationToken);

    /// <inheritdoc />
    public virtual Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        => inner.GetOpenDraftAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(ApplyEditsAsync));
        return inner.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(DiscardDraftAsync));
        return inner.DiscardDraftAsync(actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        Record(nameof(FreezeDraftAsync));
        return inner.FreezeDraftAsync(baseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => inner.ClearDraftFreezeAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => inner.ReadPublishBaselineAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
    {
        Record(nameof(CommitPublishAsync));
        return inner.CommitPublishAsync(plan, request, pointers, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> PublishAsync(ContentPublishRequest request, CancellationToken cancellationToken = default)
    {
        Record(nameof(PublishAsync));
        return inner.PublishAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft> RollbackToAsync(
        int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(RollbackToAsync));
        return inner.RollbackToAsync(targetVersion, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type, int versionNumber, string? keyPrefix, bool includeRetired, int skip, int take, CancellationToken cancellationToken = default)
        => inner.ListRowsAsync(type, versionNumber, keyPrefix, includeRetired, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type, int definitionId, CancellationToken cancellationToken = default)
        => inner.GetRowHistoryAsync(type, definitionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type, int definitionId, int skip, int take, CancellationToken cancellationToken = default)
        => inner.ListAuditAsync(type, definitionId, skip, take, cancellationToken);

    /// <inheritdoc />
    public Task AppendOperationalAuditAsync(
        string action, string actor, string operatorId, string fieldName, string? value, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(AppendOperationalAuditAsync));
        return inner.AppendOperationalAuditAsync(action, actor, operatorId, fieldName, value, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateAsync(ContentTypeId type, int count, CancellationToken cancellationToken = default)
    {
        Record(nameof(AllocateAsync));
        return inner.AllocateAsync(type, count, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
    {
        Record(nameof(AllocateInFamilyAsync));
        return inner.AllocateInFamilyAsync(familyId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(ContentTypeId type, CancellationToken cancellationToken = default)
        => inner.ListFamiliesAsync(type, cancellationToken);

    /// <inheritdoc />
    public Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type, string familyKey, int blockSize, string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(CreateFamilyAsync));
        return inner.CreateFamilyAsync(type, familyKey, blockSize, actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(ImportBundleAsync));
        return inner.ImportBundleAsync(bundle, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ExportBundleAsync(versionNumber, cancellationToken);
}

/// <summary>
/// A minimal test double of the text companion over the in-memory reference store. It can answer an exact
/// version's text with a supplied snapshot, or refuse it as UNKNOWN provenance, which is the legacy state a
/// durable provider holds for a version committed before text existed. The reference store itself never
/// produces an unknown version, so this branch is reachable only through a double, never a production shortcut.
/// It forwards the guarded freeze companion too, so a publish over it keeps the text route.
/// </summary>
/// <param name="inner">The reference store.</param>
internal sealed class TextStoreDouble(InMemoryContentAuthoringStore inner)
    : ConditionalRowOnlyStoreView(inner), IContentTextAuthoringStore
{
    IContentTextAuthoringStore Text => (IContentTextAuthoringStore)Inner;

    /// <summary>When set, answers the exact-version text instead of the reference store.</summary>
    public Func<int, ContentVersionTextSnapshot>? TextOverride { get; set; }

    /// <summary>When true, every exact-version text read refuses as unknown provenance.</summary>
    public bool ProvenanceUnknown { get; set; }

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(ApplyChangesAsync));
        return Text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
    {
        Record(nameof(FreezeChangesAsync));
        return Text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
    {
        Record(nameof(ReadTextSnapshotAsync));
        if (ProvenanceUnknown)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant($"Version {versionNumber} has no complete text record."),
                default,
                0,
                ContentAuthoringException.TextProvenanceUnknownReason);
        }

        return TextOverride is not null
            ? Task.FromResult(TextOverride(versionNumber))
            : Text.ReadTextSnapshotAsync(versionNumber, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
    {
        Record(nameof(CommitTextPublishAsync));
        return Text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryDiscardChangesAsync(ContentDraft expected, string actor, string operatorId, CancellationToken cancellationToken = default)
    {
        Record(nameof(TryDiscardChangesAsync));
        return Text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(ImportTextBundleAsync));
        return Text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
    {
        Record(nameof(RollbackTextToAsync));
        return Text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);
    }
}
