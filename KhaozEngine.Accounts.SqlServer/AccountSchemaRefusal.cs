namespace KhaozEngine.Accounts.SqlServer;

/// <summary>Rules that refuse the SQL Server account store's first-call schema bootstrap.</summary>
public enum AccountSchemaRefusal
{
    /// <summary>Refuses a configured schema that does not exist.</summary>
    SchemaMissing,

    /// <summary>Refuses a table that does not exist under <see cref="AccountSchemaMode.ValidateOnly"/>, which never
    /// creates one.</summary>
    TableMissing,

    /// <summary>Refuses a table without an owned column it must already have.</summary>
    ColumnMissing,

    /// <summary>Refuses an owned column declared with a type the store cannot read.</summary>
    ColumnTypeMismatch,

    /// <summary>Refuses an owned text column narrower than the limit the store applies to it.</summary>
    ColumnTooNarrow,
}
