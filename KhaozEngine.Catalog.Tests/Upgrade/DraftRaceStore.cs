using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Where a rival's write lands relative to the write of the definition under test, which is the whole of the
/// interleaving these tests exist for.
/// </summary>
internal enum DraftRaceMoment
{
    /// <summary>The rival's edits open the draft, and the runner's own write then APPENDS into it.</summary>
    BeforeTheRunnersWrite,

    /// <summary>The runner's write opens the draft, and the rival's edits then APPEND into it.</summary>
    AfterTheRunnersWrite,
}

/// <summary>
/// A store that lands ONE rival write into the window between the runner's no-draft check and its own
/// <see cref="IContentAuthoringStore.ApplyEditsAsync"/>, which is the interleaving two runners on one catalog
/// really produce and which no amount of looping reproduces on demand.
/// <para>
/// <b>The default rival is the runner's own earlier plan, replayed.</b> The double captures the edits of the
/// first write carrying <c>captureNote</c> and replays them under the same actor and note once the catalog
/// has moved past them, which is exactly what a second runner that planned against the old baseline and was
/// slow to get to its write does. Nothing here invents content the runner could not have produced.
/// </para>
/// <para>
/// It can also land a FOREIGN write instead, under another actor or carrying content no shipped definition
/// plans, which is the operator whose edit must never be destroyed.
/// </para>
/// </summary>
internal sealed class DraftRaceStore : ForwardingContentAuthoringStore, IContentUpgradeLedger
{
    readonly IContentUpgradeLedger _ledger;
    readonly string _captureNote;
    readonly string _targetNote;
    readonly DraftRaceMoment _moment;
    readonly string? _rivalActor;
    readonly IReadOnlyList<ContentEdit>? _rivalEdits;
    readonly string? _rivalNote;
    IReadOnlyList<ContentEdit>? _captured;
    string? _capturedActor;
    bool _armed = true;

    /// <summary>The rival that replays an earlier plan of this run's own.</summary>
    /// <param name="inner">The store behind the double, which keeps the upgrade ledger.</param>
    /// <param name="captureNote">The note whose write is captured to be replayed later.</param>
    /// <param name="targetNote">The note of the write the rival's lands around.</param>
    /// <param name="moment">Which side of the runner's write the rival's lands on.</param>
    internal DraftRaceStore(
        IContentAuthoringStore inner,
        string captureNote,
        string targetNote,
        DraftRaceMoment moment)
        : base(inner)
    {
        _ledger = (IContentUpgradeLedger)inner;
        _captureNote = captureNote;
        _targetNote = targetNote;
        _moment = moment;
    }

    /// <summary>The rival that writes content of its own, which is what an operator's console does.</summary>
    /// <param name="inner">The store behind the double, which keeps the upgrade ledger.</param>
    /// <param name="targetNote">The note of the write the rival's lands around.</param>
    /// <param name="moment">Which side of the runner's write the rival's lands on.</param>
    /// <param name="rivalActor">The identity the rival writes under.</param>
    /// <param name="rivalNote">The note the rival's write carries.</param>
    /// <param name="rivalEdits">The edits the rival writes.</param>
    internal DraftRaceStore(
        IContentAuthoringStore inner,
        string targetNote,
        DraftRaceMoment moment,
        string rivalActor,
        string rivalNote,
        IReadOnlyList<ContentEdit> rivalEdits)
        : base(inner)
    {
        _ledger = (IContentUpgradeLedger)inner;
        _captureNote = string.Empty;
        _targetNote = targetNote;
        _moment = moment;
        _rivalActor = rivalActor;
        _rivalNote = rivalNote;
        _rivalEdits = rivalEdits;
    }

    /// <summary>Whether the rival's write went in, which a test asserts the interleaving really happened by.</summary>
    internal bool Raced { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (_captured is null
            && _captureNote.Length > 0
            && string.Equals(note, _captureNote, StringComparison.Ordinal))
        {
            _captured = edits;
            _capturedActor = actor;
        }

        if (!_armed || !string.Equals(note, _targetNote, StringComparison.Ordinal))
        {
            return await base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
        }

        _armed = false;
        Raced = true;
        if (_moment == DraftRaceMoment.BeforeTheRunnersWrite)
        {
            await RivalWriteAsync(cancellationToken);
            return await base.ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken);
        }

        // The runner's own write returned the draft as it stood THEN, so the draft the runner holds is
        // exactly its plan and the rival's edits arrive after it stopped looking.
        ContentDraft written = await base.ApplyEditsAsync(
            edits, actor, operatorId, note, cancellationToken);
        await RivalWriteAsync(cancellationToken);
        return written;
    }

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

    Task RivalWriteAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentEdit>? edits = _rivalEdits ?? _captured;
        if (edits is null)
        {
            throw new InvalidOperationException(
                "The race store captured no write to replay, so the interleaving under test never happened.");
        }

        return Inner.ApplyEditsAsync(
            edits,
            _rivalActor ?? _capturedActor ?? UpgradeFixtures.Actor,
            "oid:rival",
            _rivalNote ?? _captureNote,
            cancellationToken);
    }
}
