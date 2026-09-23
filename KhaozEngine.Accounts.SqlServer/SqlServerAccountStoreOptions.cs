using System;

namespace KhaozEngine.Accounts.SqlServer;

/// <summary>What <see cref="SqlServerAccountStore"/> may do to the database on its first call.</summary>
public enum AccountSchemaMode
{
    /// <summary>
    /// Creates the account table when absent and adds a missing <c>ban_reason</c> or <c>ban_until</c> when present,
    /// under an application lock, then validates the result. Needs DDL rights on the schema.
    /// </summary>
    AutoCreate,

    /// <summary>
    /// Reads the catalog and refuses a missing or mismatched table, with no DDL and no lock, so the runtime identity
    /// needs only <c>SELECT</c>, <c>INSERT</c> and <c>UPDATE</c> on the table. Run once under
    /// <see cref="AutoCreate"/> with a migration identity first.
    /// </summary>
    ValidateOnly,
}

/// <summary>
/// Where a <see cref="SqlServerAccountStore"/> keeps its accounts, and what it may do to the schema.
/// </summary>
/// <remarks>
/// The names are configuration, never SQL: the store refuses anything but a plain identifier (ASCII letters,
/// digits and underscores, not starting with a digit, at most 128 characters) and bracket-quotes it, so no value
/// here can reach the statement text as anything but a name. The schema must already exist, which <c>dbo</c>
/// always does.
/// </remarks>
/// <param name="Schema">The SQL schema the table lives in. <c>dbo</c> by default.</param>
/// <param name="Table">The table name. <c>accounts</c>, Grimhollow's table, by default.</param>
/// <param name="SchemaMode">Whether the first call may create and widen the table, or only validate it.</param>
public sealed record SqlServerAccountStoreOptions(
    string Schema = "dbo", string Table = "accounts", AccountSchemaMode SchemaMode = AccountSchemaMode.AutoCreate);

/// <summary>The one identifier rule the SQL Server store applies to a configured schema or table name.</summary>
internal static class SqlServerAccountIdentifier
{
    /// <summary>The longest name accepted, SQL Server's own identifier limit.</summary>
    internal const int MaxChars = 128;

    /// <summary>
    /// Returns <paramref name="name"/> bracket-quoted when it is a plain identifier, and throws otherwise. The
    /// message names the rule and never the value.
    /// </summary>
    internal static string Quote(string? name, string what, string paramName)
    {
        if (!IsPlain(name))
            throw new ArgumentException(
                $"The account {what} name must be a plain SQL identifier: ASCII letters, digits and underscores, not " +
                $"starting with a digit, at most {MaxChars} characters.", paramName);
        return "[" + name + "]";
    }

    private static bool IsPlain(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxChars || char.IsAsciiDigit(name[0])) return false;
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }
}
