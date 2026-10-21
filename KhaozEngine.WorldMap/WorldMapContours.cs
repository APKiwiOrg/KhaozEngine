using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap;

/// <summary>Optional dry-ground elevation lines, measured in height metres and raster pixels.</summary>
/// <param name="IntervalMetres">The finite positive height interval between contour lines.</param>
/// <param name="Color">The contour colour, composited over terrain relief using its alpha.</param>
/// <param name="WidthPixels">The finite positive line width at every raster level.</param>
public sealed record WorldMapContours(float IntervalMetres, Color Color, float WidthPixels = 0.75f);
