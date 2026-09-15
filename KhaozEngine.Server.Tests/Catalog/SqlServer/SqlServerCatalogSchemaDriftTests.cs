using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The one SQL Server schema test that needs NO SQL Server: the embedded <c>CatalogSchemaV1.sql</c> read as
/// text against the three name sets the validator compares a live database to.
/// <para>
/// <b>Why it exists.</b> <c>SqlServerCatalogSchemaExpectations</c> holds 117 names transcribed BY HAND from
/// that file, and <c>SqlServerCatalogSchemaValidation</c> compares <c>sys.tables</c>,
/// <c>sys.indexes</c> and <c>sys.check_constraints</c> against them in both directions. A constraint added to
/// the DDL and not to the list, or renamed in one and not the other, still builds and still passes every
/// other test in this project, because all of those skip without an instance. The first sign would be a
/// production <c>ValidateOnly</c> start refusing a schema this same build created, naming an object an
/// operator cannot act on, which is the exact failure the validator exists to prevent.
/// </para>
/// <para>
/// <b>What it does NOT do</b> is generate the expectation sets from the DDL at run time. That would make the
/// validator compare the file against itself and vouch for nothing. The transcription stays, and this test is
/// what keeps it honest.
/// </para>
/// </summary>
public partial class SqlServerCatalogSchemaDriftTests
{
    [Fact]
    public void TheEmbeddedDdlDeclaresExactlyTheTablesTheValidatorExpects()
    {
        SchemaObjects declared = Parse();

        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.Tables),
            Ordered(declared.Tables));
    }

    [Fact]
    public void TheEmbeddedDdlDeclaresExactlyTheIndexesTheValidatorExpects()
    {
        SchemaObjects declared = Parse();

        // Primary keys are in here because a primary key IS an index in sys.indexes, which is the set the
        // validator reads. Keyed table.index, so a correctly named index hanging off the wrong table is drift
        // this test can see.
        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.Indexes),
            Ordered(declared.Indexes));
    }

    [Fact]
    public void TheEmbeddedDdlDeclaresExactlyTheCheckConstraintsTheValidatorExpects()
    {
        SchemaObjects declared = Parse();

        Assert.Equal(
            Ordered(SqlServerCatalogSchemaExpectations.Checks),
            Ordered(declared.Checks));
    }

    [Fact]
    public void EveryConstraintInTheEmbeddedDdlIsNamed()
    {
        // The whole comparison above is by NAME, and SQL Server invents a per-database name for an unnamed
        // constraint, so an unnamed one is unverifiable by construction rather than merely undeclared here.
        string[] unnamed = SqlServerCatalogSchema.SchemaSql
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => UnnamedConstraint().IsMatch(line))
            .ToArray();

        Assert.Empty(unnamed);
    }

    /// <summary>The three name sets one reading of the DDL produced.</summary>
    /// <param name="Tables">Every table, bare.</param>
    /// <param name="Indexes">Every named index and primary key, as <c>table.index</c>.</param>
    /// <param name="Checks">Every check constraint, as <c>table.constraint</c>.</param>
    sealed record SchemaObjects(
        IReadOnlySet<string> Tables,
        IReadOnlySet<string> Indexes,
        IReadOnlySet<string> Checks);

    /// <summary>
    /// The DDL read the way the transcription read it: line by line, attributing every inline constraint to
    /// the <c>CREATE TABLE</c> it sits inside. A standalone <c>CREATE INDEX</c> names its own table and ends
    /// the block, so nothing after it can be attributed to a table by accident.
    /// </summary>
    static SchemaObjects Parse()
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var checks = new HashSet<string>(StringComparer.Ordinal);
        string? table = null;

        foreach (string raw in SqlServerCatalogSchema.SchemaSql.Split('\n'))
        {
            string line = raw.Trim();

            Match created = CreateTable().Match(line);
            if (created.Success)
            {
                table = created.Groups[1].Value;
                tables.Add(table);
                continue;
            }

            Match index = CreateIndex().Match(line);
            if (index.Success)
            {
                indexes.Add(index.Groups[2].Value + "." + index.Groups[1].Value);
                table = null;
                continue;
            }

            if (table is null)
            {
                continue;
            }

            Match key = PrimaryKey().Match(line);
            if (key.Success)
            {
                indexes.Add(table + "." + key.Groups[1].Value);
                continue;
            }

            Match check = CheckConstraint().Match(line);
            if (check.Success)
            {
                checks.Add(table + "." + check.Groups[1].Value);
            }
        }

        return new SchemaObjects(tables, indexes, checks);
    }

    /// <summary>The set as a sorted list, so a failure message names what differs rather than reporting false.</summary>
    static IReadOnlyList<string> Ordered(IReadOnlySet<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    [GeneratedRegex(@"^CREATE TABLE dbo\.(\w+)")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^CREATE (?:UNIQUE )?INDEX (\w+) ON dbo\.(\w+)")]
    private static partial Regex CreateIndex();

    [GeneratedRegex(@"CONSTRAINT (\w+) PRIMARY KEY")]
    private static partial Regex PrimaryKey();

    [GeneratedRegex(@"CONSTRAINT (\w+) CHECK")]
    private static partial Regex CheckConstraint();

    [GeneratedRegex(@"^(?:PRIMARY KEY|CHECK|UNIQUE|FOREIGN KEY)\b")]
    private static partial Regex UnnamedConstraint();
}
