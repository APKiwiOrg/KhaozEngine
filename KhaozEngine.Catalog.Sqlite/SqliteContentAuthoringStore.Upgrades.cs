using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The content upgrade ledger half: the <c>catalog_content_upgrade</c> table schema version 2 adds, read in
/// upgrade order and written in one transaction with its audit row.
/// <para>
/// <b>The applied row belongs to the PUBLISH transaction</b>, which is why the insert below takes an open
/// transaction rather than opening one. The version row and the history entry that says which upgrade
/// produced it commit together or not at all, and the primary key is what makes a second publish of one
/// upgrade a refusal with nothing written.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore : IContentUpgradeLedger
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = Command(
            """
            SELECT upgrade_id, upgrade_order, disposition, version_number, actor, operator, recorded_at_utc
            FROM catalog_content_upgrade
            ORDER BY upgrade_order, upgrade_id;
            """);

        var records = new List<ContentUpgradeRecord>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new ContentUpgradeRecord(
                reader.GetString(0),
                (int)reader.GetInt64(1),
                ContentUpgradeDispositions.Parse(reader.GetString(2)),
                (int)reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        }

        return records;
    }

    /// <inheritdoc />
    public async Task RecordUpgradeAsync(
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

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        if (await HoldsUpgradeAsync(stamp.Id, transaction, cancellationToken).ConfigureAwait(false))
        {
            // Recording an id that is already there is a NO-OP rather than an error: a crash between a seed
            // and its baseline record is resolved by running the record again.
            return;
        }

        int active = (int)await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);
        await InsertUpgradeAsync(
            transaction,
            new ContentUpgradeRecord(
                stamp.Id, stamp.Order, disposition, active, actor, operatorId, _clock()),
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    /// <summary>
    /// The applied row of a publish, inside the publish's OWN transaction, or nothing when the request
    /// carries no upgrade.
    /// </summary>
    /// <param name="transaction">The publish transaction, which the insert and its audit row join.</param>
    /// <param name="request">The publish request, whose actor, operator and note the row carries.</param>
    /// <param name="versionNumber">The version this publish is assigning.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ContentAuthoringException">The ledger already holds this upgrade id.</exception>
    async Task InsertAppliedUpgradeAsync(
        SqliteTransaction transaction,
        ContentPublishRequest request,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        if (request.Upgrade is not ContentUpgradeStamp stamp)
        {
            return;
        }

        // Checked rather than left to the primary key, so the refusal carries the reason token a caller keys
        // on. The check and the insert are in the publish's one transaction, so nothing can land between them.
        if (await HoldsUpgradeAsync(stamp.Id, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw AlreadyRecorded(stamp.Id);
        }

        await InsertUpgradeAsync(
            transaction,
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

    /// <summary>One ledger row and the audit row that records it. The caller owns the transaction.</summary>
    /// <param name="transaction">The open transaction both inserts join.</param>
    /// <param name="record">The row to write.</param>
    /// <param name="note">The note the audit row carries, empty for a recorded upgrade.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    async Task InsertUpgradeAsync(
        SqliteTransaction transaction,
        ContentUpgradeRecord record,
        string note,
        CancellationToken cancellationToken)
    {
        using (SqliteCommand command = Command(
            """
            INSERT INTO catalog_content_upgrade(
                upgrade_id, upgrade_order, disposition, version_number, actor, operator, recorded_at_utc)
            VALUES ($upgrade, $order, $disposition, $version, $actor, $operator, $at);
            """,
            transaction))
        {
            Bind(command, "$upgrade", record.Id);
            Bind(command, "$order", (long)record.Order);
            Bind(command, "$disposition", ContentUpgradeDispositions.Token(record.Disposition));
            Bind(command, "$version", (long)record.VersionNumber);
            Bind(command, "$actor", record.Actor);
            Bind(command, "$operator", record.Operator);
            Bind(command, "$at", Millis(record.RecordedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The disposition as the field name and the upgrade id as the after value, which is the shape all
        // three stores write.
        await AppendAuditAsync(
            transaction,
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

    /// <summary>Whether the ledger already holds one id. The caller owns the transaction.</summary>
    /// <param name="upgradeId">The upgrade id.</param>
    /// <param name="transaction">The open transaction.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    async Task<bool> HoldsUpgradeAsync(
        string upgradeId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT 1 FROM catalog_content_upgrade WHERE upgrade_id = $upgrade;", transaction);
        Bind(command, "$upgrade", upgradeId);
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
