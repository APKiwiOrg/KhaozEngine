using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The content upgrade ledger half: the <c>catalog_content_upgrade</c> table schema version 2 adds, read in
/// upgrade order and written in one Serializable transaction with its audit row.
/// <para>
/// <b>The applied row belongs to the PUBLISH transaction</b>, which is why the insert below takes an open
/// scope rather than opening one. The version row and the history entry that says which upgrade produced it
/// commit together or not at all, and the primary key is what makes a second publish of one upgrade a refusal
/// with nothing written, whichever machine the second runner is on.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore : IContentUpgradeLedger
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => ReadAsync(
            static async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope,
                    """
                    SELECT upgrade_id, upgrade_order, disposition, version_number, actor, [operator],
                           recorded_at_utc
                    FROM dbo.catalog_content_upgrade
                    ORDER BY upgrade_order, upgrade_id;
                    """);

                var records = new List<ContentUpgradeRecord>();
                await using SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    records.Add(new ContentUpgradeRecord(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        ContentUpgradeDispositions.Parse(reader.GetString(2)),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetDateTimeOffset(6)));
                }

                return (IReadOnlyList<ContentUpgradeRecord>)records;
            },
            cancellationToken);

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

        return WriteAsync(
            async (scope, token) =>
            {
                if (await HoldsUpgradeAsync(scope, stamp.Id, token).ConfigureAwait(false))
                {
                    // Recording an id that is already there is a NO-OP rather than an error: a crash between
                    // a seed and its baseline record is resolved by running the record again.
                    return;
                }

                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                await InsertUpgradeAsync(
                    scope,
                    new ContentUpgradeRecord(
                        stamp.Id, stamp.Order, disposition, active, actor, operatorId, _clock()),
                    string.Empty,
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// The applied row of a publish, inside the publish's OWN transaction, or nothing when the request
    /// carries no upgrade.
    /// </summary>
    /// <param name="scope">The publish transaction, which the insert and its audit row join.</param>
    /// <param name="request">The publish request, whose actor, operator and note the row carries.</param>
    /// <param name="versionNumber">The version this publish is assigning.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ContentAuthoringException">The ledger already holds this upgrade id.</exception>
    async Task InsertAppliedUpgradeAsync(
        SqlServerCatalogScope scope,
        ContentPublishRequest request,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        if (request.Upgrade is not ContentUpgradeStamp stamp)
        {
            return;
        }

        // Checked rather than left to the primary key, so the refusal carries the reason token a caller keys
        // on. The read takes an UPDATE range lock, so a second publisher inserting the same id between the
        // check and the insert waits and is then refused by name rather than deadlocking.
        if (await HoldsUpgradeAsync(scope, stamp.Id, cancellationToken).ConfigureAwait(false))
        {
            throw AlreadyRecorded(stamp.Id);
        }

        await InsertUpgradeAsync(
            scope,
            new ContentUpgradeRecord(
                stamp.Id,
                stamp.Order,
                ContentUpgradeDisposition.Applied,
                versionNumber,
                request.Actor,
                request.Operator,
                _clock()),
            request.Note,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One ledger row and the audit row that records it. The caller owns the scope.</summary>
    /// <param name="scope">The open transaction both inserts join.</param>
    /// <param name="record">The row to write.</param>
    /// <param name="note">The note the audit row carries, empty for a recorded upgrade.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    async Task InsertUpgradeAsync(
        SqlServerCatalogScope scope,
        ContentUpgradeRecord record,
        string note,
        CancellationToken cancellationToken)
    {
        await using (SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_content_upgrade(
                upgrade_id, upgrade_order, disposition, version_number, actor, [operator], recorded_at_utc)
            VALUES (@upgrade, @upgradeOrder, @disposition, @version, @actor, @operator, @at);
            """))
        {
            BindText(command, "@upgrade", record.Id);
            BindInt(command, "@upgradeOrder", record.Order);
            BindText(command, "@disposition", ContentUpgradeDispositions.Token(record.Disposition));
            BindInt(command, "@version", record.VersionNumber);
            BindText(command, "@actor", record.Actor);
            BindText(command, "@operator", record.Operator);
            BindTime(command, "@at", record.RecordedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The disposition as the field name and the upgrade id as the after value, which is the shape all
        // three stores write.
        await AppendAuditAsync(
            scope,
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
            note,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the ledger already holds one id. The caller owns the scope.
    /// <para>
    /// <b><c>UPDLOCK, HOLDLOCK</c> and not the Serializable default.</b> Serializable alone takes a SHARED
    /// range lock on the key, so two recorders of one id both read, both find nothing, and both then try to
    /// convert to an exclusive lock for the insert. That is a conversion deadlock, and the victim surfaces as
    /// a provider deadlock error rather than as the refusal the caller keys on. <c>UPDLOCK</c> takes the
    /// update lock at the READ, which only one of them can hold, so the second waits and then reads the row
    /// the first one wrote. <c>HOLDLOCK</c> keeps the range locked on a key that is not there yet, which is
    /// what makes the absence itself stable until the commit.
    /// </para>
    /// </summary>
    /// <param name="scope">The open scope.</param>
    /// <param name="upgradeId">The upgrade id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    static async Task<bool> HoldsUpgradeAsync(
        SqlServerCatalogScope scope,
        string upgradeId,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT 1 FROM dbo.catalog_content_upgrade WITH (UPDLOCK, HOLDLOCK)
            WHERE upgrade_id = @upgrade;
            """);
        BindText(command, "@upgrade", upgradeId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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
