using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The embedded <c>CatalogSchemaV1.sql</c> and the provider's own source read together, so a parameter bound
/// with the wrong SQL type goes red on a plain <c>dotnet test</c> instead of on the first live instance.
/// <para>
/// <b>What this class exists for.</b> Every write went through one untyped helper that called
/// <c>AddWithValue</c>, and <c>AddWithValue</c> asks the VALUE what type it is. A null has nothing to ask, so
/// SqlClient typed every null parameter <c>nvarchar</c>, and the two field tables and the remap rules hold
/// <c>varbinary(max)</c> columns that a null field leaves empty. SQL Server refuses that pairing outright with
/// "Implicit conversion from data type nvarchar to varbinary(max) is not allowed", so 29 catalog facts failed
/// against Azure SQL while every one of them passed here. The SQLite provider cannot see this class of defect
/// at all: SQLite carries a type per VALUE rather than per column, so a null bound as text lands in a blob
/// column without complaint.
/// </para>
/// <para>
/// <b>Why the source and not the built commands.</b> A write command is built and executed inside a private
/// async method that needs an open connection, so there is nothing to hand a test without an instance, which
/// is the one thing this check must not need. The source carries the whole fact anyway: the statement says
/// which column a parameter fills, the schema says what type that column is, and the binder's name says what
/// type the parameter gets. Reading the three and comparing them is the same cross-check a live insert makes,
/// minus the database.
/// </para>
/// <para>
/// <b>Column names carry one type engine wide, which is what lets the lookup ignore tables.</b>
/// <c>family_id</c> is <c>bigint</c> in all four tables that hold one, <c>type_id</c> is <c>int</c> in all
/// nine, and so on down the schema. The first fact below pins that property, so the rest may resolve a column
/// by name alone rather than tracking which table a statement is against through a MERGE.
/// </para>
/// </summary>
public partial class SqlServerCatalogParameterTypeTests
{
    /// <summary>
    /// The rule the other facts read: one column name means one SQL type, everywhere in the schema.
    /// </summary>
    [Fact]
    public void EveryColumnNameInTheSchemaCarriesOneSqlTypeWhereverItAppears()
    {
        string[] offenders = ColumnFamilies()
            .Where(static entry => entry.Value.Count > 1)
            .Select(static entry => entry.Key + " is " + string.Join(" and ", entry.Value))
            .OrderBy(static line => line, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A column name carries two different SQL types in CatalogSchemaV1.sql. The parameter check in this "
            + "class resolves a column by name alone, which that breaks, so either give the two columns "
            + "different names or teach the check to track the table a statement is against.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The same rule one level up: a parameter name means one column type across the whole provider. Two
    /// statements that reuse a name for columns of different types cannot both bind it correctly, because the
    /// binder is chosen per name at the call site.
    /// </summary>
    [Fact]
    public void EveryParameterNameStandsForOneColumnTypeAcrossTheProvider()
    {
        string[] offenders = ParameterFamilies()
            .Where(static entry => entry.Value.Count > 1)
            .Select(static entry => "@" + entry.Key + " fills " + string.Join(" and ", entry.Value))
            .OrderBy(static line => line, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A parameter name fills columns of two different SQL types. One of them binds wrong whichever "
            + "binder the call site picks, so rename the parameter in one of the two statements.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The fact the live failure would have hit: every bind call site names the binder for the SQL type of the
    /// column its parameter fills.
    /// </summary>
    [Fact]
    public void EveryParameterBoundInTheProviderCarriesItsColumnsSqlType()
    {
        IReadOnlyDictionary<string, List<string>> families = ParameterFamilies();
        var offenders = new List<string>();

        foreach ((string file, string source) in ProviderSources())
        {
            foreach (Match match in BindCall().Matches(source))
            {
                string parameter = match.Groups[2].Value;
                string bound = match.Groups[1].Value;
                int line = source.Take(match.Index).Count(static character => character == '\n') + 1;

                if (!families.TryGetValue(parameter, out List<string>? family))
                {
                    offenders.Add(
                        $"{file}:{line} binds @{parameter}, which no statement in this provider maps to a column");
                    continue;
                }

                if (family.Count != 1)
                {
                    offenders.Add(
                        $"{file}:{line} binds @{parameter}, which fills " + string.Join(" and ", family));
                    continue;
                }

                if (!string.Equals(bound, family[0], StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{file}:{line} binds @{parameter} through Bind{bound} and its column is {family[0]}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A parameter is bound with a type its column does not accept. SQL Server refuses nvarchar into "
            + "varbinary(max) outright, and the conversions it does allow are silent, so bind through the "
            + "binder named for the column: BindInt, BindBigInt, BindText, BindLargeText, BindBlob, BindTime. "
            + "A parameter no statement maps is listed too, because an unmapped one is an unchecked one.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The habit that produced the defect, banned outright. <c>AddWithValue</c> reads the type off the value,
    /// so it cannot type a null at all and quietly types everything else by inference.
    /// </summary>
    [Fact]
    public void TheProviderBindsNothingThroughAddWithValue()
    {
        string[] offenders = ProviderSources()
            .Where(static entry => AddWithValueCall().IsMatch(entry.Source))
            .Select(static entry => entry.File)
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "AddWithValue infers a parameter's type from its value, which leaves a null with no type at all "
            + "and reads a short string as a short nvarchar. Bind through the typed binders instead.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The defect at the binder rather than at the call site. A null has no type for SqlClient to read off
    /// it, so an untyped bind sent every one of these as <c>nvarchar</c>, which SQL Server refuses into
    /// <c>varbinary(max)</c> and silently converts into the rest.
    /// </summary>
    [Fact]
    public void ABoundNullCarriesItsColumnsTypeRatherThanNVarChar()
    {
        using var command = new SqlCommand();
        SqlServerContentAuthoringStore.BindBlob(command, "@blob", null);
        SqlServerContentAuthoringStore.BindInt(command, "@number", null);
        SqlServerContentAuthoringStore.BindBigInt(command, "@wide", null);
        SqlServerContentAuthoringStore.BindText(command, "@text", null);
        SqlServerContentAuthoringStore.BindLargeText(command, "@large", null);

        Assert.Equal(SqlDbType.VarBinary, command.Parameters["@blob"].SqlDbType);
        Assert.Equal(-1, command.Parameters["@blob"].Size);
        Assert.Equal(DBNull.Value, command.Parameters["@blob"].Value);
        Assert.Equal(SqlDbType.Int, command.Parameters["@number"].SqlDbType);
        Assert.Equal(DBNull.Value, command.Parameters["@number"].Value);
        Assert.Equal(SqlDbType.BigInt, command.Parameters["@wide"].SqlDbType);
        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@text"].SqlDbType);
        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@large"].SqlDbType);
        Assert.Equal(-1, command.Parameters["@large"].Size);
    }

    /// <summary>
    /// A bound VALUE keeps its own CLR type, and a stamp keeps the column's scale. An empty payload is an
    /// empty array rather than a null, because the field tables mean different things by the two.
    /// </summary>
    [Fact]
    public void ABoundValueKeepsItsClrTypeAndAStampKeepsTheColumnsScale()
    {
        var stamp = new DateTimeOffset(2026, 9, 16, 4, 5, 6, TimeSpan.Zero).AddTicks(1234567);
        using var command = new SqlCommand();
        SqlServerContentAuthoringStore.BindTime(command, "@at", stamp);
        SqlServerContentAuthoringStore.BindBlob(command, "@blob", []);
        SqlServerContentAuthoringStore.BindBigInt(command, "@wide", 7);

        Assert.Equal(SqlDbType.DateTimeOffset, command.Parameters["@at"].SqlDbType);
        Assert.Equal((byte)7, command.Parameters["@at"].Scale);
        Assert.Equal(stamp, command.Parameters["@at"].Value);
        Assert.Empty(Assert.IsType<byte[]>(command.Parameters["@blob"].Value));
        Assert.Equal(7L, command.Parameters["@wide"].Value);
    }

    /// <summary>Every column the schema declares, by name, with the binder family its type wants.</summary>
    static Dictionary<string, List<string>> ColumnFamilies()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match match in SchemaColumn().Matches(SqlServerCatalogSchema.SchemaSql))
        {
            Record(found, match.Groups[1].Value, FamilyOf(match.Groups[2].Value));
        }

        return found;
    }

    /// <summary>
    /// Every <c>@parameter</c> the provider's statements use, with the binder family of the column it fills.
    /// A parameter that fills no column (the two paging parameters) is typed from its clause instead.
    /// </summary>
    static Dictionary<string, List<string>> ParameterFamilies()
    {
        Dictionary<string, List<string>> columns = ColumnFamilies();
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Fill(string parameter, string column)
        {
            if (columns.TryGetValue(column, out List<string>? family) && family.Count == 1)
            {
                Record(found, parameter, family[0]);
            }
        }

        foreach (string statement in ProviderSources().SelectMany(static entry => SqlBlocks(entry.Source)))
        {
            foreach (Match match in Insert().Matches(statement))
            {
                string[] into = Split(match.Groups[1].Value);
                string[] values = Split(match.Groups[2].Value);
                if (into.Length != values.Length)
                {
                    continue;
                }

                for (int i = 0; i < into.Length; i++)
                {
                    if (values[i].StartsWith('@'))
                    {
                        Fill(values[i][1..], into[i]);
                    }
                }
            }

            foreach (Match match in Compared().Matches(statement))
            {
                Fill(match.Groups[2].Value, match.Groups[1].Value);
            }

            foreach (Match match in Aliased().Matches(statement))
            {
                Fill(match.Groups[1].Value, match.Groups[2].Value);
            }

            foreach (Match match in Paged().Matches(statement))
            {
                Record(found, match.Groups[1].Value, "Int");
            }
        }

        return found;
    }

    /// <summary>The binder family a declared SQL type wants, or the type itself when it is a new one.</summary>
    static string FamilyOf(string declared) => declared switch
    {
        "int" => "Int",
        "bigint" => "BigInt",
        "varbinary(max)" => "Blob",
        "nvarchar(max)" => "LargeText",
        _ when declared.StartsWith("nvarchar(", StringComparison.Ordinal) => "Text",
        _ when declared.StartsWith("datetimeoffset(", StringComparison.Ordinal) => "Time",
        _ => declared,
    };

    static void Record(Dictionary<string, List<string>> into, string key, string family)
    {
        if (!into.TryGetValue(key, out List<string>? families))
        {
            families = [];
            into[key] = families;
        }

        if (!families.Contains(family, StringComparer.Ordinal))
        {
            families.Add(family);
        }
    }

    /// <summary>A comma list of column names or bound values, trimmed and unbracketed.</summary>
    static string[] Split(string list) => list
        .Split(',')
        .Select(static part => part.Trim().Trim('[', ']'))
        .ToArray();

    /// <summary>
    /// The SQL a source file holds: every raw string literal, plus every one line literal that reads like a
    /// statement. The raw ones come out first and are blanked, so the line scan cannot see into them.
    /// </summary>
    static List<string> SqlBlocks(string source)
    {
        var blocks = new List<string>();
        foreach (Match match in RawLiteral().Matches(source))
        {
            blocks.Add(match.Groups[1].Value);
        }

        string remaining = RawLiteral().Replace(source, "\n");
        foreach (Match match in LineLiteral().Matches(remaining))
        {
            if (Statement().IsMatch(match.Groups[1].Value))
            {
                blocks.Add(match.Groups[1].Value);
            }
        }

        return blocks;
    }

    /// <summary>Every source file of the provider, by file name and text.</summary>
    static IReadOnlyList<(string File, string Source)> ProviderSources() => Directory
        .EnumerateFiles(ProviderDirectory(), "*.cs", SearchOption.TopDirectoryOnly)
        .OrderBy(static path => path, StringComparer.Ordinal)
        .Select(static path => (Path.GetFileName(path), File.ReadAllText(path)))
        .ToArray();

    /// <summary>
    /// The provider's directory, found from this file's own compile time path. The same trick
    /// <c>ArchitectureTests</c> uses, and the reason this class needs no test data of its own.
    /// </summary>
    static string ProviderDirectory([CallerFilePath] string thisFile = "")
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!)!;
        return Path.Combine(root, "KhaozEngine.Catalog.SqlServer");
    }

    /// <summary>One declared column of a CREATE TABLE, which is four spaces in and typed on the same line.</summary>
    [GeneratedRegex(
        @"^    \[?(\w+)\]?\s+(int|bigint|nvarchar\(\w+\)|varbinary\(max\)|datetimeoffset\(\d\))(?=\s)",
        RegexOptions.Multiline)]
    private static partial Regex SchemaColumn();

    /// <summary>A call to the untyped bind, which the doc comments name without calling.</summary>
    [GeneratedRegex(@"\.AddWithValue\s*\(")]
    private static partial Regex AddWithValueCall();

    /// <summary>A bind call site, with the binder's family suffix and the parameter it binds.</summary>
    [GeneratedRegex(@"\bBind(\w*)\(\s*\w+\s*,\s*""@(\w+)""")]
    private static partial Regex BindCall();

    /// <summary>A raw string literal, which is how every multi line statement is written.</summary>
    [GeneratedRegex(@"""""""(.*?)""""""", RegexOptions.Singleline)]
    private static partial Regex RawLiteral();

    /// <summary>A one line string literal, which is how the short statements are written.</summary>
    [GeneratedRegex(@"""([^""\n]*)""")]
    private static partial Regex LineLiteral();

    /// <summary>What makes a one line literal a statement rather than a message.</summary>
    [GeneratedRegex(@"\b(SELECT|INSERT|UPDATE|DELETE|MERGE)\b")]
    private static partial Regex Statement();

    /// <summary>An INSERT and its two lists, in both the plain and the MERGE spelling.</summary>
    [GeneratedRegex(@"INSERT(?:\s+INTO)?\s+(?:dbo\.\w+\s*)?\(\s*([^)]*?)\s*\)\s*VALUES\s*\(\s*([^)]*?)\s*\)")]
    private static partial Regex Insert();

    /// <summary>A column compared or assigned a parameter, which is how a WHERE and a SET both read.</summary>
    [GeneratedRegex(@"(\w+)\s*(?:=|<>|<=|>=|<|>)\s*@(\w+)")]
    private static partial Regex Compared();

    /// <summary>A parameter given a column's name, which is how the MERGE sources read.</summary>
    [GeneratedRegex(@"@(\w+)\s+AS\s+(\w+)")]
    private static partial Regex Aliased();

    /// <summary>The paging parameters, which fill no column and are int by the clause they sit in.</summary>
    [GeneratedRegex(@"(?:OFFSET|FETCH NEXT)\s+@(\w+)\s+ROWS")]
    private static partial Regex Paged();
}
