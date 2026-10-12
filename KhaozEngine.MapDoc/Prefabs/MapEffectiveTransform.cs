namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>A persisted binary64 pose. Effective geometry composition is versioned separately.</summary>
public readonly record struct MapEffectiveTransform(double X, double Y, double Z, double YawRadians, double Scale);

/// <summary>The precise pose retained by a standalone resource after unpack.</summary>
public sealed record MapStandaloneResourceTransform(MapPrefabChildKind Kind, string OwnerId,
    MapEffectiveTransform Transform, string SourceDigest);
