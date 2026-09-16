using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The DRAFT FREEZE of spec 6.2: <c>catalog_draft.frozen_for_base_version</c>, the marker a publish sets at
/// step 1, the release it runs on every exit path, and the refusal every draft write reads.
/// <para>
/// <b>It is a column rather than the row lock the spec describes, and that is forced rather than chosen.</b>
/// This provider's Serializable transaction covers step 10 alone, and step 9 writes the whole pack before it
/// opens, so no lock this backend can take spans steps 1 to 10. A durable marker does, and it survives the
/// process too, which a lock never could: a publish killed after its commit leaves one behind, and that case
/// has to be recoverable rather than merely rare.
/// </para>
/// <para>
/// <b>A marker naming a base version the database no longer stands at is STALE.</b> Only a publish that
/// committed and then died before its own release can leave one, and the draft it names would otherwise
/// refuse every edit forever, so the baseline read every publish starts with clears it.
/// </para>
/// <para>
/// <b>Unexercised.</b> No SQL Server is reachable from the build this landed in, so every statement here is
/// written to mirror the SQLite provider's, which the conformance suite does run. The SQL Server conformance
/// class overrides the same facts under its gated attribute, so they run the moment an instance is there.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    public Task FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);

        return WriteAsync(
            async (scope, token) =>
            {
                // It OVERWRITES rather than refusing an already frozen draft. A marker a dead publish left
                // behind must not block the retry, and the retry is what an operator does to recover.
                await using SqlCommand command = Command(
                    scope,
                    "UPDATE dbo.catalog_draft SET frozen_for_base_version = @base WHERE draft_key = 1;");
                Bind(command, "@base", baseVersion);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                {
                    throw new ContentAuthoringException(
                        "There is no open draft to freeze, so there is nothing to publish.",
                        default,
                        0,
                        ContentAuthoringException.NoOpenDraftReason);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearDraftFreezeAsync(CancellationToken cancellationToken = default)
        => WriteAsync((scope, token) => ClearFreezeAsync(scope, token), cancellationToken);

    /// <summary>The freeze released, leaving the draft and its edits alone. The caller owns the scope.</summary>
    /// <param name="scope">The caller's connection and transaction.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    static async Task ClearFreezeAsync(SqlServerCatalogScope scope, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "UPDATE dbo.catalog_draft SET frozen_for_base_version = NULL WHERE draft_key = 1;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A marker naming a base version this database no longer stands at, cleared. It runs on the baseline
    /// read rather than on the draft read: a console polling the draft should not be the thing that repairs
    /// it, and the publish is what needs it repaired.
    /// </summary>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task ClearStaleFreezeAsync(CancellationToken cancellationToken)
        => WriteAsync(
            async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_draft SET frozen_for_base_version = NULL
                    WHERE draft_key = 1
                      AND frozen_for_base_version IS NOT NULL
                      AND frozen_for_base_version <>
                          (SELECT active_version FROM dbo.catalog_metadata WHERE metadata_key = 1);
                    """);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// The refusal every draft write runs first, inside the caller's own Serializable transaction, so the
    /// marker cannot be written between the check and the write it guards.
    /// </summary>
    /// <param name="scope">The caller's connection and transaction.</param>
    /// <param name="member">The member being refused, which is what the message names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    static async Task RequireNotFrozenAsync(
        SqlServerCatalogScope scope,
        string member,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "SELECT frozen_for_base_version FROM dbo.catalog_draft WHERE draft_key = 1;");
        object? held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (held is null or DBNull)
        {
            return;
        }

        throw new ContentAuthoringException(
            FormattableString.Invariant(
                $"{member} is refused: a publish standing on version {Convert.ToInt32(held, CultureInfo.InvariantCulture)} holds this draft frozen. The change set that publish read at step 1 is the one it will delete, so an edit arriving now would either be published without review or be deleted unpublished."),
            default,
            0,
            ContentAuthoringException.PublishInProgressReason);
    }

    /// <summary>
    /// Step 10's draft delete, scoped to the edits the plan FROZE. The caller owns the scope.
    /// <para>
    /// The freeze is what makes that set the whole draft, so an edit surviving here means the marker did not
    /// hold. It is kept rather than deleted: an edit published without review is a defect and an edit deleted
    /// unpublished is a lost afternoon, and the draft carrying it forward is the only outcome that is
    /// neither. The survivor's base version moves to the version that just committed, because that is what
    /// its edits now sit on top of, and the marker goes either way.
    /// </para>
    /// <para>
    /// The edit's field rows go with it through the cascade the schema declares.
    /// </para>
    /// </summary>
    /// <param name="scope">The commit's connection and transaction.</param>
    /// <param name="plan">The plan committing, whose frozen edits leave the draft.</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    static async Task DeleteFrozenEditsAsync(
        SqlServerCatalogScope scope,
        ContentPublishPlan plan,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentEdit> frozen = plan.FrozenEdits;
        for (int i = 0; i < frozen.Count; i++)
        {
            ContentEditTarget target = ContentChangeSet.TargetOf(frozen[i]);
            await using SqlCommand command = Command(
                scope,
                """
                DELETE FROM dbo.catalog_draft_edit
                WHERE type_id = @type AND definition_id = @id AND content_key = @key;
                """);
            Bind(command, "@type", (int)target.Type.Value);
            Bind(command, "@id", target.DefinitionId);
            Bind(command, "@key", target.Key.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int survivors = await ReadIntAsync(
            scope, "SELECT COUNT(*) FROM dbo.catalog_draft_edit;", cancellationToken).ConfigureAwait(false);
        if (survivors == 0)
        {
            await using SqlCommand draft = Command(scope, "DELETE FROM dbo.catalog_draft;");
            await draft.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using SqlCommand rebase = Command(
            scope,
            """
            UPDATE dbo.catalog_draft SET base_version = @version, frozen_for_base_version = NULL
            WHERE draft_key = 1;
            """);
        Bind(rebase, "@version", plan.VersionNumber);
        await rebase.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
