using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog;

/// <summary>Registration fixtures shared by the catalog and item-instance suites.</summary>
internal static class ContentRegistryRefusalFixtures
{
    /// <summary>A registry occupying one type id under a different key, so only the numeric identity collides.</summary>
    public static ContentTypeRegistry WithOccupiedTypeId(ContentTypeRegistration declaration)
    {
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(declaration.Band, declaration.Type.Value, "occupied_type", declaration.Codec,
            declaration.Validator, declaration.Schema, declaration.DefaultVisibility, declaration.ChunkSlots,
            declaration.MaxRowBytes, declaration.MaxDefinitionId, declaration.LoadIndex);
        return registry;
    }
}
