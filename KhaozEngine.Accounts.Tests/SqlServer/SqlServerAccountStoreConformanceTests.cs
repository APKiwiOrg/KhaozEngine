using System;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// The conformance suite against <see cref="SqlServerAccountStore"/> over FRESH engine tables, gated on
/// <c>KE_ACCOUNTS_SQLSERVER</c>. Each store gets a table of its own in the one test database, created lazily by its
/// first call under <see cref="AccountSchemaMode.AutoCreate"/> and dropped when the test instance is disposed, so
/// classes and facts never share rows and need no serialized collection.
/// <para>
/// <b>Every fact is overridden to carry <see cref="AccountsSqlServerFactAttribute"/>.</b> xUnit decides a skip at
/// discovery from the attribute on the method, and an inherited <c>[Fact]</c> would run here with no instance to run
/// against, so the one-line overrides below are what turn the inherited suite into a gated one. They add nothing
/// else.
/// </para>
/// </summary>
public sealed class SqlServerAccountStoreConformanceTests : AccountStoreConformance, IDisposable
{
    private SqlServerAccountDatabase? database;

    /// <inheritdoc />
    protected override IAccountStore NewStore(bool whitelistOnCreate) =>
        new SqlServerAccountStore(Database.ConnectionString, whitelistOnCreate,
            new SqlServerAccountStoreOptions(Table: Database.NewTableName()));

    public void Dispose() => database?.Dispose();

    // Opened on first use, because a skipped fact never runs and the fixture requires the variable.
    private SqlServerAccountDatabase Database => database ??= new SqlServerAccountDatabase();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task List_RunsInOrdinalOrder() => base.List_RunsInOrdinalOrder();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task List_PagesByKeyset_UntilAnEmptyPage() => base.List_PagesByKeyset_UntilAnEmptyPage();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task List_AfterASubjectNoAccountHas_StartsAtItsPosition() => base.List_AfterASubjectNoAccountHas_StartsAtItsPosition();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task List_ReturnsWholeRecords() => base.List_ReturnsWholeRecords();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ListBanned_HoldsEveryFiledBan_LapsedIncluded_InOrdinalOrder() => base.ListBanned_HoldsEveryFiledBan_LapsedIncluded_InOrdinalOrder();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task SignIn_RefusesAProviderSubjectWithADot_AndCreatesNothing() => base.SignIn_RefusesAProviderSubjectWithADot_AndCreatesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task SignIn_RefusesTheReservedGuestPrefix_AndCreatesNothing() => base.SignIn_RefusesTheReservedGuestPrefix_AndCreatesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task SignIn_RefusesAMalformedProviderIdOrSubject_AndCreatesNothing() => base.SignIn_RefusesAMalformedProviderIdOrSubject_AndCreatesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task SignIn_RefusesAnOverlongSubjectOrName_AndCreatesNothing() => base.SignIn_RefusesAnOverlongSubjectOrName_AndCreatesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ARepeatSignIn_WithAnOverlongName_ChangesNothing() => base.ARepeatSignIn_WithAnOverlongName_ChangesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Ban_RefusesAnOverlongOrNullReason_AndChangesNothing() => base.Ban_RefusesAnOverlongOrNullReason_AndChangesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ReadsAndWrites_RefuseANullOrEmptySubject() => base.ReadsAndWrites_RefuseANullOrEmptySubject();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task List_RefusesANonPositiveLimit() => base.List_RefusesANonPositiveLimit();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Writes_OnAnUnknownSubject_ReturnNull_AndCreateNothing() => base.Writes_OnAnUnknownSubject_ReturnNull_AndCreateNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task SetWhitelisted_ReturnsTheAccountAsItStandsAfterwards_AndTouchesNothingElse() => base.SetWhitelisted_ReturnsTheAccountAsItStandsAfterwards_AndTouchesNothingElse();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Ban_FilesTheReasonAndExpiry_AndReturnsTheAccount() => base.Ban_FilesTheReasonAndExpiry_AndReturnsTheAccount();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task APermanentBan_HasNoExpiry() => base.APermanentBan_HasNoExpiry();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ALapsedTimedBan_StaysFiled_ThroughASignIn_AndInTheBannedList() => base.ALapsedTimedBan_StaysFiled_ThroughASignIn_AndInTheBannedList();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Unban_ClearsTheReasonAndTheExpiry() => base.Unban_ClearsTheReasonAndTheExpiry();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Unban_OfAnAccountWithNoBan_ReturnsItUnchanged() => base.Unban_OfAnAccountWithNoBan_ReturnsItUnchanged();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ABanOnABannedAccount_ReplacesTheReasonAndTheExpiry() => base.ABanOnABannedAccount_ReplacesTheReasonAndTheExpiry();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ABanExpiry_ReadsBackAsTheSameInstant_InUtc() => base.ABanExpiry_ReadsBackAsTheSameInstant_InUtc();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task FirstSignIn_MintsProviderColonSubject_AndStoresTheName() => base.FirstSignIn_MintsProviderColonSubject_AndStoresTheName();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task FirstSignIn_AppliesWhitelistOnCreate() => base.FirstSignIn_AppliesWhitelistOnCreate();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task WhitelistOnCreate_IsCreateOnly_SoARepeatKeepsTheStoredFlag() => base.WhitelistOnCreate_IsCreateOnly_SoARepeatKeepsTheStoredFlag();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task RepeatSignIn_RefreshesTheName_AndKeepsTheWhitelistAndTheBan() => base.RepeatSignIn_RefreshesTheName_AndKeepsTheWhitelistAndTheBan();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ASignInWithNoName_StoresNone_AndARepeatWithNoName_KeepsTheStoredOne() => base.ASignInWithNoName_StoresNone_AndARepeatWithNoName_KeepsTheStoredOne();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ConcurrentFirstSignIns_ProduceOneAccount() => base.ConcurrentFirstSignIns_ProduceOneAccount();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Find_OfAnUnknownSubject_IsNull_AndCreatesNothing() => base.Find_OfAnUnknownSubject_IsNull_AndCreatesNothing();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task Subjects_CompareByCodePoint_SoCaseMakesTwoAccounts() => base.Subjects_CompareByCodePoint_SoCaseMakesTwoAccounts();

    /// <inheritdoc />
    [AccountsSqlServerFact]
    public override Task ValuesAtTheLimits_RoundTripExactly() => base.ValuesAtTheLimits_RoundTripExactly();
}
