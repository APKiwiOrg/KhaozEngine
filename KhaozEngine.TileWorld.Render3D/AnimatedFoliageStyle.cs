using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Motion and shadow casting for one selected animated foliage archetype.</summary>
public readonly record struct AnimatedFoliageStyle(FoliageMotionProfile Motion, bool CastsShadows);
