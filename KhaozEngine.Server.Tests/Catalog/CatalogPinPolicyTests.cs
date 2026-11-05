using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

// Regression #1700, review F1. Operator version selection must honor the host's policy.
public sealed class CatalogPinPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIncompatibleHistoricalPinIsRefusedLikeTheRollbackDraft(bool sqlite)
    {
        using var fixture = new CatalogPinPolicyFixture(sqlite);
        await fixture.HistoryAsync();
        ServerAdmin admin = fixture.Register(new GuardPinValue());
        await CallOkAsync(admin, "catalog-rollback", """{ "toVersion": 1 }""");
        Assert.False((await CallOkAsync(admin, "catalog-validate", null)).GetProperty("valid").GetBoolean());

        await AssertGuardRefusalAsync(admin, """{ "version": 1, "publishPolicy": null, "bypass": true }""", fixture.Id);

        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
        Assert.Equal(2, await fixture.Store.GetActiveVersionAsync());
        Assert.NotNull(await fixture.Store.GetOpenDraftAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingGuardedValuesAndAnAlreadyEffectiveVersionAreAccepted(bool sqlite)
    {
        using var fixture = new CatalogPinPolicyFixture(sqlite);
        await fixture.HistoryAsync();
        await fixture.PublishAsync(4500, stackable: true);
        ServerAdmin admin = fixture.Register(new GuardPinValue());

        await CallOkAsync(admin, "catalog-pin", """{ "version": 2 }""");
        Assert.Equal(2, await fixture.Store.GetPinnedVersionAsync());
        await CallOkAsync(fixture.Register(new GuardPinValue(fail: true)), "catalog-pin", """{ "version": 2 }""");
        await CallOkAsync(admin, "catalog-pin", """{ "version": null }""");
        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
    }

    [Theory]
    [InlineData(false, 4200, 4500)]
    [InlineData(false, 4500, 4200)]
    [InlineData(true, 4200, 4500)]
    [InlineData(true, 4500, 4200)]
    public async Task UnpinUsesThePinnedBaselineAndRefusesEitherIncompatibleDirection(bool sqlite, int first, int active)
    {
        using var fixture = new CatalogPinPolicyFixture(sqlite);
        await fixture.HistoryAsync(first, active);
        await fixture.Store.SetPinnedVersionAsync(1, "trusted-recovery", "server");

        await AssertGuardRefusalAsync(fixture.Register(new GuardPinValue()), """{ "version": null }""", fixture.Id);

        Assert.Equal(1, await fixture.Store.GetPinnedVersionAsync());
        Assert.Equal(2, await fixture.Store.GetActiveVersionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyExceptionsRefusePinsAndUnpinsWithoutChangingTheHold(bool unpin)
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync();
        await fixture.Store.SetPinnedVersionAsync(unpin ? 1 : 2, "trusted-recovery", "server");
        ServerAdmin admin = fixture.Register(new GuardPinValue(fail: true));
        int? before = await fixture.Store.GetPinnedVersionAsync();

        JsonElement body = await CallRefusedAsync(admin, unpin ? """{ "version": null }""" : """{ "version": 1 }""");

        Assert.Equal(IContentPublishPolicy.FailureCode, Assert.Single(body.GetProperty("findings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(before, await fixture.Store.GetPinnedVersionAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnreadableCurrentOrTargetRowsRefuseWithoutWritingThePin(int unreadableVersion)
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync(4200, 4200);
        var store = new UnreadablePinRowsStore(fixture.Store, unreadableVersion);

        JsonElement body = await CallRefusedAsync(fixture.Register(new GuardPinValue(), store), """{ "version": 1 }""");

        Assert.Equal(IContentVersionSelectionStore.UnavailableCode, Assert.Single(body.GetProperty("findings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AConcurrentSelectionChangeCannotAuthorizeAnUncheckedUnpin(bool sqlite, bool changePin)
    {
        using var fixture = new CatalogPinPolicyFixture(sqlite);
        await fixture.HistoryAsync(4200, 4200);
        await fixture.Store.SetPinnedVersionAsync(1, "trusted-recovery", "server");
        var store = new ChangedPinSelectionStore(fixture.Store, changePin
            ? () => fixture.Store.SetPinnedVersionAsync(2, "trusted-recovery", "server")
            : () => fixture.PublishAsync(4500));

        JsonElement body = await CallRefusedAsync(fixture.Register(new GuardPinValue(), store), """{ "version": null }""");

        Assert.Equal(IContentVersionSelectionStore.UnavailableCode,
            Assert.Single(body.GetProperty("findings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(changePin ? 2 : 1, await fixture.Store.GetPinnedVersionAsync());
        Assert.Equal(changePin ? 2 : 3, await fixture.Store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task SnapshotIdentityMustMatchTheRequestedPublishedVersion()
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync(4200, 4200);

        JsonElement body = await CallRefusedAsync(fixture.Register(new GuardPinValue(),
            new MismatchedPinSnapshotStore(fixture.Store)), """{ "version": 1 }""");

        Assert.Equal(IContentVersionSelectionStore.UnavailableCode,
            Assert.Single(body.GetProperty("findings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
    }

    [Fact]
    public async Task MissingConditionalPinSupportRefusesInsteadOfWritingAnUncheckedHold()
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync(4200, 4200);

        JsonElement body = await CallRefusedAsync(fixture.Register(new GuardPinValue(),
            new UnsupportedPinSelectionStore(fixture.Store)), """{ "version": 1 }""");

        Assert.Equal(IContentVersionSelectionStore.UnavailableCode,
            Assert.Single(body.GetProperty("findings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
    }

    [Fact]
    public async Task AWriteAcknowledgementFailureIsNotReportedAsAPolicyRefusal()
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync(4200, 4200);
        ServerAdmin admin = fixture.Register(new GuardPinValue(), new FailedPinAcknowledgementStore(fixture.Store));

        await Assert.ThrowsAsync<IOException>(() => CatalogActionHarness.DispatchAsync(admin, "catalog-pin", """{ "version": 1 }"""));

        Assert.Equal(1, await fixture.Store.GetPinnedVersionAsync());
    }

    [Fact]
    public async Task NoPolicyKeepsTheExistingPinAndUnpinBehaviorWithoutReadingRows()
    {
        using var fixture = new CatalogPinPolicyFixture(false);
        await fixture.HistoryAsync();
        ServerAdmin admin = fixture.Register(null, new UnreadablePinRowsStore(fixture.Store, 1));

        await CallOkAsync(admin, "catalog-pin", """{ "version": 1 }""");
        Assert.Equal(1, await fixture.Store.GetPinnedVersionAsync());
        await CallOkAsync(admin, "catalog-pin", """{ "version": null }""");
        Assert.Null(await fixture.Store.GetPinnedVersionAsync());
    }

    static async Task AssertGuardRefusalAsync(ServerAdmin admin, string body, int id)
    {
        JsonElement result = await CallRefusedAsync(admin, body);
        JsonElement finding = Assert.Single(result.GetProperty("findings").EnumerateArray());
        Assert.Equal(CatalogPinPolicyFixture.Code, finding.GetProperty("code").GetString());
        Assert.Equal("thing", finding.GetProperty("type").GetString());
        Assert.Equal(id, finding.GetProperty("id").GetInt32());
        Assert.Equal(CatalogPinPolicyFixture.Key, finding.GetProperty("key").GetString());
    }

    static async Task<JsonElement> CallRefusedAsync(ServerAdmin admin, string body)
    {
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, "catalog-pin", body);
        Assert.Equal(AdminActionStatus.BadRequest, result.Status);
        JsonElement payload = CatalogActionHarness.Wire(result);
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, payload.GetProperty("reason").GetString());
        return payload;
    }

    static async Task<JsonElement> CallOkAsync(ServerAdmin admin, string action, string? body)
    {
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, action, body);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        return CatalogActionHarness.Wire(result);
    }
}
