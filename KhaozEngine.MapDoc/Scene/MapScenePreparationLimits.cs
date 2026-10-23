using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Optional caller ceilings for scene preparation. Omitted families keep their released allowances.</summary>
public sealed record MapScenePreparationLimits(MapFoliageLimits? Foliage = null,
    MapFoliageContextLimits? FoliageContext = null, MapWaterCompileLimits? Water = null);
