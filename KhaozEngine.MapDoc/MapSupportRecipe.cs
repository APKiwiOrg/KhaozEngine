namespace KhaozEngine.MapDoc;

/// <summary>The document-wide placement support contract.</summary>
public enum MapSupportRecipe { LegacyXzCallbackV1 = 1, AuthoredBindingsV2 = 2 }

public enum MapSupportBindingKind : byte { Surface, Space }

/// <summary>An authored support target and optional vertical search interval.</summary>
public sealed record MapSupportBinding(MapSupportBindingKind Kind, string? SurfaceId, string? SpaceId,
    float? ReferenceY, float? SearchBelow, float? SearchAbove);
