using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// Every schema object the current version declares, by NAME, as five sets: the tables, the named indexes (the primary
/// keys among them, since a primary key IS an index in <c>sys.indexes</c>), the check constraints, the
/// foreign keys and the default constraints.
/// <para>
/// <b>Names are the whole mechanism, which is why the DDL names every constraint.</b> SQL Server generates a
/// name for an unnamed constraint, and a generated name differs per database, so a schema built from unnamed
/// constraints cannot be compared against anything. An index or a constraint is keyed
/// <c>table.object</c> here, so a correctly named object hanging off the wrong table is caught too.
/// </para>
/// <para>
/// The <c>V1</c> sets at the bottom are the same lists minus the ledger table version 2 adds, which is what a
/// version 1 database is validated against before it is migrated.
/// </para>
/// <para>
/// These lists are TRANSCRIBED from <c>CatalogSchemaV2.sql</c>, and <c>SqlServerCatalogSchemaDriftTests</c>
/// parses that file and asserts set equality against every one of them without needing an instance. Drift is
/// also what
/// <c>SqlServerCatalogSchemaTests</c>'s AutoCreate case exists to catch: it creates the schema from the file
/// and then validates it against these, so a constraint added to one and not the other goes red on the first
/// run against a live instance.
/// </para>
/// </summary>
internal static class SqlServerCatalogSchemaExpectations
{
    /// <summary>The prefix every object version 2 adds is named with, which is how the version 1 sets are derived.</summary>
    const string UpgradeLedger = "catalog_content_upgrade";

