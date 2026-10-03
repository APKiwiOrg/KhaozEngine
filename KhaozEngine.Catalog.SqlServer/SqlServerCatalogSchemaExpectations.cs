using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// Every schema object the current version declares, by NAME, as five sets: the tables, the named indexes (the primary
/// keys among them, since a primary key IS an index in <c>sys.indexes</c>), the check constraints, the
/// foreign keys and the default constraints. A sixth set holds the row time columns with their types, which is
/// the whole of what version 3 changed. Version 4 adds the four text tables with their objects and two
/// nullable columns, each with a named check.
/// <para>
/// <b>Names are the whole mechanism, which is why the DDL names every constraint.</b> SQL Server generates a
/// name for an unnamed constraint, and a generated name differs per database, so a schema built from unnamed
/// constraints cannot be compared against anything. An index or a constraint is keyed
/// <c>table.object</c> here, so a correctly named object hanging off the wrong table is caught too.
/// </para>
/// <para>
/// The <c>V3</c> sets at the bottom are the same lists minus every version 4 object, which is what a version 3
/// database is validated against before it is migrated. Version 3 added no named object, so a version 2 database
/// is validated against the version 3 name sets and only its row time columns differ. The <c>V1</c> sets are the
/// <c>V3</c> sets minus the ledger table version 2 adds.
/// </para>
/// <para>
/// These lists are TRANSCRIBED from <c>CatalogSchemaV4.sql</c>, and <c>SqlServerCatalogSchemaDriftTests</c>
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

    /// <summary>
    /// The fourteen tables of spec 4.3, plus the content upgrade ledger version 2 adds and the four text tables
    /// version 4 adds.
    /// </summary>
    internal static IReadOnlySet<string> Tables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        UpgradeLedger,
        "catalog_draft_text_edit",
        "catalog_draft_text_language",
        "catalog_text",
        "catalog_text_chunk",
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
    /// <c>CatalogSchemaV4.sql</c> by <c>SqlServerCatalogSchemaDriftTests</c> without an instance. Every older
    /// version's tables are a subset of it, so an older catalog is dropped by the same list.
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
        "catalog_draft_text_edit.pk_catalog_draft_text_edit",
        "catalog_draft_text_edit.ux_catalog_draft_text_edit_target",
        "catalog_draft_text_language.pk_catalog_draft_text_language",
        "catalog_draft_text_language.ux_catalog_draft_text_language_tag",
        "catalog_draft_text_language.ux_catalog_draft_text_language_wire",
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
        "catalog_text.ix_catalog_text_version",
        "catalog_text.pk_catalog_text",
        "catalog_text.ux_catalog_text_live",
        "catalog_text_chunk.ix_catalog_text_chunk_hash",
        "catalog_text_chunk.pk_catalog_text_chunk",
        "catalog_text_chunk.ux_catalog_text_chunk_wire",
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
        "catalog_draft_text_edit.fk_catalog_draft_text_edit_type",
        "catalog_family.fk_catalog_family_type",
        "catalog_family_block.fk_catalog_family_block_family",
        "catalog_id_high_water.fk_catalog_id_high_water_type",
        "catalog_remap_rule.fk_catalog_remap_rule_type",
        "catalog_remap_rule.fk_catalog_remap_rule_version",
        "catalog_row.fk_catalog_row_family",
        "catalog_row.fk_catalog_row_type",
        "catalog_row.fk_catalog_row_version",
        "catalog_row_field.fk_catalog_row_field_row",
        "catalog_text.fk_catalog_text_type",
        "catalog_text.fk_catalog_text_version",
        "catalog_text_chunk.fk_catalog_text_chunk_version",
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
        "catalog_audit.ck_catalog_audit_language",
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
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_edited_by",
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_field",
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_key",
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_language",
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_operation",
        "catalog_draft_text_edit.ck_catalog_draft_text_edit_value",
        "catalog_draft_text_language.ck_catalog_draft_text_language_tag",
        "catalog_draft_text_language.ck_catalog_draft_text_language_wire",
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
        "catalog_text.ck_catalog_text_definition",
        "catalog_text.ck_catalog_text_field",
        "catalog_text.ck_catalog_text_language",
        "catalog_text.ck_catalog_text_replaced",
        "catalog_text.ck_catalog_text_valid_from",
        "catalog_text_chunk.ck_catalog_text_chunk_hash",
        "catalog_text_chunk.ck_catalog_text_chunk_language",
        "catalog_text_chunk.ck_catalog_text_chunk_wire",
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
        "catalog_version.ck_catalog_version_text_complete",
    };

    /// <summary>The tables versions 2 and 3 declared: version 4's without the text tables.</summary>
    internal static IReadOnlySet<string> TablesV3 { get; } = WithoutVersionFour(Tables);

    /// <summary>The named indexes versions 2 and 3 declared.</summary>
    internal static IReadOnlySet<string> IndexesV3 { get; } = WithoutVersionFour(Indexes);

    /// <summary>The foreign keys versions 2 and 3 declared.</summary>
    internal static IReadOnlySet<string> ForeignKeysV3 { get; } = WithoutVersionFour(ForeignKeys);

    /// <summary>The default constraints versions 2 and 3 declared, which version 4 does not add to.</summary>
    internal static IReadOnlySet<string> DefaultsV3 { get; } = WithoutVersionFour(Defaults);

    /// <summary>The check constraints versions 2 and 3 declared: without the text tables' and the two column checks.</summary>
    internal static IReadOnlySet<string> ChecksV3 { get; } = WithoutVersionFour(Checks);

    /// <summary>The tables version 1 declared, which is version 3's set without the ledger.</summary>
    internal static IReadOnlySet<string> TablesV1 { get; } = WithoutTheLedger(TablesV3);

    /// <summary>The named indexes version 1 declared.</summary>
    internal static IReadOnlySet<string> IndexesV1 { get; } = WithoutTheLedger(IndexesV3);

    /// <summary>The foreign keys version 1 declared, which version 2 does not add to.</summary>
    internal static IReadOnlySet<string> ForeignKeysV1 { get; } = WithoutTheLedger(ForeignKeysV3);

    /// <summary>The default constraints version 1 declared.</summary>
    internal static IReadOnlySet<string> DefaultsV1 { get; } = WithoutTheLedger(DefaultsV3);

    /// <summary>The check constraints version 1 declared.</summary>
    internal static IReadOnlySet<string> ChecksV1 { get; } = WithoutTheLedger(ChecksV3);

    /// <summary>The tables the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">No catalog schema has this version.</exception>
    internal static IReadOnlySet<string> TablesFor(int version) => For(version, TablesV1, TablesV3, Tables);

    /// <summary>The named indexes the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    internal static IReadOnlySet<string> IndexesFor(int version) => For(version, IndexesV1, IndexesV3, Indexes);

    /// <summary>The foreign keys the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    internal static IReadOnlySet<string> ForeignKeysFor(int version)
        => For(version, ForeignKeysV1, ForeignKeysV3, ForeignKeys);

    /// <summary>The default constraints the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    internal static IReadOnlySet<string> DefaultsFor(int version) => For(version, DefaultsV1, DefaultsV3, Defaults);

    /// <summary>The check constraints the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    internal static IReadOnlySet<string> ChecksFor(int version) => For(version, ChecksV1, ChecksV3, Checks);

    /// <summary>
    /// Every row time column version 3 declares, as <c>table.column|type|scale|nullability</c>: each
    /// <c>created_at_utc</c> and <c>updated_at_utc</c> in the schema. Only <c>catalog_metadata.updated_at_utc</c>
    /// is older than version 3, and it alone is <c>NOT NULL</c>. Every column version 3 adds is nullable, because a
    /// legacy row takes a time only where the migration can prove one.
    /// <para>
    /// The other columns are not compared here, as they never were. Version 3 changed only these, and comparing
    /// them is what refuses a database that claims version 3 without them.
    /// </para>
    /// </summary>
    internal static IReadOnlySet<string> TimeColumnsV3 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TimeColumn("catalog_metadata", "updated_at_utc", nullable: false),
        TimeColumn("catalog_metadata", "created_at_utc"),
        TimeColumn("catalog_type", "created_at_utc"),
        TimeColumn("catalog_type", "updated_at_utc"),
        TimeColumn("catalog_family", "created_at_utc"),
        TimeColumn("catalog_family_block", "created_at_utc"),
        TimeColumn("catalog_family_block", "updated_at_utc"),
        TimeColumn("catalog_row", "created_at_utc"),
        TimeColumn("catalog_row", "updated_at_utc"),
        TimeColumn("catalog_row_field", "created_at_utc"),
        TimeColumn("catalog_id_high_water", "created_at_utc"),
        TimeColumn("catalog_id_high_water", "updated_at_utc"),
        TimeColumn("catalog_draft", "updated_at_utc"),
        TimeColumn("catalog_draft_edit", "created_at_utc"),
        TimeColumn("catalog_draft_edit_field", "created_at_utc"),
        TimeColumn("catalog_remap_rule", "created_at_utc"),
        TimeColumn("catalog_chunk", "created_at_utc"),
    };

    /// <summary>
    /// Every row time column version 4 declares: version 3's and the text tables' own. The text tables start
    /// empty on a migrated catalog and every row in them is written by this build, so their columns are
    /// <c>NOT NULL</c>.
    /// </summary>
    internal static IReadOnlySet<string> TimeColumns { get; } = new HashSet<string>(TimeColumnsV3, StringComparer.Ordinal)
    {
        TimeColumn("catalog_draft_text_edit", "created_at_utc", nullable: false),
        TimeColumn("catalog_draft_text_edit", "updated_at_utc", nullable: false),
        TimeColumn("catalog_draft_text_language", "created_at_utc", nullable: false),
        TimeColumn("catalog_text", "created_at_utc", nullable: false),
        TimeColumn("catalog_text", "updated_at_utc", nullable: false),
        TimeColumn("catalog_text_chunk", "created_at_utc", nullable: false),
    };

    /// <summary>The columns the version 3 migration adds, each in the only shape version 3 accepts.</summary>
    static readonly HashSet<string> AddedTimeColumns = AddedByVersionThree();

    /// <summary>The row time columns version 2 declared: version 3's without the ones the migration adds.</summary>
    internal static IReadOnlySet<string> TimeColumnsV2 { get; } = WithoutAdded(TimeColumnsV3);

    /// <summary>The row time columns version 1 declared, which version 2 did not add to.</summary>
    internal static IReadOnlySet<string> TimeColumnsV1 { get; } = TimeColumnsV2;

    /// <summary>The row time columns the given schema version declares.</summary>
    /// <param name="version">A schema version this build can validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">No catalog schema has this version.</exception>
    internal static IReadOnlySet<string> TimeColumnsFor(int version)
        => version switch
        {
            1 => TimeColumnsV1,
            2 => TimeColumnsV2,
            3 => TimeColumnsV3,
            4 => TimeColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "No catalog schema has this version."),
        };

    /// <summary>
    /// The row time columns of <paramref name="actual"/> a version is judged on. A version 2 database may already
    /// carry some of its version 3 columns, in exactly their version 3 shape, when the column adds ran without the
    /// version move, and the migration then adds only what is missing, so those are left out of the comparison.
    /// A column in any other shape stays in and is refused.
    /// </summary>
    /// <param name="actual">The row time columns the database holds.</param>
    /// <param name="version">The schema version the database says it is at.</param>
    internal static IReadOnlySet<string> JudgedTimeColumns(IReadOnlySet<string> actual, int version)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return version == 2 ? WithoutAdded(actual) : actual;
    }

    /// <summary>Whether <paramref name="actual"/> is the row time column set of <paramref name="version"/>.</summary>
    /// <param name="actual">The row time columns the database holds.</param>
    /// <param name="version">The schema version the database says it is at.</param>
    internal static bool TimeColumnsMatch(IReadOnlySet<string> actual, int version)
        => JudgedTimeColumns(actual, version).SetEquals(TimeColumnsFor(version));

    /// <summary>One row time column in the form validation reads it back in.</summary>
    /// <param name="table">The table.</param>
    /// <param name="column">The column.</param>
    /// <param name="nullable">Whether the column accepts NULL.</param>
    internal static string TimeColumn(string table, string column, bool nullable = true)
        => table + "." + column + "|datetimeoffset|7|" + (nullable ? "NULL" : "NOT NULL");

    static HashSet<string> AddedByVersionThree()
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string table, string column) in SqlServerCatalogSchema.VersionThreeColumns)
        {
            added.Add(TimeColumn(table, column));
        }

        return added;
    }

    static HashSet<string> WithoutAdded(IReadOnlySet<string> columns)
    {
        var kept = new HashSet<string>(columns, StringComparer.Ordinal);
        kept.ExceptWith(AddedTimeColumns);
        return kept;
    }

    /// <summary>One version's name set: version 1's, versions 2 and 3's, or version 4's.</summary>
    static IReadOnlySet<string> For(
        int version,
        IReadOnlySet<string> versionOne,
        IReadOnlySet<string> versionThree,
        IReadOnlySet<string> versionFour)
        => version switch
        {
            1 => versionOne,
            2 or 3 => versionThree,
            4 => versionFour,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "No catalog schema has this version."),
        };

    /// <summary>
    /// One set minus every object version 4 added: everything on a text table, and the checks on the two columns
    /// it added to <c>catalog_version</c> and <c>catalog_audit</c>. Deriving the older sets keeps them from
    /// drifting from a stale second copy, as <see cref="WithoutTheLedger"/> does for version 1.
    /// </summary>
    /// <param name="names">A version 4 name set, bare tables or <c>table.object</c>.</param>
    static IReadOnlySet<string> WithoutVersionFour(IReadOnlySet<string> names)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string table = dot < 0 ? name : name[..dot];
            bool textTable = false;
            foreach (string added in SqlServerCatalogSchema.VersionFourTables)
            {
                textTable |= string.Equals(table, added, StringComparison.Ordinal);
            }

            if (!textTable
                && name is not ("catalog_version.ck_catalog_version_text_complete" or "catalog_audit.ck_catalog_audit_language"))
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    /// <summary>
    /// One set minus every object of the ledger table, which is the WHOLE of what version 2 added. Deriving
    /// the version 1 sets rather than transcribing them a second time is what keeps a database this build
    /// refuses to migrate from being one that merely drifted from a stale copy of the old list.
    /// </summary>
    /// <param name="names">A version 3 name set.</param>
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
