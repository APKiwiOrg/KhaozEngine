using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// One content type as the catalog provider suites declare it: its id, its key, its default visibility and
/// its chunk slot count.
/// </summary>
/// <param name="TypeId">The stable numeric type id, always in the game band.</param>
/// <param name="TypeKey">The stable string type key.</param>
/// <param name="Visibility">The type's default visibility.</param>
/// <param name="ChunkSlots">Id slots per chunk.</param>
internal sealed record CatalogTypeSpec(
    ushort TypeId,
    string TypeKey,
    ContentVisibility Visibility = ContentVisibility.Client,
    int ChunkSlots = CatalogFixtures.ChunkSlots);

/// <summary>
/// The registry, the schema and the field edits every catalog provider suite in this project shares. It is a
/// deliberate sibling of <c>KhaozEngine.Catalog.Tests</c>'s own publish fixtures rather than a reference to
/// them: a test project's types are internal to it, and the two suites are in two assemblies because the
/// provider tests belong beside every other Commerce and WorldStore provider test.
/// <para>
/// The types are GAME band, because every rule under test here is about the STORE rather than about any
/// engine type's schema, and a two-field type keeps a row readable in a failure message.
/// </para>
/// </summary>
internal static class CatalogFixtures
{
    /// <summary>The first id the game band is entitled to, which the plain type takes.</summary>
    public const ushort ThingTypeId = 1024;

    /// <summary>The plain type's key.</summary>
    public const string ThingTypeKey = "thing";

    /// <summary>A second game-band type, for a fact that needs two.</summary>
    public const ushort OtherTypeId = 1025;

    /// <summary>The second type's key.</summary>
    public const string OtherTypeKey = "other_thing";

    /// <summary>The required <c>Client</c> int field every fixture row carries.</summary>
    public const string ValueField = "value";

    /// <summary>The optional <c>Bool</c> field, which is what a fork flags its copy with.</summary>
    public const string LegacyField = "legacy";

    /// <summary>The smallest legal slot count, so a second chunk is reachable with an id a test can read.</summary>
    public const int ChunkSlots = 256;

    /// <summary>The actor every fixture edit and publish carries.</summary>
    public const string Actor = "catalog-conformance";

    /// <summary>The operator identity every fixture edit and publish carries.</summary>
    public const string Operator = "oid:conformance";

    /// <summary>The plain type as a type id, which is what every seam member takes.</summary>
    public static ContentTypeId Thing => new(ThingTypeId);

    /// <summary>The second type as a type id.</summary>
    public static ContentTypeId Other => new(OtherTypeId);

    /// <summary>The one plain type every ordinary fact uses.</summary>
    public static CatalogTypeSpec ThingSpec => new(ThingTypeId, ThingTypeKey);

    /// <summary>A second plain type, for a fact that spans two.</summary>
    public static CatalogTypeSpec OtherSpec => new(OtherTypeId, OtherTypeKey);

    /// <summary>The schema of a fixture type: one required Client int and one optional Client bool.</summary>
    public static ContentFieldSchema Schema()
        => new(new List<ContentFieldEntry>
        {
            new(ValueField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, false),
        });

    /// <summary>A registry carrying the given types IN THE ORDER they are handed in, and nothing else.</summary>
    public static ContentTypeRegistry Registry(params CatalogTypeSpec[] specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var registry = new ContentTypeRegistry();
        foreach (CatalogTypeSpec spec in specs)
        {
            Register(registry, spec);
        }

        return registry;
    }

    /// <summary>Adds one more type to a registry that already carries some.</summary>
    public static void Register(ContentTypeRegistry registry, CatalogTypeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(spec);

        ContentFieldSchema schema = Schema();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            spec.TypeId,
            spec.TypeKey,
            new CatalogFixtureCodec(new ContentTypeId(spec.TypeId), schema),
            null,
            schema,
            spec.Visibility,
            spec.ChunkSlots);
    }

    /// <summary>The field edits one fixture row carries.</summary>
    public static ContentFieldEdit[] Fields(int value)
        => [new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, value))];

    /// <summary>The optional Bool field set to a value, which is what an update that merges fields adds.</summary>
    public static ContentFieldEdit[] Legacy(bool value)
        => [new ContentFieldEdit(LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, value ? 1 : 0))];
}

/// <summary>The generic positional walk with nothing added, which is what a fixture type registers.</summary>
internal sealed class CatalogFixtureCodec(ContentTypeId type, ContentFieldSchema schema)
    : ContentRowCodecBase(type, schema)
{
}
