using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The bundle half: the lossless export of one version, and the import that seeds an EMPTY database from
/// one.
/// <para>
/// <b>An import runs through the ordinary publish and there is no second mechanism.</b> It turns the bundle
/// into a draft of <c>Add</c> edits, some carrying their own id and some not, and publishes it as version 1.
/// </para>
/// <para>
/// <b>Into an EMPTY database only</b>, empty meaning <c>catalog_version</c> holds no rows. That single rule
/// is the answer to a whole class of seeding defect, where a seed that runs repeatedly against live data
/// either reverts an operator's value on the next deploy or is beaten forever by a stored row.
/// </para>
/// <para>
/// <b>The restamped rules are held in memory until the publish commits them</b>, and that is forced by the
/// schema rather than chosen: <c>catalog_remap_rule.introduced_in</c> has a foreign key to
/// <c>catalog_version</c>, so a rule stamped at version 1 cannot be written before version 1 exists. The
/// baseline read hands them to the publish pipeline, and the one transaction of step 10 appends them after
/// the version row, which is the same order every other publish writes in
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/918">issue 918</see>, item 2).
/// </para>
/// <para>
/// <b>The family restore is the one place this backend turns IDENTITY off.</b> A bundle carries its family
/// ids and the import keeps them, which is what makes a row's family membership answerable afterwards, and an
/// identity column refuses an explicit value without <c>SET IDENTITY_INSERT</c>. It is scoped to the restore
/// and to the one table, inside the same transaction, so nothing else in the import can insert an id of its
/// own choosing.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// The families and the id marks are written BEFORE the publish, because the edits and the baseline both
    /// need them. Any refusal after that point takes that staging back, so a caller that catches one is
    /// holding a store it may import into again. It takes back the staging and NOTHING else, because the
    /// publish commits whole or not at all. Files a failed attempt already wrote to the pack store are
    /// ordinary orphans and the next sweep takes them.
    /// </remarks>
    public async Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        IReadOnlyList<ContentEdit> edits = await WriteAsync(
            async (scope, token) =>
            {
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                int published = await ReadIntAsync(
                    scope, "SELECT COUNT(*) FROM dbo.catalog_version;", token).ConfigureAwait(false);
                if (published > 0)
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"This store already stands at version {active}, and a bundle is imported into an EMPTY store only. A deployed catalog's values change through an edit and a publish and through nothing else."),
                        default,
                        0,
                        ContentAuthoringException.CatalogNotEmptyReason);
                }

                if (PackStore is null)
                {
                    throw NoPackStore(nameof(ImportBundleAsync));
                }

                RequireTypesAgree(bundle);

                await RestoreFamiliesAsync(scope, bundle, token).ConfigureAwait(false);
                await SeedMarksAsync(scope, bundle, token).ConfigureAwait(false);

                _importRules = Restamp(bundle);
                return await EditsAsync(scope, bundle, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await ApplyEditsAsync(edits, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
            ContentPublishResult published = await PublishAsync(
                new ContentPublishRequest(actor, operatorId, note, 0), cancellationToken).ConfigureAwait(false);

            await WriteAsync(
                (scope, token) => AppendAuditAsync(
                    scope,
                    ContentAuditActions.BulkImport,
                    actor,
                    operatorId,
                    default,
                    0,
                    default,
                    string.Empty,
                    null,
                    Render(bundle.Rows.Count),
                    published.VersionNumber,
                    note,
                    token),
                cancellationToken).ConfigureAwait(false);
            return published;
        }
        catch (ContentAuthoringException)
        {
            await ResetToEmptyAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _importRules = null;
        }
    }

    /// <inheritdoc />
    public Task<ContentBundle> ExportBundleAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            async (scope, token) =>
            {
                if (!await VersionExistsAsync(scope, versionNumber, token).ConfigureAwait(false))
                {
                    throw UnknownVersion(versionNumber);
                }

                var types = new List<ContentBundleType>();
                IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
                for (int i = 0; i < registrations.Count; i++)
                {
                    ContentTypeRegistration registration = registrations[i];
                    types.Add(new ContentBundleType(
                        registration.Type,
                        registration.TypeKey,
                        registration.DefaultVisibility,
                        registration.ChunkSlots,
                        registration.MaxDefinitionId,
                        registration.Schema));
                }

                IReadOnlyList<ContentFamily> families = await ReadFamiliesAsync(scope, default, null, token)
                    .ConfigureAwait(false);
                var familyKeys = new Dictionary<long, string>();
                for (int i = 0; i < families.Count; i++)
                {
                    familyKeys[families[i].FamilyId] = families[i].FamilyKey;
                }

                IReadOnlyList<ContentRowRevision> live = await ReadRevisionsAsync(
                    scope, default, null, versionNumber, token).ConfigureAwait(false);
                var rows = new List<ContentBundleRow>(live.Count);
                for (int i = 0; i < live.Count; i++)
                {
                    rows.Add(Export(live[i], familyKeys));
                }

                var rules = new List<RemapRule>();
                IReadOnlyList<RemapRule> all = await ReadRulesAsync(scope, token).ConfigureAwait(false);
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].IntroducedIn <= versionNumber)
                    {
                        rules.Add(all[i]);
                    }
                }

                string epoch;
                await using (SqlCommand command = Command(
                    scope, "SELECT store_epoch FROM dbo.catalog_metadata WHERE metadata_key = 1;"))
                {
                    epoch = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string
                        ?? throw NoMetadata();
                }

                return new ContentBundle(
                    ContentBundle.CurrentFormatVersion, epoch, versionNumber, types, rows, families, rules);
            },
            cancellationToken);

    /// <summary>
    /// One live row as a bundle carries it. The id is always NAMED on an export, because that is what makes
    /// an import reproduce the same ids and therefore makes an adoption a no-op for stored player data. A
    /// DERIVED marker field is skipped: an edit cannot author a value for one.
    /// </summary>
    ContentBundleRow Export(ContentRowRevision revision, Dictionary<long, string> familyKeys)
    {
        ContentRow row = revision.Row;
        IReadOnlyList<ContentFieldEntry> schema = RequireType(row.Type).Schema.Fields;
        var fields = new List<ContentFieldEdit>(schema.Count);
        for (int i = 0; i < schema.Count && i < row.Fields.Count; i++)
        {
            if (!schema[i].IsDerivedMarker)
            {
                fields.Add(new ContentFieldEdit(schema[i].Name, row.Fields[i]));
            }
        }

        string? familyKey = revision.FamilyId is long familyId && familyKeys.TryGetValue(familyId, out string? key)
            ? key
            : null;

        return new ContentBundleRow(row.Type, row.Id, row.Key, row.IsRetired, familyKey, fields);
    }

    /// <summary>
    /// Every bundle type must be registered HERE, under the same key and the same chunk slot count. A codec
    /// is code and no document can carry one, so an import registers nothing: it checks that the declaration
    /// the bundle was exported under and the one this process holds are the same declaration.
    /// </summary>
    void RequireTypesAgree(ContentBundle bundle)
    {
        for (int i = 0; i < bundle.Types.Count; i++)
        {
            ContentBundleType declared = bundle.Types[i];
            if (!_registry.TryGet(declared.Type, out ContentTypeRegistration? registration))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"The bundle carries content type {declared.Type.Value} '{declared.TypeKey}', which this process has not registered. A codec is code, so an import needs the type already declared against the codec that decodes it."),
                    declared.Type,
                    0,
                    ContentAuthoringException.UnknownTypeReason);
            }

            if (!string.Equals(registration.TypeKey, declared.TypeKey, StringComparison.Ordinal)
                || registration.ChunkSlots != declared.ChunkSlots)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"The bundle declares content type {declared.Type.Value} as '{declared.TypeKey}' with {declared.ChunkSlots} chunk slots and this process registers it as '{registration.TypeKey}' with {registration.ChunkSlots}. The two declarations disagree about where an id falls, so the import is refused whole."),
                    declared.Type,
                    0,
                    ContentAuthoringException.BundleFormatReason);
            }
        }
    }

    /// <summary>
    /// The families and their blocks VERBATIM, ids included, because a family is what makes a row's id
    /// membership answerable and an import that reallocated blocks would move every id in one. The identity
    /// column is turned off for the duration and back on afterwards.
    /// </summary>
    async Task RestoreFamiliesAsync(
        SqlServerCatalogScope scope,
        ContentBundle bundle,
        CancellationToken cancellationToken)
    {
        if (bundle.Families.Count == 0)
        {
            return;
        }

        await ExecuteAsync(scope, "SET IDENTITY_INSERT dbo.catalog_family ON;", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            for (int i = 0; i < bundle.Families.Count; i++)
            {
                ContentFamily family = bundle.Families[i];
                RequireType(family.Type);

                await using (SqlCommand insert = Command(
                    scope,
                    """
                    INSERT INTO dbo.catalog_family(
                        family_id, type_id, family_key, block_size, retired, created_in_version)
                    VALUES (@family, @type, @key, @size, @retired, @created);
                    """))
                {
                    Bind(insert, "@family", family.FamilyId);
                    Bind(insert, "@type", (int)family.Type.Value);
                    Bind(insert, "@key", family.FamilyKey);
                    Bind(insert, "@size", family.BlockSize);
                    Bind(insert, "@retired", family.IsRetired ? 1 : 0);
                    Bind(insert, "@created", family.CreatedInVersion);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                for (int b = 0; b < family.Blocks.Count; b++)
                {
                    ContentFamilyBlock block = family.Blocks[b];
                    await using SqlCommand insert = Command(
                        scope,
                        """
                        INSERT INTO dbo.catalog_family_block(
                            family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
                        VALUES (@family, @ordinal, @base, @size, @next, @version);
                        """);
                    Bind(insert, "@family", family.FamilyId);
                    Bind(insert, "@ordinal", block.BlockOrdinal);
                    Bind(insert, "@base", block.BaseId);
                    Bind(insert, "@size", block.BlockSize);
                    Bind(insert, "@next", block.NextFreeId);
                    Bind(insert, "@version", block.ReservedInVersion);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await ExecuteAsync(scope, "SET IDENTITY_INSERT dbo.catalog_family OFF;", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Both marks move to the top of every restored block and every carried id, so the plain counter cannot
    /// walk into a family's block or onto a row the bundle already named.
    /// <para>
    /// <b>It runs BEFORE the allocation rather than after it</b>, which is the one place this diverges from
    /// spec 6.3's written order, and it has to. A bundle may MIX named and unnamed ids, so an unnamed row
    /// allocated from a counter that has not yet seen the carried ids lands straight on one of them.
    /// </para>
    /// </summary>
    static async Task SeedMarksAsync(
        SqlServerCatalogScope scope,
        ContentBundle bundle,
        CancellationToken cancellationToken)
    {
        var highest = new Dictionary<ushort, int>();
        for (int i = 0; i < bundle.Families.Count; i++)
        {
            ContentFamily family = bundle.Families[i];
            for (int b = 0; b < family.Blocks.Count; b++)
            {
                Raise(highest, family.Type.Value, family.Blocks[b].TopExclusive - 1);
            }
        }

        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            if (row.Id is int id)
            {
                Raise(highest, row.Type.Value, id);
            }
        }

        foreach (KeyValuePair<ushort, int> mark in highest)
        {
            var type = new ContentTypeId(mark.Key);
            ContentIdHighWater held = await ReadHighWaterAsync(scope, type, cancellationToken)
                .ConfigureAwait(false);
            await WriteHighWaterAsync(
                scope,
                type,
                new ContentIdHighWater(
                    Math.Max(held.ReservedThrough, mark.Value), Math.Max(held.IssuedThrough, mark.Value)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The bundle's rules as the NEW line's, contiguous from 1 and introduced in version 1, which is what
    /// makes an import a republish rather than a restore. A lossless export is not a backup: the version
    /// LINE restarts, so a durable page stamped against the old line is newer than every rule there is.
    /// </summary>
    static IReadOnlyList<RemapRule> Restamp(ContentBundle bundle)
    {
        var rules = new List<RemapRule>(bundle.Rules.Count);
        for (int i = 0; i < bundle.Rules.Count; i++)
        {
            RemapRule rule = bundle.Rules[i];
            rules.Add(new RemapRule(i + 1, 1, rule.Type, rule.Kind, rule.FromId, rule.ToId, rule.Payload));
        }

        return rules;
    }

    /// <summary>One <c>Add</c> per bundle row, in the bundle's own order, which is the allocation order.</summary>
    static async Task<IReadOnlyList<ContentEdit>> EditsAsync(
        SqlServerCatalogScope scope,
        ContentBundle bundle,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentFamily> families = await ReadFamiliesAsync(
            scope, default, null, cancellationToken).ConfigureAwait(false);
        var byKey = new Dictionary<(ushort Type, string Key), long>();
        for (int i = 0; i < families.Count; i++)
        {
            byKey[(families[i].Type.Value, families[i].FamilyKey)] = families[i].FamilyId;
        }

        var edits = new List<ContentEdit>(bundle.Rows.Count);
        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow row = bundle.Rows[i];
            long? familyId = null;
            if (row.FamilyKey is string familyKey)
            {
                if (!byKey.TryGetValue((row.Type.Value, familyKey), out long held))
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"Bundle row '{row.Key}' of content type {row.Type.Value} names family '{familyKey}', which the bundle's own family list does not carry."),
                        row.Type,
                        row.Id ?? 0,
                        ContentAuthoringException.UnknownFamilyReason);
                }

                familyId = held;
            }

            edits.Add(ContentEdit.Import(
                row.Type, row.Id ?? 0, row.Key, row.Fields, familyId, row.IsRetired));
        }

        return edits;
    }

    /// <summary>
    /// The empty state the import was required to start from, narrowed to the tables the import writes
    /// BEFORE the commit. A refused import leaves a store a caller may import into again rather than one
    /// carrying half a bundle. The audit is KEPT: a refused import is a thing that happened and the trace of
    /// it is the point.
    /// <para>
    /// <b>It deletes from five tables and no more, because the commit is atomic.</b> The version, row, row
    /// field, chunk and rule tables are written only inside step 10's one transaction, which either commits
    /// whole or not at all, and an import that reaches here was refused before that transaction opened or
    /// inside it. Either way those five tables still hold what they held when the import started, which on
    /// the empty store an import is licensed into is nothing, and the active and pinned pointers have not
    /// moved either. The families, the id marks and the draft are the only things staged ahead of the
    /// commit, so they are the only things there is anything to take back from. A <c>DELETE</c> against
    /// <c>catalog_remap_rule</c> would also be the one statement in this provider that mutates an
    /// append-only table.
    /// </para>
    /// </summary>
    Task ResetToEmptyAsync(CancellationToken cancellationToken)
        => WriteAsync(
            async (scope, token) =>
            {
                // Child first, so no delete trips a foreign key on the way down. The edit fields go with the
                // edits through the cascade the schema declares.
                string[] statements =
                [
                    "DELETE FROM dbo.catalog_draft_edit;",
                    "DELETE FROM dbo.catalog_draft;",
                    "DELETE FROM dbo.catalog_family_block;",
                    "DELETE FROM dbo.catalog_family;",
                    "DELETE FROM dbo.catalog_id_high_water;",
                ];

                for (int i = 0; i < statements.Length; i++)
                {
                    await ExecuteAsync(scope, statements[i], token).ConfigureAwait(false);
                }
            },
            cancellationToken);

    static async Task ExecuteAsync(
        SqlServerCatalogScope scope,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(scope, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static void Raise(Dictionary<ushort, int> highest, ushort typeId, int candidate)
        => highest[typeId] = highest.TryGetValue(typeId, out int held) ? Math.Max(held, candidate) : candidate;
}
