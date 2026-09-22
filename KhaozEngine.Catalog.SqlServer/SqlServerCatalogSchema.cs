using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The content authoring schema as SQL Server holds it (spec 4.5): the fifteen tables of spec 4.4 and the
/// content upgrade ledger, with the type and constraint idioms swapped, shipped as the EMBEDDED RESOURCE
/// <c>CatalogSchemaV2.sql</c>, plus the version and migration this build supports.
/// <para>
/// This file holds the two constants and the resource load and NOTHING else. Creating and validating a
/// database against it is <see cref="SqlServerCatalogSchemaValidation"/>, the same split the SQLite provider
/// and the journal both carry, and the DDL itself is a file rather than a <c>const string</c> because that is
/// the shape <c>KhaozEngine.WorldStore.SqlServer/JournalSchemaV1.sql</c> established.
/// </para>
/// <para>
/// <b>Every constraint in that file is NAMED.</b> An unnamed constraint gets a generated name, and validation
/// compares names against <c>sys.check_constraints</c>, so an unnamed one would be unverifiable by
/// construction. Every key column is
/// <c>nvarchar(N) COLLATE Latin1_General_100_BIN2</c>, because comparison is ordinal always and a
/// case-insensitive database default silently merged two accounts once (contracts 5.3). Every size cap is a
/// named <c>CHECK</c>, <c>LEN</c> for text and <c>DATALENGTH</c> for binary.
/// </para>
/// <para>
/// <b>There is no <c>UPDATE</c> and no <c>DELETE</c> for <c>catalog_remap_rule</c> anywhere in this
/// provider</b>, and no <c>BEFORE DELETE</c> trigger standing in for one. Rules are append only
/// (contracts 8.1). The journal takes the stronger position with a delete guard trigger, and this schema
/// deliberately does not copy it: that guard exists to permit a retention sweep and there is no retention
/// sweep here. A later phase that adds a maintenance path over rules adds the trigger with it.
/// </para>
/// <para>
/// The one column beyond spec 4.4 is <c>catalog_draft_edit.imported_retired</c>, which the SQLite provider
/// added for the same reason and which is filed against the spec as
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/918">issue 918</see>. A bundle carries every
/// live row including the retired ones, and a lossless import reproduces one through an <c>Add</c> edit that
/// is already retired. With no column for that bit, a draft written to the database and read back would
/// publish every imported retired row as live.
/// </para>
/// </summary>
internal static class SqlServerCatalogSchema
{
    /// <summary>The schema version this build writes and the only one it accepts.</summary>
    internal const int CurrentVersion = 2;

    /// <summary>The migration an operator is told to apply when the database does not match.</summary>
    internal const string RequiredMigration = "catalog-v2-content-upgrade-ledger";

    /// <summary>The whole schema, as one batch, exactly as the embedded file gives it.</summary>
    internal static string SchemaSql { get; } = Load("CatalogSchemaV2.sql");

    /// <summary>
    /// Version 1's script, kept EMBEDDED beside version 2's rather than deleted. It is what a test builds a
    /// version 1 database from, and it is the operator-facing record of the shape the migration below moves,
    /// which is the same pair <c>KhaozEngine.WorldStore.SqlServer</c> ships for the journal.
    /// </summary>
    internal static string VersionOneSchemaSql { get; } = Load("CatalogSchemaV1.sql");

    /// <summary>
    /// The version 1 to version 2 migration as one statement per batch: the ledger table, its index, and the
    /// metadata row moved to 2. Nothing else is touched, so the rows, the history, the audit, the open draft,
    /// the pin and the store epoch all survive it unchanged.
    /// <para>
    /// It is TRANSCRIBED from <c>CatalogSchemaV2.sql</c>'s table rather than generated from it, and
    /// <c>SqlServerCatalogSchemaDriftTests</c> is what keeps the two saying the same thing: a migrated
    /// database is validated against the same name sets a freshly created one is.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> VersionOneMigrationSql { get; } = Array.AsReadOnly(new[]
    {
        """
        CREATE TABLE dbo.catalog_content_upgrade (
            upgrade_id nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            upgrade_order int NOT NULL,
            disposition nvarchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
            version_number int NOT NULL,
            actor nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [operator] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_catalog_content_upgrade_operator DEFAULT N'',
            recorded_at_utc datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_catalog_content_upgrade PRIMARY KEY (upgrade_id),
            CONSTRAINT ck_catalog_content_upgrade_id CHECK (LEN(upgrade_id) BETWEEN 1 AND 128),
            CONSTRAINT ck_catalog_content_upgrade_order CHECK (upgrade_order >= 1),
            CONSTRAINT ck_catalog_content_upgrade_disposition CHECK (disposition IN (N'applied', N'adopted', N'baseline')),
            CONSTRAINT ck_catalog_content_upgrade_version CHECK (version_number >= 0),
            CONSTRAINT ck_catalog_content_upgrade_actor CHECK (LEN(actor) BETWEEN 1 AND 128),
            CONSTRAINT ck_catalog_content_upgrade_operator CHECK (LEN([operator]) <= 128));
        """,
        """
        CREATE INDEX ix_catalog_content_upgrade_order ON dbo.catalog_content_upgrade(upgrade_order, upgrade_id);
        """,
        """
        UPDATE dbo.catalog_metadata
        SET schema_version = 2,
            updated_at_utc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
        WHERE metadata_key = 1 AND schema_version = 1;
        """,
    });

    static string Load(string suffix)
    {
        Assembly assembly = typeof(SqlServerCatalogSchema).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded SQL Server content catalog schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
