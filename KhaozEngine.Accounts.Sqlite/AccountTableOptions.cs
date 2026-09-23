using System;

namespace KhaozEngine.Accounts.Sqlite;

/// <summary>
/// Where a <see cref="SqliteAccountStore"/> keeps its accounts.
/// </summary>
/// <remarks>
/// The name is configuration, never SQL: the store refuses anything but a plain identifier (ASCII letters, digits
/// and underscores, not starting with a digit, at most 128 characters) and quotes it, so no value here can reach
/// the statement text as anything but a table name.
/// </remarks>
/// <param name="Table">The table name. <c>accounts</c>, Grimhollow's table, by default.</param>
public sealed record AccountTableOptions(string Table = "accounts");

/// <summary>The one identifier rule the SQLite store applies to a configured table name.</summary>
internal static class SqliteAccountIdentifier
{
    /// <summary>The longest table name accepted.</summary>
    internal const int MaxChars = 128;

    /// <summary>
    /// Returns <paramref name="name"/> double-quoted when it is a plain identifier, and throws otherwise. The
    /// message names the rule and never the value.
    /// </summary>
    internal static string Quote(string? name, string paramName)
    {
        if (!IsPlain(name))
            throw new ArgumentException(
                "The account table name must be a plain SQL identifier: ASCII letters, digits and underscores, not " +
                $"starting with a digit, at most {MaxChars} characters.", paramName);
        return "\"" + name + "\"";
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
