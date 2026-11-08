namespace KhaozEngine.Accounts.SqlServer;

/// <summary>One owned column the layout check refused. Identifiers the refused rule does not know remain null.</summary>
/// <param name="Reason">The rule the column broke.</param>
/// <param name="ColumnName">The owned column.</param>
/// <param name="Message">The developer-facing explanation, not a classification token.</param>
public sealed record AccountSchemaFinding(AccountSchemaRefusal Reason, string ColumnName, string Message)
{
    /// <summary>The type the column declares, for <see cref="AccountSchemaRefusal.ColumnTypeMismatch"/>.</summary>
    public string? ActualType { get; init; }

    /// <summary>The type the store reads the column as, for <see cref="AccountSchemaRefusal.ColumnTypeMismatch"/>.</summary>
    public string? ExpectedType { get; init; }

    /// <summary>The characters the column holds, for <see cref="AccountSchemaRefusal.ColumnTooNarrow"/>.</summary>
    public int? ActualCharacters { get; init; }

    /// <summary>The least characters the column may hold, inclusive, for
    /// <see cref="AccountSchemaRefusal.ColumnTooNarrow"/>.</summary>
    public int? MinimumCharacters { get; init; }
}
