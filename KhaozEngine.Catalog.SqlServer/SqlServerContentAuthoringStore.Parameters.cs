using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The parameter half: one binder per SQL type the schema declares, so a bound value carries the type of the
/// COLUMN it fills rather than the type SqlClient guesses from the value.
/// <para>
/// <b>Why not <c>AddWithValue</c>.</b> It reads the type off the value, and a null has no type to read, so
/// SqlClient falls back to <c>nvarchar</c> for every null parameter. SQL Server refuses nvarchar into
/// <c>varbinary(max)</c> outright with "Implicit conversion from data type nvarchar to varbinary(max) is not
/// allowed", which is what 29 catalog facts hit against Azure SQL the first time this provider ran against a
/// live instance: a field that holds a number leaves <c>blob_value</c> null, and the insert died on the null
/// rather than on anything about the row. The conversions it does NOT refuse are the worse half, because
/// nvarchar into <c>int</c> or <c>bigint</c> or <c>datetimeoffset</c> is implicit and silent, so every other
/// null in the provider was converting per row and nothing said so.
/// </para>
/// <para>
/// <b>The SQLite provider cannot show this and never will.</b> SQLite carries a type per VALUE, not per
/// column, so a null bound as text lands in a blob column without complaint and both providers pass the same
/// conformance suite. <c>SqlServerCatalogParameterTypeTests</c> is what sees it without an instance, by
/// reading the schema for each column's type, the statements for which column a parameter fills, and this
/// file's binder names for the type each parameter gets.
/// </para>
/// <para>
/// <b>A parameter name means one column type across the whole provider</b>, which is what lets a call site
/// pick its binder by name alone. Two statements that need different types use different names, which is why
/// <c>@blockOrdinal</c> is not <c>@ordinal</c> (one is <c>catalog_family_block.block_ordinal</c> and the other
/// is the <c>bigint</c> identity of a draft edit) and why the row reads take <c>@live</c> rather than
/// <c>@at</c> (one is a version number and the other is a timestamp).
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <summary>The size that means <c>max</c> on a varbinary or nvarchar parameter.</summary>
    const int MaxLength = -1;

    /// <summary>An <c>int</c> column, or NULL.</summary>
    internal static void BindInt(SqlCommand command, string name, int? value)
        => Add(command, name, SqlDbType.Int, value);

    /// <summary>A <c>bigint</c> column, or NULL.</summary>
    internal static void BindBigInt(SqlCommand command, string name, long? value)
        => Add(command, name, SqlDbType.BigInt, value);

    /// <summary>
    /// An <c>nvarchar(n)</c> column, or NULL. The size is left to the value, exactly as the untyped bind had
    /// it: pinning the column's declared length here would TRUNCATE an over-long string on the way out, and
    /// the schema's own <c>LEN</c> check is what should refuse it instead.
    /// </summary>
    internal static void BindText(SqlCommand command, string name, string? value)
        => Add(command, name, SqlDbType.NVarChar, value);

    /// <summary>
    /// An <c>nvarchar(max)</c> column, or NULL. Only the audit's before and after values are this, and they
    /// are declared max because a field value renders to more than the 4000 characters an nvarchar(n)
    /// parameter would cap at.
    /// </summary>
    internal static void BindLargeText(SqlCommand command, string name, string? value)
        => Add(command, name, SqlDbType.NVarChar, value).Size = MaxLength;

    /// <summary>
    /// A <c>varbinary(max)</c> column, or NULL. An ABSENT payload is a null here rather than an empty array,
    /// because the two field tables mean different things by them: a null field value holds no bytes at all
    /// and an empty one holds zero bytes.
    /// </summary>
    internal static void BindBlob(SqlCommand command, string name, byte[]? value)
        => Add(command, name, SqlDbType.VarBinary, value).Size = MaxLength;

    /// <summary>
    /// A <c>datetimeoffset(7)</c> column. The scale is the column's, so the stamp a read hands back is the
    /// one the write passed in rather than a rounded version of it.
    /// </summary>
    internal static void BindTime(SqlCommand command, string name, DateTimeOffset value)
        => Add(command, name, SqlDbType.DateTimeOffset, value).Scale = 7;

    /// <summary>
    /// One parameter, typed, with a null mapped to <see cref="DBNull"/> so a nullable column takes NULL
    /// rather than the provider throwing on a null value.
    /// </summary>
    static SqlParameter Add(SqlCommand command, string name, SqlDbType type, object? value)
    {
        SqlParameter parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }
}
