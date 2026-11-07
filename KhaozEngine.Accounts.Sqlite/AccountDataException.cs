using System;

namespace KhaozEngine.Accounts.Sqlite;

/// <summary>A structured refusal of the account table or of a value stored in it. Existing
/// InvalidOperationException catches remain valid. It names tables, columns and collations, and never a subject, a
/// stored value or a row.</summary>
public sealed class AccountDataException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the table or the value.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public AccountDataException(AccountDataRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the table or the value.</summary>
    public AccountDataRefusal Reason { get; }

    /// <summary>The configured table, unquoted, for <see cref="AccountDataRefusal.CoreColumnMissing"/>.</summary>
    public string? TableName { get; init; }

    /// <summary>The absent column for <see cref="AccountDataRefusal.CoreColumnMissing"/>, or the column holding the
    /// refused value for <see cref="AccountDataRefusal.TimestampMalformed"/>.</summary>
    public string? ColumnName { get; init; }

    /// <summary>The collation the subject key uses, for
    /// <see cref="AccountDataRefusal.SubjectKeyCollationUnsupported"/>.</summary>
    public string? ActualCollation { get; init; }

    /// <summary>The collation the subject key must use, for
    /// <see cref="AccountDataRefusal.SubjectKeyCollationUnsupported"/>.</summary>
    public string? ExpectedCollation { get; init; }
}
