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

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ContentFieldLookup.IndexIn(runtime, ItemType, "heals"));

        Assert.Contains(EngineContentTypes.ItemTypeKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("heals", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnregisteredTypeIsARefusalToo()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ContentFieldLookup.IndexIn(runtime, new ContentTypeId(4096), "value"));

        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
        Assert.Contains("value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullRuntimeOrFieldIsRefused()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        Assert.Throws<ArgumentNullException>(() => ContentFieldLookup.IndexIn(null!, ItemType, "value"));
        Assert.Throws<ArgumentNullException>(() => ContentFieldLookup.IndexIn(runtime, ItemType, null!));
    }
}
