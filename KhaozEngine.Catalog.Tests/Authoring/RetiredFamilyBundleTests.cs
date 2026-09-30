using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

public class RetiredFamilyBundleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFamilyRetirementFlagSurvivesImportReadsAndExport(bool isRetired)
    {
        ContentTypeId type = new(PublishFixtures.ThingTypeId);
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "seed",
            0,
            [],
            [new ContentBundleRow(type, 16, new ContentKey("sword"), false, "swords", PublishFixtures.Fields(2))],
            [new ContentFamily(7, type, "swords", 16, isRetired, 1, [new(7, 0, 16, 16, 17, 1)])],
            []);
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        using var firstRoot = new TemporaryRoot();
        var first = PublishFixtures.Store(registry, new FileSystemPackStore(firstRoot.Path));

        await first.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed");

        ContentFamily? read = await first.ReadFamilyAsync(7);
        Assert.NotNull(read);
        Assert.Equal(isRetired, read.IsRetired);
        Assert.Equal(isRetired, Assert.Single(await first.ListFamiliesAsync(type)).IsRetired);

        ContentBundle exported = await first.ExportBundleAsync(1);
        ContentFamily family = Assert.Single(exported.Families);
        Assert.Equal(isRetired, family.IsRetired);
        Assert.Equal(7, family.FamilyId);
        Assert.Equal("swords", family.FamilyKey);
        Assert.Equal(new ContentFamilyBlock(7, 0, 16, 16, 17, 1), Assert.Single(family.Blocks));

        using var secondRoot = new TemporaryRoot();
        var second = PublishFixtures.Store(registry, new FileSystemPackStore(secondRoot.Path));
        ContentBundle document = ContentBundleJson.Read(ContentBundleJson.Write(exported));
        await second.ImportBundleAsync(document, PublishFixtures.Actor, "oid:tests", "reimport");

        Assert.Equal(isRetired, Assert.Single((await second.ExportBundleAsync(1)).Families).IsRetired);
    }
}
