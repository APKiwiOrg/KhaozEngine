using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>Adapts an authored world's surface to x-east, y-north map space in metres.</summary>
/// <remarks>
/// Implementations permit concurrent worker reads. Changes and notifications occur on the data's owner thread.
/// Consumers discard results computed against an older revision.
/// </remarks>
public interface IWorldMapSource
{
    /// <summary>The map-space bounds of the authored surface.</summary>
    MapRect Bounds { get; }

    /// <summary>The native surface lattice's cell edge length in metres.</summary>
    float SurfaceCellSize { get; }

    /// <summary>The current source revision, incremented when authored data changes.</summary>
    long Revision { get; }

    /// <summary>Raised on the owner thread with the map-space area touched by a change.</summary>
    event Action<MapRect>? Changed;

    /// <summary>Samples at cell centres on the source lattice, covering at least the requested area.</summary>
    /// <param name="area">The map-space area to cover.</param>
    /// <param name="maxCellSize">The cell-size budget in metres.</param>
    /// <remarks>
    /// The returned cell size is the coarsest multiple of <see cref="SurfaceCellSize"/> no larger than
    /// the greater of <paramref name="maxCellSize"/> and <see cref="SurfaceCellSize"/>.
    /// </remarks>
    WorldMapSurfaceGrid SampleSurface(MapRect area, float maxCellSize);

    /// <summary>Appends every surface footprint whose oriented rectangular shape intersects the area.</summary>
    void CollectFootprints(MapRect area, List<WorldMapFootprint> into);

    /// <summary>Appends every surface marker whose position is contained by the half-open area.</summary>
    void CollectMarkers(MapRect area, List<WorldMapMarker> into);

    /// <summary>Converts a world position into map-space metres, handling the world's handedness here.</summary>
    Vector2 ToMap(Vector3 worldPosition);

    /// <summary>Converts a world forward vector to radians clockwise from map north. A zero vector returns zero.</summary>
    float ToMapHeading(Vector3 worldForward);
}
