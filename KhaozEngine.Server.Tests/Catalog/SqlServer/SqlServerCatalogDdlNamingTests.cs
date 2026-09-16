using System;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The embedded <c>CatalogSchemaV1.sql</c> read as text for the naming rules SQL Server enforces at CREATE
/// time, so a script that cannot be applied to an instance AT ALL goes red on a plain <c>dotnet test</c>.
/// <para>
/// <b>Why this is not part of the drift test.</b> <c>SqlServerCatalogSchemaDriftTests</c> compares the DDL
/// against the transcribed expectations as SETS, so a name declared twice collapses to one entry on each
/// side and the two sides still agree. That is how <c>ck_catalog_family_block_size</c> came to sit on both
/// <c>catalog_family</c> and <c>catalog_family_block</c> with every test in this project green: the first
/// live run failed on the second <c>CREATE TABLE</c> with "There is already an object named", and every
/// conformance fact gated on an instance failed with it.
/// </para>
/// <para>
/// <b>SQLite cannot see this class of defect, which is why a second provider did not catch it.</b> A
/// constraint name there is per table and is not required to be unique at all. A SQL Server constraint is an
/// object in <c>sys.objects</c> keyed by SCHEMA, so every check, primary key, foreign key and default name in
/// the file shares one namespace. Index names are the narrower rule, unique per table rather than per schema.
/// </para>
/// </summary>
public partial class SqlServerCatalogDdlNamingTests
{
    [Fact]
    public void EveryConstraintNameInTheEmbeddedDdlIsUniqueAcrossTheWholeScript()
    {
        string[] duplicates = ConstraintName().Matches(SqlServerCatalogSchema.SchemaSql)
            .Select(static match => match.Groups[1].Value)
            .GroupBy(static name => name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Array.Empty<string>(), duplicates);
    }

    [Fact]
    public void EveryIndexNameInTheEmbeddedDdlIsUniqueWithinItsTable()
    {
        string[] duplicates = CreateIndex().Matches(SqlServerCatalogSchema.SchemaSql)
            .Select(static match => match.Groups[2].Value + "." + match.Groups[1].Value)
            .GroupBy(static keyed => keyed, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static keyed => keyed, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Array.Empty<string>(), duplicates);
    }

    /// <summary>Every named constraint of every kind, since one schema namespace holds all of them.</summary>
    [GeneratedRegex(@"CONSTRAINT (\w+)")]
    private static partial Regex ConstraintName();

    /// <summary>A standalone index and the table it hangs off, which is the namespace its name lives in.</summary>
    [GeneratedRegex(@"^CREATE (?:UNIQUE )?INDEX (\w+) ON dbo\.(\w+)", RegexOptions.Multiline)]
    private static partial Regex CreateIndex();
}
