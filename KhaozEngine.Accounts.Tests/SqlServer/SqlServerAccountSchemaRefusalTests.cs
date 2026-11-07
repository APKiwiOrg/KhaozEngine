using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// Every <see cref="AccountSchemaRefusal"/> over the bootstrap's two catalog-free checks, so each reason runs on every
/// leg. <see cref="SqlServerAccountStoreLayoutTests"/> proves the same reasons through the real catalog on the SQL
/// Server leg.
/// </summary>
public sealed class SqlServerAccountSchemaRefusalTests
{
    private static Dictionary<string, (string Type, int MaxBytes, bool Nullable)> FreshLayout() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["subject"] = ("nvarchar", AccountStoreRules.MaxSubjectChars * 2, false),
            ["display_name"] = ("nvarchar", AccountStoreRules.MaxDisplayNameChars * 2, true),
            ["whitelisted"] = ("bit", 1, false),
            ["banned"] = ("bit", 1, false),
            ["ban_reason"] = ("nvarchar", AccountStoreRules.MaxBanReasonChars * 2, true),
            ["ban_until"] = ("datetimeoffset", 10, true),
            ["created_at_utc"] = ("datetimeoffset", 10, true),
            ["updated_at_utc"] = ("datetimeoffset", 10, true),
        };

    private static AccountSchemaException Refused(
        IReadOnlyDictionary<string, (string Type, int MaxBytes, bool Nullable)> found, bool addedMayBeMissing = false) =>
        Assert.Throws<AccountSchemaException>(
            () => SqlServerAccountSchema.CheckColumns(found, AccountSchemaMode.ValidateOnly, addedMayBeMissing));

    [Theory]
    [InlineData(AccountSchemaMode.AutoCreate)]
    [InlineData(AccountSchemaMode.ValidateOnly)]
    public void AMissingSchema_IsRefusedInEitherMode_NamingOnlyTheSchema(AccountSchemaMode mode)
    {
        AccountSchemaException refusal = Assert.Throws<AccountSchemaException>(
            () => SqlServerAccountSchema.RefuseMissingObjects(schemaExists: false, tableExists: false, "game", "grim_accounts", mode));

        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
        Assert.Equal(AccountSchemaRefusal.SchemaMissing, refusal.Reason);
        Assert.Equal("game", refusal.SchemaName);
        Assert.Null(refusal.TableName);
        Assert.Null(refusal.Findings);

        // The control supplies a schema and a table that both exist, because a table cannot exist without its
        // schema. Neither mode has anything left to refuse.
        SqlServerAccountSchema.RefuseMissingObjects(schemaExists: true, tableExists: true, "game", "grim_accounts", mode);
    }

    [Fact]
    public void AMissingTable_IsRefusedUnderValidateOnly_AndLeftForAutoCreateToMake()
    {
        AccountSchemaException refusal = Assert.Throws<AccountSchemaException>(
            () => SqlServerAccountSchema.RefuseMissingObjects(schemaExists: true, tableExists: false, "game", "grim_accounts",
                AccountSchemaMode.ValidateOnly));

        Assert.Equal(AccountSchemaRefusal.TableMissing, refusal.Reason);
        Assert.Equal("grim_accounts", refusal.TableName);
        Assert.Null(refusal.SchemaName);
        Assert.Null(refusal.Findings);

        SqlServerAccountSchema.RefuseMissingObjects(schemaExists: true, tableExists: true, "game", "grim_accounts",
            AccountSchemaMode.ValidateOnly);
        SqlServerAccountSchema.RefuseMissingObjects(schemaExists: true, tableExists: false, "game", "grim_accounts",
            AccountSchemaMode.AutoCreate);
    }

    // One column the widening never adds, refused even while the added ones may be missing, and one it adds, refused
    // once they may not be.
    [Theory]
    [InlineData("whitelisted", true)]
    [InlineData("ban_reason", false)]
    public void AMissingColumn_IsRefused_NamingIt(string column, bool addedMayBeMissing)
    {
        Dictionary<string, (string Type, int MaxBytes, bool Nullable)> found = FreshLayout();
        found.Remove(column);

        AccountSchemaException refusal = Refused(found, addedMayBeMissing);

        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
        Assert.Equal(AccountSchemaRefusal.ColumnMissing, refusal.Reason);
        Assert.Equal(column, refusal.ColumnName);
        AccountSchemaFinding finding = Assert.Single(refusal.Findings!);
        Assert.Equal(AccountSchemaRefusal.ColumnMissing, finding.Reason);
        Assert.Equal(column, finding.ColumnName);
        Assert.Null(finding.ExpectedType);
        Assert.Null(finding.MinimumCharacters);

        Assert.False(SqlServerAccountSchema.CheckColumns(FreshLayout(), AccountSchemaMode.ValidateOnly, addedMayBeMissing));
    }

    [Fact]
    public void TheFourAddedColumns_MayBeMissingOnlyWhileTheWideningCanStillAddThem()
    {
        string[] added = { "ban_reason", "ban_until", "created_at_utc", "updated_at_utc" };
        Dictionary<string, (string Type, int MaxBytes, bool Nullable)> found = FreshLayout();
        foreach (string column in added) found.Remove(column);

        Assert.False(SqlServerAccountSchema.CheckColumns(found, AccountSchemaMode.AutoCreate, addedMayBeMissing: true));

        AccountSchemaException refusal = Refused(found);
        Assert.Equal(added, refusal.Findings!.Select(finding => finding.ColumnName));
        Assert.All(refusal.Findings!, finding => Assert.Equal(AccountSchemaRefusal.ColumnMissing, finding.Reason));
    }

    // The last row is narrow as well as the wrong type, so it names the branch that comes first: a width means nothing
    // on a column of another type.
    [Theory]
    [InlineData("created_at_utc", "datetime2", 8, "datetimeoffset")]
    [InlineData("banned", "int", 4, "bit")]
    [InlineData("subject", "varchar", 1, "nvarchar")]
    public void AColumnOfAnotherType_IsRefused_NamingBothTypes(string column, string actual, int maxBytes, string expected)
    {
        Dictionary<string, (string Type, int MaxBytes, bool Nullable)> found = FreshLayout();
        found[column] = (actual, maxBytes, true);

        AccountSchemaException refusal = Refused(found, addedMayBeMissing: true);

        Assert.Equal(AccountSchemaRefusal.ColumnTypeMismatch, refusal.Reason);
        Assert.Equal(column, refusal.ColumnName);
        Assert.Equal(actual, refusal.ActualType);
        Assert.Equal(expected, refusal.ExpectedType);
        Assert.Null(refusal.ActualCharacters);
        Assert.Null(refusal.MinimumCharacters);
        AccountSchemaFinding finding = Assert.Single(refusal.Findings!);
        Assert.Equal(AccountSchemaRefusal.ColumnTypeMismatch, finding.Reason);
        Assert.Equal(actual, finding.ActualType);
        Assert.Equal(expected, finding.ExpectedType);

        found[column] = FreshLayout()[column];
        Assert.False(SqlServerAccountSchema.CheckColumns(found, AccountSchemaMode.ValidateOnly, addedMayBeMissing: true));
    }

    [Theory]
    [InlineData("subject", AccountStoreRules.MaxSubjectChars)]
    [InlineData("display_name", AccountStoreRules.MaxDisplayNameChars)]
    [InlineData("ban_reason", AccountStoreRules.MaxBanReasonChars)]
    public void ATextColumnOneCharacterUnderItsLimit_IsRefused_AndPassesAtTheLimitReported(string column, int limit)
    {
        Dictionary<string, (string Type, int MaxBytes, bool Nullable)> found = FreshLayout();
        found[column] = ("nvarchar", (limit - 1) * 2, true);

        AccountSchemaException refusal = Refused(found);

        Assert.Equal(AccountSchemaRefusal.ColumnTooNarrow, refusal.Reason);
        Assert.Equal(column, refusal.ColumnName);
        Assert.Equal(limit - 1, refusal.ActualCharacters);
        Assert.Equal(limit, refusal.MinimumCharacters);
        Assert.Null(refusal.ActualType);
        Assert.Null(refusal.ExpectedType);
        AccountSchemaFinding finding = Assert.Single(refusal.Findings!);
        Assert.Equal(AccountSchemaRefusal.ColumnTooNarrow, finding.Reason);
        Assert.Equal(limit - 1, finding.ActualCharacters);
        Assert.Equal(limit, finding.MinimumCharacters);

        found[column] = ("nvarchar", refusal.MinimumCharacters!.Value * 2, true);
        SqlServerAccountSchema.CheckColumns(found, AccountSchemaMode.ValidateOnly, addedMayBeMissing: false);

        // NVARCHAR(MAX) reports -1, which holds any limit.
        found[column] = ("nvarchar", -1, true);
        SqlServerAccountSchema.CheckColumns(found, AccountSchemaMode.ValidateOnly, addedMayBeMissing: false);
    }

    // A narrow display name precedes a mistyped flag, which precedes a missing expiry. Each row removes the columns
    // before its first, so a fixed priority among the kinds answers wrongly on at least one row.
    [Theory]
    [InlineData("display_name", AccountSchemaRefusal.ColumnTooNarrow)]
    [InlineData("whitelisted", AccountSchemaRefusal.ColumnTypeMismatch)]
    [InlineData("ban_until", AccountSchemaRefusal.ColumnMissing)]
    public void AnAggregateRefusal_TakesItsReasonFromTheFirstColumnInLayoutOrder_AndKeepsEveryFinding(
        string firstBroken, AccountSchemaRefusal expected)
    {
        var broken = new (string Column, AccountSchemaRefusal Reason)[]
        {
            ("display_name", AccountSchemaRefusal.ColumnTooNarrow),
            ("whitelisted", AccountSchemaRefusal.ColumnTypeMismatch),
            ("ban_until", AccountSchemaRefusal.ColumnMissing),
        };
        var kept = broken.SkipWhile(entry => entry.Column != firstBroken).ToArray();
        Dictionary<string, (string Type, int MaxBytes, bool Nullable)> found = FreshLayout();
        if (kept.Any(entry => entry.Column == "display_name")) found["display_name"] = ("nvarchar", 2, true);
        if (kept.Any(entry => entry.Column == "whitelisted")) found["whitelisted"] = ("int", 4, false);
        found.Remove("ban_until");

        AccountSchemaException refusal = Refused(found);

        Assert.Equal(expected, refusal.Reason);
        Assert.Equal(firstBroken, refusal.ColumnName);
        Assert.Equal(kept, refusal.Findings!.Select(finding => (finding.ColumnName, finding.Reason)));
    }

    [Fact]
    public void TheFindings_AreASnapshotNoCallerCanChange()
    {
        var source = new List<AccountSchemaFinding>
        {
            new(AccountSchemaRefusal.ColumnMissing, "subject", "prose"),
        };
        var refusal = new AccountSchemaException(AccountSchemaRefusal.ColumnMissing, "prose") { Findings = source };

        source.Clear();

        Assert.Equal("subject", Assert.Single(refusal.Findings!).ColumnName);
        Assert.Throws<NotSupportedException>(() => ((IList<AccountSchemaFinding>)refusal.Findings!).Clear());
    }
}
