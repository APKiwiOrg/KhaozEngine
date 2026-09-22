using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// An upgrade onto a catalog whose id marks sit ABOVE its highest row id, which is where the whole carried-id
/// rule comes from.
/// <para>
/// <b>A publish refused after step 3 burns the ids it reserved.</b> That is reserve before issue working
/// exactly as designed: the durable promise lands first, so a refusal later skips numbers rather than handing
/// one out twice. One failed operator publish is enough, and no race is needed. A planner that predicted the
/// allocator from the highest ROW id would pass its own check here and then watch the publish issue a higher
/// number, filing the new row under an id the committed bundle names nothing by. A game names its definition
/// ids as code constants, so that is a silent stable-id violation and nothing afterwards can see it.
/// </para>
/// <para>
/// Refusing the upgrade instead would dead-end the catalog forever, because a burnt id cannot be unburnt. The
/// answer is that the add CARRIES the committed id, and this is the regression that says so end to end, down
/// to the strict boot reading the row back.
/// </para>
/// </summary>
public sealed class CatalogUpgradeAfterBurntIdsTests
{
    /// <summary>The key that fails <c>KEC0001</c>, which is how a publish is refused AFTER it allocated.</summary>
    const string MalformedKey = "bad__key";

    static ContentTypeId Thing => UpgradeFixtures.Thing;

    static ContentTypeId Other => UpgradeFixtures.Other;

    [Fact]
    public async Task AnUpgradeOntoACatalogWithBurntIdsPublishesTheCommittedIdsExactly()
    {
        using var database = new TemporaryCatalogDatabase();
        var packs = database.Pack();
        ContentTypeRegistry superset = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);

        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, superset, packs))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
                UpgradeFixtures.Actor,
                UpgradeFixtures.Operator,
                "seed the older catalog");
            Assert.Equal(1, (await store.PublishAsync(PublishFixtures.Request(0))).VersionNumber);

            await BurnAnIdAsync(store);

            // The mark really is above the highest row id of the new type, which holds no rows at all.
            ContentIdHighWater burnt = await store.ReadHighWaterAsync(Other);
            Assert.True(burnt.IssuedThrough >= 1, "The refused publish burnt the id it had allocated.");
            Assert.Equal(0, (await store.ListRowsAsync(Other, 0, null, true, 0, 10)).Total);

            ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
                store, superset, SetFor(superset), UpgradeFixtures.Apply(expectedVersion: 1));

            Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
            Assert.Equal(2, report.ActiveVersionAfter);

            // EXACTLY the committed ids. The allocator would have issued 2 and 3 here.
            ContentRowPage rows = await store.ListRowsAsync(Other, 0, null, false, 0, 10);
            Assert.Equal(2, rows.Total);
            Assert.Equal(1, rows.Rows[0].Id);
            Assert.Equal("new_row", rows.Rows[0].Key.ToString());
            Assert.Equal(2, rows.Rows[1].Id);
            Assert.Equal("second_new_row", rows.Rows[1].Key.ToString());
            Assert.Null(await store.GetOpenDraftAsync());
        }

        superset.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = superset,
            Store = packs,
            Pointers = packs,
            Holder = holder,
            ConfiguredVersion = 2,
            ServerBuild = int.MaxValue,
            StoreName = "the burnt-id upgrade store",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.True(boot.Runtime!.TryGetRow(Other, 1, out ContentRow? first));
        Assert.Equal("new_row", first.Key.ToString());
        Assert.Equal(22, first.Fields[0].Number);
        Assert.True(boot.Runtime.TryGetRow(Other, 2, out ContentRow? second));
        Assert.Equal("second_new_row", second.Key.ToString());
        Assert.Equal(33, second.Fields[0].Number);
    }

    /// <summary>
    /// One publish refused by the VALIDATOR after step 3 had allocated, then its draft discarded. The
    /// malformed key is refused by <c>KEC0001</c> at step 4, which is past the allocation and past its two
    /// commits, so the id it took is gone for good.
    /// </summary>
    static async Task BurnAnIdAsync(SqliteContentAuthoringStore store)
    {
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Other, new ContentKey(MalformedKey), PublishFixtures.Fields(1))],
            "a-human-operator",
            "oid:human",
            "an operator publish that will be refused");
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(PublishFixtures.Request(1)));
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        await store.DiscardDraftAsync("a-human-operator", "oid:human");
    }

    /// <summary>The two definitions this build ships, which add the new type's first two committed rows.</summary>
    static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(Other, 1, "new_row", 22),
            UpgradeFixtures.Row(Other, 2, "second_new_row", 33));
        return new ContentUpgradeSet(UpgradeFixtures.Adds(
            "add-the-new-type",
            1,
            target,
            UpgradeFixtures.Identity(Other, "new_row"),
            UpgradeFixtures.Identity(Other, "second_new_row")));
    }
}
