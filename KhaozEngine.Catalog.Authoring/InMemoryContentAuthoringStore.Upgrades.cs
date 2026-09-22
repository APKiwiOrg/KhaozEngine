using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The content upgrade ledger of the in-memory store, which is the REFERENCE behaviour the two providers'
/// <c>catalog_content_upgrade</c> tables are held to.
/// <para>
/// <b>The applied row is written by the COMMIT and never by <see cref="RecordUpgradeAsync"/>.</b> It is
/// staged in the commit's build phase, where a duplicate id is still a refusal that leaves the store exactly
/// where it was, and added in the tail that cannot fail. A provider gets the same property from its one
/// transaction and its primary key.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore : IContentUpgradeLedger
{
    readonly Dictionary<string, ContentUpgradeRecord> _upgrades = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var records = new List<ContentUpgradeRecord>(_upgrades.Values);
            records.Sort(Order);
            return Task.FromResult<IReadOnlyList<ContentUpgradeRecord>>(records);
        }
    }

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        RequireRecordable(disposition);

        lock (_gate)
        {
            if (_upgrades.ContainsKey(stamp.Id))
            {
                // Recording an id that is already there is a NO-OP rather than an error: a crash between a
                // seed and its baseline record is resolved by running the record again.
                return Task.CompletedTask;
            }

            var record = new ContentUpgradeRecord(
                stamp.Id,
                stamp.Order,
                disposition,
                _activeVersion,
                actor,
                operatorId,
                _clock());

            var staged = new List<ContentAuditEntry>(1);
            StageUpgradeAudit(staged, record, string.Empty);
            _upgrades.Add(record.Id, record);
            _audit.Commit(staged);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The applied row of a publish, rendered with its audit entry and NOT yet added, or null when the
    /// request carries no upgrade. The caller already holds the gate and adds the record in its tail.
    /// </summary>
    /// <param name="staged">The commit's staging list, which the audit entry joins.</param>
    /// <param name="request">The publish request, whose actor, operator and note the row carries.</param>
    /// <param name="versionNumber">The version this publish is assigning.</param>
    /// <exception cref="ContentAuthoringException">The ledger already holds this upgrade id.</exception>
    ContentUpgradeRecord? StageAppliedUpgrade(
        List<ContentAuditEntry> staged,
        ContentPublishRequest request,
        int versionNumber)
    {
        if (request.Upgrade is not ContentUpgradeStamp stamp)
        {
            return null;
        }

        if (_upgrades.ContainsKey(stamp.Id))
        {
            throw AlreadyRecorded(stamp.Id);
        }

        var record = new ContentUpgradeRecord(
            stamp.Id,
            stamp.Order,
            ContentUpgradeDisposition.Applied,
            versionNumber,
            request.Actor,
            request.Operator,
            _clock());
        StageUpgradeAudit(staged, record, request.Note);
        return record;
    }

    /// <summary>
    /// One ledger row's audit entry: the disposition as the field name and the upgrade id as the after value,
    /// which is the shape all three stores write.
    /// </summary>
    /// <param name="staged">The staging list the entry is added to.</param>
    /// <param name="record">The ledger row being written.</param>
    /// <param name="note">The note the row carries, empty for a recorded one.</param>
    void StageUpgradeAudit(List<ContentAuditEntry> staged, ContentUpgradeRecord record, string note)
        => _audit.Stage(
            staged,
            ContentAuditActions.ContentUpgrade,
            record.Actor,
            record.Operator,
            default,
            0,
            default,
            ContentUpgradeDispositions.Token(record.Disposition),
            null,
            record.Id,
            record.VersionNumber,
            note);

    /// <summary>Ascending by order, then by id ordinally, which is the order the upgrades ran in.</summary>
    static int Order(ContentUpgradeRecord left, ContentUpgradeRecord right)
    {
        int byOrder = left.Order.CompareTo(right.Order);
        return byOrder != 0 ? byOrder : string.CompareOrdinal(left.Id, right.Id);
    }

    /// <summary>
    /// The one disposition <see cref="RecordUpgradeAsync"/> refuses. An applied row says a publish happened,
    /// and only the publish commit can say that truthfully.
    /// </summary>
    /// <param name="disposition">The disposition the caller passed.</param>
    static void RequireRecordable(ContentUpgradeDisposition disposition)
    {
        if (disposition == ContentUpgradeDisposition.Applied)
        {
            throw new ArgumentException(
                "An applied upgrade row is written inside the publish commit, so it cannot be recorded on its own. Use Adopted or Baseline.",
                nameof(disposition));
        }
    }

    static ContentAuthoringException AlreadyRecorded(string upgradeId)
        => new(
            FormattableString.Invariant(
                $"The content upgrade ledger already holds '{upgradeId}', so this publish was refused whole and nothing was written. Re-read the ledger: another runner applied it."),
            default,
            0,
            ContentAuthoringException.UpgradeAlreadyRecordedReason);
}
