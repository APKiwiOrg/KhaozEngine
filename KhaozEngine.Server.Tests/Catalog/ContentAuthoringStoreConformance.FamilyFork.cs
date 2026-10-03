using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// A family fork across every provider (issue 908): the copy is allocated inside the family's blocks and a
/// lossless export of it imports unchanged. A copy forked before 20.19.0, which holds a plain id while naming
/// the family, is refused on import with nothing left behind.
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>The family key both family fork facts declare.</summary>
    const string ForkFamilyKey = "swords";

    /// <summary>
    /// A fork of a family row allocates its copy inside the family's block, and export then import into an
    /// empty store reproduces both rows, their family, the block and the MovedToLegacy rule.
    /// </summary>
    [Fact]
    public virtual async Task AFamilyForkRoundTripsThroughExportAndImportWithTheCopyInsideTheBlock()
    {
        IContentAuthoringStore source = await OpenAsync();
        ContentFamily family = await source.CreateFamilyAsync(
            Thing, ForkFamilyKey, ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        Assert.Equal(16, Assert.Single(family.Blocks).BaseId);
        await PublishAsync(
            source, ContentEdit.Add(Thing, new ContentKey("source"), CatalogFixtures.Fields(10), family.FamilyId));
        ContentPublishResult forked = await PublishAsync(
            source,
            ContentEdit.Fork(
                Thing,
                16,
                new ContentKey("source"),
                new ContentKey("source_legacy"),
                CatalogFixtures.LegacyField,
                CatalogFixtures.Fields(25)));
        Assert.Equal(2, forked.VersionNumber);
        Assert.Equal(1, forked.RulesAppended);

        ContentRowPage page = await RowsAsync(source);
        Assert.Equal(new[] { 16, 17 }, Ids(page));
        Assert.Equal(new[] { "source", "source_legacy" }, Keys(page));
        Assert.Equal(family.FamilyId, (await source.GetRowHistoryAsync(Thing, 17))[0].FamilyId);

        ContentBundle exported = await source.ExportBundleAsync(2);
        Assert.Equal(new int?[] { 16, 17 }, new int?[] { exported.Rows[0].Id, exported.Rows[1].Id });
        Assert.Equal(ForkFamilyKey, exported.Rows[0].FamilyKey);
        Assert.Equal(ForkFamilyKey, exported.Rows[1].FamilyKey);
        ContentFamily exportedFamily = Assert.Single(exported.Families);
        ContentFamilyBlock block = Assert.Single(exportedFamily.Blocks);
        Assert.Equal(16, block.BaseId);
        Assert.Equal(16, block.BlockSize);
        Assert.Equal(18, block.NextFreeId);
        RemapRule rule = Assert.Single(exported.Rules);
        Assert.Equal(RemapRuleKind.MovedToLegacy, rule.Kind);
        Assert.Equal(16, rule.FromId);
        Assert.Equal(17, rule.ToId);
        Assert.Equal(2, rule.IntroducedIn);

        IContentAuthoringStore target = await ResetToEmptyAsync();
        ContentPublishResult imported = await target.ImportBundleAsync(
            exported, CatalogFixtures.Actor, CatalogFixtures.Operator, "family fork");
        Assert.Equal(1, imported.VersionNumber);

        ContentBundle again = await target.ExportBundleAsync(1);
        Assert.Equal(exported.Rows.Count, again.Rows.Count);
        for (int i = 0; i < exported.Rows.Count; i++)
        {
            Assert.Equal(exported.Rows[i].Id, again.Rows[i].Id);
            Assert.Equal(exported.Rows[i].Key, again.Rows[i].Key);
            Assert.Equal(exported.Rows[i].FamilyKey, again.Rows[i].FamilyKey);
            Assert.Equal(exported.Rows[i].IsRetired, again.Rows[i].IsRetired);
            Assert.Equal(exported.Rows[i].Fields, again.Rows[i].Fields);
        }

        ContentFamily restored = Assert.Single(again.Families);
        Assert.Equal(exportedFamily.FamilyId, restored.FamilyId);
        Assert.Equal(ForkFamilyKey, restored.FamilyKey);
        Assert.Equal(exportedFamily.Blocks, restored.Blocks);
        RemapRule carried = Assert.Single(again.Rules);
        Assert.Equal(1, carried.Sequence);
        Assert.Equal(1, carried.IntroducedIn);
        Assert.Equal(RemapRuleKind.MovedToLegacy, carried.Kind);
        Assert.Equal(16, carried.FromId);
        Assert.Equal(17, carried.ToId);
        Assert.Equal(exportedFamily.FamilyId, (await target.GetRowHistoryAsync(Thing, 17))[0].FamilyId);
    }

    /// <summary>
    /// The shape 19.0.0 through 20.18.0 exported: the copy holds plain id 32 outside the family's block
    /// [16,32) and still names the family. Import refuses it with KEC0037 and leaves the store empty.
    /// </summary>
    [Fact]
    public virtual async Task ALegacyFamilyForkCopyOutsideEveryBlockIsRefusedOnImportAndLeavesNothing()
    {
        IContentAuthoringStore store = await ResetToEmptyAsync();
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "legacy-store",
            2,
            [],
            [
                new ContentBundleRow(
                    Thing, 16, new ContentKey("source"), false, ForkFamilyKey, CatalogFixtures.Fields(25)),
                new ContentBundleRow(
                    Thing,
                    32,
                    new ContentKey("source_legacy"),
                    false,
                    ForkFamilyKey,
                    [.. CatalogFixtures.Fields(10), .. CatalogFixtures.Legacy(true)]),
            ],
            [
                new ContentFamily(
                    1, Thing, ForkFamilyKey, 16, false, 1, [new ContentFamilyBlock(1, 0, 16, 16, 17, 1)]),
            ],
            [new RemapRule(1, 2, Thing, RemapRuleKind.MovedToLegacy, 16, 32, [])]);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(bundle, CatalogFixtures.Actor, CatalogFixtures.Operator, "legacy"));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        ContentFinding finding = Assert.Single(refused.Findings);
        Assert.Equal(ContentCarriedIdCodes.Family, finding.Code);
        Assert.Equal(32, finding.Id);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Empty(await store.ListFamiliesAsync(default));
        Assert.Empty(Ids(await RowsAsync(store, includeRetired: true)));
        IReadOnlyList<RemapRule> rules = await RulesAsync(store);
        Assert.Empty(rules);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(new ContentIdHighWater(0, 0), await Ids(store).ReadHighWaterAsync(Thing));
    }
}
