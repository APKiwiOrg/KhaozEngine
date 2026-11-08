using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

// Regression #1700. Request JSON never controls the host's publish policy.
public sealed class CatalogConsumerPublishPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData(", \"publishPolicy\": null, \"skipPolicy\": true, \"bypass\": true")]
    [InlineData(", \"publishPolicy\": {}, \"upgrade\": { \"id\": \"spoofed\" }")]
    public async Task PreviewAndPublishUseTheHostPolicyRegardlessOfRequestFields(string extra)
    {
        using var harness = new CatalogActionHarness(new CatalogAdminActionOptions
        {
            PublishPolicy = new GuardValue(),
        });
        await harness.PublishThingsAsync("artifact_value");
        int id = await harness.IdOfAsync("artifact_value");
        await harness.OkAsync("catalog-edit", $$"""
            { "edits": [{ "op": "update", "typeKey": "thing", "id": {{id}}, "fields": { "value": 4500 } }] }
            """);

        JsonElement preview = await harness.OkAsync("catalog-validate", "{" + extra.TrimStart(',', ' ') + "}");
        Assert.False(preview.GetProperty("valid").GetBoolean());
        AssertFinding(preview, id);
        JsonElement refused = await harness.RefusedAsync("catalog-publish", "{\"expectedBaseVersion\":1" + extra + "}");
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.GetProperty("reason").GetString());
        AssertFinding(refused, id);
        Assert.Equal(1, await harness.Store.GetActiveVersionAsync());
        Assert.NotNull(await harness.Store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task ANewEditAfterPreviewCannotUseTheEarlierPassingResult()
    {
        using var harness = new CatalogActionHarness(new CatalogAdminActionOptions { PublishPolicy = new GuardValue() });
        await harness.PublishThingsAsync("artifact_value");
        int id = await harness.IdOfAsync("artifact_value");
        await harness.OkAsync("catalog-edit", $$"""
            { "edits": [{ "op": "update", "typeKey": "thing", "id": {{id}}, "fields": { "stackable": true } }] }
            """);
        Assert.True((await harness.OkAsync("catalog-validate", null)).GetProperty("valid").GetBoolean());
        await harness.OkAsync("catalog-edit", $$"""
            { "edits": [{ "op": "update", "typeKey": "thing", "id": {{id}}, "fields": { "value": 4500 } }] }
            """);

        AssertFinding(await harness.RefusedAsync("catalog-publish", """{ "expectedBaseVersion": 1 }"""), id);
    }

    [Fact]
    public async Task HttpSeedCannotBypassHostPolicyButTrustedSeedRemainsAvailable()
    {
        using var source = new CatalogActionHarness();
        await source.PublishThingsAsync("artifact_value");
        int id = await source.IdOfAsync("artifact_value");
        string bundle = (await source.OkAsync("catalog-export", null)).GetProperty("bundle").GetRawText();
        using var target = new CatalogActionHarness(new CatalogAdminActionOptions
        {
            PublishPolicy = new GuardValue(rejectSeed: true),
        });

        JsonElement refused = await target.RefusedAsync("catalog-import", $$"""
            { "bundle": {{bundle}}, "publishPolicy": null, "bypass": true }
            """);

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.GetProperty("reason").GetString());
        JsonElement finding = Assert.Single(refused.GetProperty("findings").EnumerateArray(),
            value => value.GetProperty("code").GetString() == "artifact-rebuild-required");
        Assert.Equal("artifact_value", finding.GetProperty("key").GetString());
        Assert.Equal(id, finding.GetProperty("id").GetInt32());
        Assert.Equal(0, await target.Store.GetActiveVersionAsync());
        Assert.Null(await target.Store.GetOpenDraftAsync());
        Assert.Equal(1, (await target.Store.ImportTextBundleAsync(ContentBundleJson.Read(bundle),
            "trusted-seed", "server", "matching artifacts")).VersionNumber);
    }

    [Fact]
    public async Task AStoreWithoutPolicyImportSupportRefusesRatherThanDroppingThePolicy()
    {
        using var source = new CatalogActionHarness();
        await source.PublishThingsAsync("artifact_value");
        string bundle = (await source.OkAsync("catalog-export", null)).GetProperty("bundle").GetRawText();
        using var target = new CatalogActionHarness();
        var admin = CatalogActionHarness.NewSurface();
        var store = new LegacyImportStore(target.Store);
        var policy = new GuardValue(rejectSeed: true);
        CatalogAdminActions.Register(admin, store, target.Registry,
            new CatalogAdminActionOptions { PublishPolicy = policy });
        string before = await target.SnapshotAsync();
        string epochBefore = await target.Store.GetStoreEpochAsync();
        ContentBundle input = ContentBundleJson.Read(bundle);
        var failure = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            CatalogOperatorImport.ImportAsync(store, input, CatalogActionHarness.Operator, "seed", policy, default));
        Assert.Equal(ContentAuthoringRefusal.PolicyImportCapabilityMissing, failure.Refusal);
        Assert.Equal(typeof(IContentPolicyImportStore), failure.RequiredCapability);
        Assert.Equal("policy-import-unavailable", failure.Reason);
        Assert.Equal(default, failure.Type);
        Assert.Equal(0, failure.Id);
        Assert.Empty(failure.Findings);

        var result = await CatalogActionHarness.DispatchAsync(admin, "catalog-import", $$"""{ "bundle": {{bundle}} }""");

        Assert.Equal(KhaozEngine.NetWorld.AdminActionStatus.BadRequest, result.Status);
        Assert.Equal("policy-import-unavailable",
            CatalogActionHarness.Wire(result).GetProperty("reason").GetString());
        Assert.Equal(before, await target.SnapshotAsync());
        Assert.Equal(epochBefore, await target.Store.GetStoreEpochAsync());
        Assert.Equal(0, await target.Store.GetActiveVersionAsync());
        Assert.Null(await target.Store.GetOpenDraftAsync());

        ContentPublishResult seeded = await CatalogOperatorImport.ImportAsync(
            store, input, CatalogActionHarness.Operator, "trusted seed", null, default);
        Assert.Equal(1, seeded.VersionNumber);
        Assert.Equal("artifact_value", Assert.Single((await target.Store.ExportBundleAsync(1)).Rows).Key.ToString());
    }

    sealed class LegacyImportStore(IContentAuthoringStore inner) : ForwardingContentAuthoringStore(inner);

    static void AssertFinding(JsonElement body, int id)
    {
        Assert.Equal(1, body.GetProperty("findingCount").GetInt32());
        JsonElement finding = Assert.Single(body.GetProperty("findings").EnumerateArray());
        Assert.Equal("artifact-rebuild-required", finding.GetProperty("code").GetString());
        Assert.Equal("thing", finding.GetProperty("type").GetString());
        Assert.Equal(id, finding.GetProperty("id").GetInt32());
        Assert.Equal("artifact_value", finding.GetProperty("key").GetString());
    }

    sealed class GuardValue(bool rejectSeed = false) : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings)
        {
            foreach (ContentPublishPolicyRow row in candidate.Rows(CatalogActionHarness.Thing))
            {
                bool existing = baseline.TryGetRow(row.Type, row.Key, out ContentPublishPolicyRow? old);
                if (existing ? old!.GetValue(CatalogActionHarness.ValueField) != row.GetValue(CatalogActionHarness.ValueField) : rejectSeed)
                {
                    findings.Add(row.Finding("artifact-rebuild-required", "Matching baked artifacts are required."));
                }
            }
        }
    }
}
