namespace KhaozEngine.Navigation;

/// <summary>
/// One cell's raw surface sample for <see cref="NavGrid.FromSurfaces"/>: whether the cell is standable,
/// and if so its surface height and the headroom above it. A non-standable sample bakes the cell
/// blocked and its <see cref="Height"/> / <see cref="Headroom"/> are ignored.
/// </summary>
/// <param name="Standable">True when the cell has a walkable surface.</param>
/// <param name="Height">World Y of the surface top (read only when <paramref name="Standable"/>).</param>
/// <param name="Headroom">Clear vertical space above the surface, world units (read only when standable).</param>
public readonly record struct NavSurfaceSample(bool Standable, float Height, float Headroom);
