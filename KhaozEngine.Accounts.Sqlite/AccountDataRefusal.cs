namespace KhaozEngine.Accounts.Sqlite;

/// <summary>Rules that refuse a table the SQLite account store cannot adopt, or what it reads back from one.</summary>
public enum AccountDataRefusal
{
    /// <summary>Refuses an existing table without one of the four original columns.</summary>
    CoreColumnMissing,

    /// <summary>Refuses an existing table whose primary key or unique index on the subject alone is not
    /// <c>BINARY</c> collated.</summary>
    SubjectKeyCollationUnsupported,

    /// <summary>Refuses a stored time that is not a date and time.</summary>
    TimestampMalformed,

    /// <summary>Refuses a find-or-create the table discarded, through its own trigger calling <c>RAISE(IGNORE)</c>
    /// or a constraint declared <c>ON CONFLICT IGNORE</c>, so the statement returned no row. The write is rolled
    /// back.</summary>
    UpsertReturnedNoRow,

    /// <summary>Refuses a find-or-create that returned a row whose subject is not ordinally the minted one, which
    /// only a table changed after the store opened can produce. The write is rolled back.</summary>
    UpsertReturnedDifferentAccount,
}
