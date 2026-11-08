using System;
using System.Collections.Generic;

namespace KhaozEngine.Accounts.SqlServer;

/// <summary>A structured refusal of the account table's schema. Existing InvalidOperationException catches remain
/// valid. It names schemas, tables, columns and types, and never a row.</summary>
public sealed class AccountSchemaException : InvalidOperationException
{
    private readonly IReadOnlyList<AccountSchemaFinding>? findings;

    /// <summary>Construct a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the bootstrap.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public AccountSchemaException(AccountSchemaRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the bootstrap. For a layout refusal, the first finding's reason.</summary>
    public AccountSchemaRefusal Reason { get; }

    /// <summary>The configured schema, for <see cref="AccountSchemaRefusal.SchemaMissing"/>.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The configured table, for <see cref="AccountSchemaRefusal.TableMissing"/>.</summary>
    public string? TableName { get; init; }

    /// <summary>The first finding's column, for a layout refusal.</summary>
    public string? ColumnName { get; init; }

    /// <summary>The type the first finding's column declares, for
    /// <see cref="AccountSchemaRefusal.ColumnTypeMismatch"/>.</summary>
    public string? ActualType { get; init; }

    /// <summary>The type the store reads the first finding's column as, for
    /// <see cref="AccountSchemaRefusal.ColumnTypeMismatch"/>.</summary>
    public string? ExpectedType { get; init; }

    /// <summary>The characters the first finding's column holds, for
    /// <see cref="AccountSchemaRefusal.ColumnTooNarrow"/>.</summary>
    public int? ActualCharacters { get; init; }

    /// <summary>The least characters the first finding's column may hold, inclusive, for
    /// <see cref="AccountSchemaRefusal.ColumnTooNarrow"/>.</summary>
    public int? MinimumCharacters { get; init; }

    /// <summary>Every refused column in layout order, for a layout refusal, or null for a missing schema or table.
    /// Setting this property takes an owned, read-only snapshot, so changing the input collection or casting the
    /// exposed list cannot mutate it.</summary>
    public IReadOnlyList<AccountSchemaFinding>? Findings
    {
        get => findings;
        init => findings = value is null ? null : Array.AsReadOnly<AccountSchemaFinding>([.. value]);
    }
}
