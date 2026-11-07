using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldSchemaTests
{
    [Fact]
    public void Missing_embedded_schema_names_the_resource_without_poisoning_the_real_schema()
    {
        string schema = TileWorldSchema.GetCatalogJson();
        Assert.NotEmpty(schema);

        const string missing = "KhaozEngine.TileWorld.missing.schema.json";
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileWorldSchema.ReadMissingResourceForTests(missing));

        Assert.Equal(TileWorldRefusal.SchemaResourceMissing, ex.Reason);
        Assert.Equal(missing, ex.ResourceName);
        Assert.Equal(schema, TileWorldSchema.GetCatalogJson());
    }
}
