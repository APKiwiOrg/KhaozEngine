using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Rollback refusals render the rule metadata the authoring provider actually returned.</summary>
public sealed class CatalogRollbackBlockerActionTests
{
    [Theory]
    [InlineData(false, null, 0, 0)]
    [InlineData(true, "Retired", 1, 2)]
    public async Task Rollback_RendersOnlyTheRetiringRulePresentInTheBaseline(
        bool includeRetiringRule,
        string? expectedKind,
        int expectedSequence,
        int expectedIntroducedIn)
    {
        using var harness = new CatalogActionHarness();
        await harness.PublishThingsAsync("stone_sword");
        int sword = await harness.IdOfAsync("stone_sword");
        await harness.PublishAsync(ContentEdit.Retire(
            CatalogActionHarness.Thing, sword, new ContentKey("stone_sword"), ContentRetirePolicy.Placeholder, 0));

        ContentPublishBaseline published = await harness.Store.ReadPublishBaselineAsync();
        var baseline = new ContentPublishBaseline(
            published.VersionNumber,
            published.Rows,
            includeRetiringRule ? published.Rules : [],
            published.Chunks,
            published.Languages,
            published.MinimumServerBuild,
            published.MinimumClientBuild);

        // Normal publishes append a retire rule. This public provider seam can answer a baseline
        // without it, while rollback and target row paging still execute against the real history.
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, new RollbackBaselineStore(harness.Store, baseline), harness.Registry);

        AdminActionResult result = await CatalogActionHarness.DispatchAsync(
            admin, "catalog-rollback", """{ "toVersion": 1 }""");
        Assert.Equal(AdminActionStatus.Conflict, result.Status);
        JsonElement body = CatalogActionHarness.Wire(result);

        Assert.Equal("KEC0039", body.GetProperty("code").GetString());
        Assert.Equal(ContentAuthoringException.RetireIrreversibleReason, body.GetProperty("reason").GetString());
        JsonElement rule = body.GetProperty("blockedByRules").EnumerateArray().Single();
        Assert.Equal(expectedSequence, rule.GetProperty("sequence").GetInt32());
        Assert.Equal(expectedIntroducedIn, rule.GetProperty("introducedIn").GetInt32());
        Assert.Equal("thing", rule.GetProperty("type").GetString());
        Assert.Equal(sword, rule.GetProperty("fromId").GetInt32());
        JsonElement kind = rule.GetProperty("kind");
        Assert.Equal(expectedKind is null ? JsonValueKind.Null : JsonValueKind.String, kind.ValueKind);
        Assert.Equal(expectedKind, kind.GetString());
        Assert.Contains("new key", body.GetProperty("remedy").GetString()!);
        Assert.Null(await harness.Store.GetOpenDraftAsync());
        Assert.Equal(2, await harness.Store.GetActiveVersionAsync());
    }
}

/// <summary>Supplies one baseline at the public provider seam while preserving the real store's operations.</summary>
internal sealed class RollbackBaselineStore(IContentAuthoringStore inner, ContentPublishBaseline baseline)
    : ForwardingContentAuthoringStore(inner)
{
    /// <inheritdoc />
    public override Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(baseline);
}