    /// <summary>The fourteen tables of spec 4.3, plus the content upgrade ledger version 2 adds.</summary>
    internal static IReadOnlySet<string> Tables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        UpgradeLedger,
        "catalog_metadata",
        "catalog_type",
        "catalog_version",
        "catalog_family",
        "catalog_family_block",
        "catalog_row",
        "catalog_row_field",
        "catalog_id_high_water",
        "catalog_draft",
        "catalog_draft_edit",
        "catalog_draft_edit_field",
        "catalog_audit",
        "catalog_remap_rule",
        "catalog_chunk",
    };

    /// <summary>
    /// <see cref="Tables"/> as a T-SQL literal list for an <c>IN</c> clause, sorted so the statement reads
    /// the same on every run.
    /// <para>
    /// <b>This is what a DROP names, rather than a <c>LIKE</c> pattern.</b> A host is entitled to keep its own
    /// tables in the catalog's database, and a table it named <c>catalog_overrides_by_host</c> matches every
    /// name rule an engine could write while belonging to nobody here. Naming the inventory means a drop
    /// destroys exactly the tables above and cannot reach anything else, and the set is pinned against
    /// <c>CatalogSchemaV1.sql</c> by <c>SqlServerCatalogSchemaDriftTests</c> without an instance.
    /// </para>
    /// </summary>
    internal static string TableNameList { get; } = BuildTableNameList();

    static string BuildTableNameList()
    {
        var names = new List<string>(Tables);
        names.Sort(StringComparer.Ordinal);
        var builder = new StringBuilder(names.Count * 24);
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append("N'").Append(names[i]).Append('\'');
        }

        return builder.ToString();
    }

    /// <summary>Every named index, primary keys included, as <c>table.index</c>.</summary>
    internal static IReadOnlySet<string> Indexes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "catalog_audit.ix_catalog_audit_target",
        "catalog_audit.ix_catalog_audit_time",
        "catalog_audit.pk_catalog_audit",
        "catalog_chunk.ix_catalog_chunk_hash",
        "catalog_chunk.pk_catalog_chunk",
        "catalog_content_upgrade.ix_catalog_content_upgrade_order",
        "catalog_content_upgrade.pk_catalog_content_upgrade",
        "catalog_draft.pk_catalog_draft",
        "catalog_draft_edit.pk_catalog_draft_edit",
        "catalog_draft_edit.ux_catalog_draft_edit_target",
        "catalog_draft_edit_field.pk_catalog_draft_edit_field",
        "catalog_family.pk_catalog_family",
        "catalog_family.ux_catalog_family_key",
        "catalog_family_block.pk_catalog_family_block",
        "catalog_id_high_water.pk_catalog_id_high_water",
        "catalog_metadata.pk_catalog_metadata",
        "catalog_remap_rule.pk_catalog_remap_rule",
        "catalog_row.ix_catalog_row_changed",
        "catalog_row.ix_catalog_row_key",
        "catalog_row.ix_catalog_row_live",
        "catalog_row.pk_catalog_row",
        "catalog_row_field.pk_catalog_row_field",
        "catalog_type.pk_catalog_type",
        "catalog_type.ux_catalog_type_key",
        "catalog_version.pk_catalog_version",
    };

    /// <summary>
    /// Every foreign key, as <c>table.constraint</c>. A foreign key is the only thing keeping a chunk row
    /// pointing at a version that exists, so a schema missing one accepts writes this build assumes cannot
    /// happen.
    /// </summary>
    internal static IReadOnlySet<string> ForeignKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "catalog_chunk.fk_catalog_chunk_type",
        "catalog_chunk.fk_catalog_chunk_version",
        "catalog_draft_edit.fk_catalog_draft_edit_type",
        "catalog_draft_edit_field.fk_catalog_draft_edit_field_edit",
        "catalog_family.fk_catalog_family_type",
        "catalog_family_block.fk_catalog_family_block_family",
        "catalog_id_high_water.fk_catalog_id_high_water_type",
        "catalog_remap_rule.fk_catalog_remap_rule_type",
        "catalog_remap_rule.fk_catalog_remap_rule_version",
        "catalog_row.fk_catalog_row_family",
        "catalog_row.fk_catalog_row_type",
        "catalog_row.fk_catalog_row_version",
        "catalog_row_field.fk_catalog_row_field_row",
    };

    /// <summary>
    /// Every default constraint, as <c>table.constraint</c>. A missing default turns an insert that omits
    /// the column into a NULL in a NOT NULL column, which fails at run time on a schema that validated.
    /// </summary>
    internal static IReadOnlySet<string> Defaults { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "catalog_audit.df_catalog_audit_definition",
        "catalog_audit.df_catalog_audit_field",
        "catalog_audit.df_catalog_audit_key",
        "catalog_audit.df_catalog_audit_note",
        "catalog_audit.df_catalog_audit_operator",
        "catalog_audit.df_catalog_audit_type",
        "catalog_audit.df_catalog_audit_version",
        "catalog_content_upgrade.df_catalog_content_upgrade_operator",
        "catalog_draft.df_catalog_draft_note",
        "catalog_draft_edit.df_catalog_draft_edit_definition",
        "catalog_draft_edit.df_catalog_draft_edit_imported",
        "catalog_draft_edit.df_catalog_draft_edit_policy",
        "catalog_draft_edit.df_catalog_draft_edit_replacement",
        "catalog_family.df_catalog_family_retired",
        "catalog_metadata.df_catalog_metadata_active",
        "catalog_remap_rule.df_catalog_remap_rule_payload",
        "catalog_remap_rule.df_catalog_remap_rule_to",
        "catalog_row.df_catalog_row_parent",
        "catalog_row.df_catalog_row_retired",
    };

    /// <summary>Every check constraint, as <c>table.constraint</c>.</summary>
    internal static IReadOnlySet<string> Checks { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "catalog_audit.ck_catalog_audit_action",
        "catalog_audit.ck_catalog_audit_actor",
        "catalog_audit.ck_catalog_audit_after",
        "catalog_audit.ck_catalog_audit_before",
        "catalog_audit.ck_catalog_audit_field",
        "catalog_audit.ck_catalog_audit_key",
        "catalog_audit.ck_catalog_audit_note",
        "catalog_audit.ck_catalog_audit_operator",
        "catalog_chunk.ck_catalog_chunk_hash",
        "catalog_chunk.ck_catalog_chunk_index",
        "catalog_chunk.ck_catalog_chunk_rows",
        "catalog_chunk.ck_catalog_chunk_stored",
        "catalog_chunk.ck_catalog_chunk_uncompressed",
        "catalog_chunk.ck_catalog_chunk_visibility",
        "catalog_content_upgrade.ck_catalog_content_upgrade_actor",
        "catalog_content_upgrade.ck_catalog_content_upgrade_disposition",
        "catalog_content_upgrade.ck_catalog_content_upgrade_id",
        "catalog_content_upgrade.ck_catalog_content_upgrade_operator",
        "catalog_content_upgrade.ck_catalog_content_upgrade_order",
        "catalog_content_upgrade.ck_catalog_content_upgrade_version",
        "catalog_draft.ck_catalog_draft_base",
        "catalog_draft.ck_catalog_draft_frozen",
        "catalog_draft.ck_catalog_draft_key",
        "catalog_draft.ck_catalog_draft_note",
        "catalog_draft.ck_catalog_draft_opened_by",
        "catalog_draft_edit.ck_catalog_draft_edit_definition",
        "catalog_draft_edit.ck_catalog_draft_edit_edited_by",
        "catalog_draft_edit.ck_catalog_draft_edit_fork_flag",
        "catalog_draft_edit.ck_catalog_draft_edit_fork_key",
        "catalog_draft_edit.ck_catalog_draft_edit_imported",
        "catalog_draft_edit.ck_catalog_draft_edit_key",
        "catalog_draft_edit.ck_catalog_draft_edit_operation",
        "catalog_draft_edit.ck_catalog_draft_edit_policy",
        "catalog_draft_edit.ck_catalog_draft_edit_replacement",
        "catalog_draft_edit_field.ck_catalog_draft_edit_field_blob",
        "catalog_draft_edit_field.ck_catalog_draft_edit_field_kind",
        "catalog_draft_edit_field.ck_catalog_draft_edit_field_name",
        "catalog_draft_edit_field.ck_catalog_draft_edit_field_text",
        "catalog_family.ck_catalog_family_block_size",
        "catalog_family.ck_catalog_family_created",
        "catalog_family.ck_catalog_family_key",
        "catalog_family.ck_catalog_family_retired",
        "catalog_family_block.ck_catalog_family_block_alignment",
        "catalog_family_block.ck_catalog_family_block_base",
        "catalog_family_block.ck_catalog_family_block_block_size",
        "catalog_family_block.ck_catalog_family_block_next",
        "catalog_family_block.ck_catalog_family_block_ordinal",
        "catalog_family_block.ck_catalog_family_block_top",
        "catalog_family_block.ck_catalog_family_block_version",
        "catalog_id_high_water.ck_catalog_id_high_water_issued",
        "catalog_id_high_water.ck_catalog_id_high_water_reserved",
        "catalog_metadata.ck_catalog_metadata_active",
        "catalog_metadata.ck_catalog_metadata_epoch",
        "catalog_metadata.ck_catalog_metadata_key",
        "catalog_metadata.ck_catalog_metadata_pinned",
        "catalog_metadata.ck_catalog_metadata_version",
        "catalog_remap_rule.ck_catalog_remap_rule_from",
        "catalog_remap_rule.ck_catalog_remap_rule_introduced",
        "catalog_remap_rule.ck_catalog_remap_rule_kind",
        "catalog_remap_rule.ck_catalog_remap_rule_payload",
        "catalog_remap_rule.ck_catalog_remap_rule_sequence",
        "catalog_remap_rule.ck_catalog_remap_rule_to",
        "catalog_row.ck_catalog_row_definition",
        "catalog_row.ck_catalog_row_key",
        "catalog_row.ck_catalog_row_parent",
        "catalog_row.ck_catalog_row_replaced",
        "catalog_row.ck_catalog_row_retired",
        "catalog_row.ck_catalog_row_valid_from",
        "catalog_row_field.ck_catalog_row_field_blob",
        "catalog_row_field.ck_catalog_row_field_kind",
        "catalog_row_field.ck_catalog_row_field_name",
        "catalog_row_field.ck_catalog_row_field_text",
        "catalog_type.ck_catalog_type_ceiling",
        "catalog_type.ck_catalog_type_first_seen",
        "catalog_type.ck_catalog_type_id",
        "catalog_type.ck_catalog_type_key",
        "catalog_type.ck_catalog_type_slots",
        "catalog_type.ck_catalog_type_visibility",
        "catalog_version.ck_catalog_version_base",
        "catalog_version.ck_catalog_version_client_hash",
        "catalog_version.ck_catalog_version_generation",
        "catalog_version.ck_catalog_version_min_client",
        "catalog_version.ck_catalog_version_min_server",
        "catalog_version.ck_catalog_version_note",
        "catalog_version.ck_catalog_version_number",
        "catalog_version.ck_catalog_version_published_by",
        "catalog_version.ck_catalog_version_server_hash",
    };

    /// <summary>The tables version 1 declared, which is version 2's set without the ledger.</summary>
    internal static IReadOnlySet<string> TablesV1 { get; } = WithoutTheLedger(Tables);

    /// <summary>The named indexes version 1 declared.</summary>
    internal static IReadOnlySet<string> IndexesV1 { get; } = WithoutTheLedger(Indexes);

    /// <summary>The foreign keys version 1 declared, which version 2 does not add to.</summary>
    internal static IReadOnlySet<string> ForeignKeysV1 { get; } = WithoutTheLedger(ForeignKeys);

    /// <summary>The default constraints version 1 declared.</summary>
    internal static IReadOnlySet<string> DefaultsV1 { get; } = WithoutTheLedger(Defaults);

    /// <summary>The check constraints version 1 declared.</summary>
    internal static IReadOnlySet<string> ChecksV1 { get; } = WithoutTheLedger(Checks);

    /// <summary>
    /// One set minus every object of the ledger table, which is the WHOLE of what version 2 added. Deriving
    /// the version 1 sets rather than transcribing them a second time is what keeps a database this build
    /// refuses to migrate from being one that merely drifted from a stale copy of the old list.
    /// </summary>
    /// <param name="names">A version 2 name set.</param>
    static IReadOnlySet<string> WithoutTheLedger(IReadOnlySet<string> names)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!name.StartsWith(UpgradeLedger, StringComparison.Ordinal))
            {
                kept.Add(name);
            }
        }

        return kept;
    }
}
