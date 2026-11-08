using System;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Runtime;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Registry;

/// <summary>
/// By-name field resolution against a LOADED runtime, and its loud policy: a field the runtime's schema
/// does not carry is a refusal naming both the type and the field, never a -1 a caller reads as zero.
/// </summary>
public class ContentFieldLookupTests
{
    static readonly ContentTypeId ItemType = new(EngineContentTypes.ItemTypeId);

    [Fact]
    public void ANamedFieldResolvesToItsPositionInTheLoadedSchema()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out ContentTypeRegistry registry);
        Assert.True(registry.TryGet(ItemType, out ContentTypeRegistration? registration));

        Assert.Equal(
            registration.Schema.IndexOf(ItemContentType.MaxStackField),
            ContentFieldLookup.IndexIn(runtime, ItemType, ItemContentType.MaxStackField));
    }

    [Fact]
    public void AScaledFieldHandsBackTheSchemasOwnScale()
    {
        // The scale is READ rather than written down beside the reader: a publish that retuned it would
        // otherwise leave every answer off by a factor with nothing failing.
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        int index = ContentFieldLookup.IndexIn(runtime, ItemType, ItemContentType.IconTiltField, out int scale);

        Assert.True(index > 0);
        Assert.Equal(1000, scale);

        // Every other kind carries 1, which is what lets one read path divide unconditionally.
        ContentFieldLookup.IndexIn(runtime, ItemType, ItemContentType.MaxStackField, out int plain);
        Assert.Equal(1, plain);
    }

    [Fact]
    public void AFieldTheSchemaLacksIsARefusalNamingTheTypeAndTheField()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        ContentFieldLookupException error = Assert.Throws<ContentFieldLookupException>(
            () => ContentFieldLookup.IndexIn(runtime, ItemType, "heals"));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(ContentFieldLookupRefusal.FieldUndeclared, error.Reason);
        Assert.Equal(ItemType, error.Type);
        Assert.Equal("item", error.TypeKey);
        Assert.Equal("heals", error.FieldName);
        Assert.True(ContentFieldLookup.IndexIn(runtime, ItemType, ItemContentType.MaxStackField) >= 0);
    }

    [Fact]
    public void AnUnregisteredTypeIsARefusalToo()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out ContentTypeRegistry registry);

        ContentFieldLookupException error = Assert.Throws<ContentFieldLookupException>(
            () => ContentFieldLookup.IndexIn(runtime, new ContentTypeId(4096), "value"));

        Assert.Equal(ContentFieldLookupRefusal.TypeUnregistered, error.Reason);
        Assert.Equal(new ContentTypeId(4096), error.Type);
        Assert.Null(error.TypeKey);
        Assert.Equal("value", error.FieldName);

        ContentFieldSchema schema = ItemContentType.CreateSchema();
        registry.RegisterContentType(ContentRegistrationBand.Game, 4096, "tuning",
            new ItemContentType.Codec(new ContentTypeId(4096), schema), null,
            schema, ContentVisibility.Client, 256);
        Assert.True(ContentFieldLookup.IndexIn(runtime, new ContentTypeId(4096), "value") >= 0);
    }

    [Fact]
    public void ANullRuntimeOrFieldIsRefused()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        Assert.Throws<ArgumentNullException>(() => ContentFieldLookup.IndexIn(null!, ItemType, "value"));
        Assert.Throws<ArgumentNullException>(() => ContentFieldLookup.IndexIn(runtime, ItemType, null!));
    }
}
