using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.WorldMap;

namespace KhaozEngine.TileWorld.WorldMap;

/// <summary>Adapts a static TileWorld document and its catalogs to east/north map metres.</summary>
/// <remarks>
/// Concurrent reads are supported. Keep the document and catalogs unchanged for this source's lifetime,
/// and create a new source after edits or changes to loaded regions.
/// </remarks>
public sealed class TileWorldMapSource : IWorldMapSource
{
    readonly TileWorldDocument _document;
    readonly float _tileSize;
    readonly int _surfacePlane;
    readonly TileWorldMapSurface _surface;
    readonly TileWorldMapFootprints _footprints;

    /// <summary>Creates a map source over the currently loaded regions and the selected surface plane.</summary>
    /// <param name="document">The static authored world.</param>
    /// <param name="catalogs">The static ground materials and object archetypes.</param>
    /// <param name="surfacePlane">The terrain, object and marker plane. Roofs on all planes are included.</param>
    public TileWorldMapSource(TileWorldDocument document, TileWorldCatalogs catalogs, int surfacePlane = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (!float.IsFinite(document.TileSize) || document.TileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(document), "Tile size must be finite and positive.");
        if ((uint)surfacePlane >= (uint)document.PlaneCount)
            throw new ArgumentOutOfRangeException(nameof(surfacePlane));

        _document = document;
        _tileSize = document.TileSize;
        _surfacePlane = surfacePlane;
        _surface = new TileWorldMapSurface(document, catalogs, surfacePlane, _tileSize);
        _footprints = new TileWorldMapFootprints(document, catalogs, surfacePlane, _tileSize);

        MapRect? bounds = null;
        foreach (RegionCoord coord in document.Regions.Keys)
        {
            MapRect rect = MapRectangle(coord.Rect, _tileSize);
            bounds = bounds is { } existing ? existing.Union(rect) : rect;
        }
        Bounds = bounds ?? default;
    }

    /// <summary>The union of the loaded region rectangles in map metres.</summary>
    public MapRect Bounds { get; }

    /// <summary>The document's tile edge length in metres.</summary>
    public float SurfaceCellSize => _tileSize;

    /// <summary>Always zero because this source reads a static document.</summary>
    public long Revision => 0;

    /// <summary>Never raised because the document and catalogs remain static.</summary>
    public event Action<MapRect>? Changed
    {
        add { }
        remove { }
    }

    /// <summary>Point-samples a stride-aligned grid covering the area, ordered south to north.</summary>
    /// <param name="area">The map-space area to cover.</param>
    /// <param name="maxCellSize">The cell-size budget in metres. Hints below one tile use native spacing.</param>
    /// <remarks>Each cell samples the tile at its block start plus half the integer tile stride.</remarks>
    public WorldMapSurfaceGrid SampleSurface(MapRect area, float maxCellSize) => _surface.Sample(area, maxCellSize);

    /// <summary>Appends intersecting surface objects and roofs, including anchors outside the area.</summary>
    public void CollectFootprints(MapRect area, List<WorldMapFootprint> into) => _footprints.Collect(area, into);

    /// <summary>Appends surface-plane markers whose tile centres lie inside the half-open area.</summary>
    public void CollectMarkers(MapRect area, List<WorldMapMarker> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (TileRegion region in _document.RegionsTouching(TilesCovering(area, _tileSize)))
            foreach (TileMarker marker in region.Markers)
            {
                if (marker.Plane != _surfacePlane) continue;
                var position = new Vector2((marker.X + 0.5f) * _tileSize, (marker.Z + 0.5f) * _tileSize);
                if (area.Contains(position))
                    into.Add(new WorldMapMarker(marker.Name, position, marker.Tags?.ToArray() ?? Array.Empty<string>()));
            }
    }

    /// <summary>Converts render-space world metres through TileWorld's coordinate seam to map metres.</summary>
    public Vector2 ToMap(Vector3 worldPosition) => new(
        TileWorldSpace.TileX(worldPosition.X, _tileSize) * _tileSize,
        TileWorldSpace.TileZ(worldPosition.Z, _tileSize) * _tileSize);

    /// <summary>Returns radians clockwise from map north, or zero for a zero horizontal forward vector.</summary>
    public float ToMapHeading(Vector3 worldForward) =>
        worldForward.X == 0f && worldForward.Z == 0f ? 0f : MathF.Atan2(worldForward.X, -worldForward.Z);

    internal static TileRect TilesCovering(MapRect area, float tileSize)
    {
        int x = checked((int)MathF.Floor(area.X / tileSize));
        int z = checked((int)MathF.Floor(area.Y / tileSize));
        if (area.Width <= 0f || area.Height <= 0f) return new TileRect(x, z, 0, 0);
        int x1 = checked((int)MathF.Ceiling(area.Right / tileSize));
        int z1 = checked((int)MathF.Ceiling(area.Top / tileSize));
        return new TileRect(x, z, checked(x1 - x), checked(z1 - z));
    }

    internal static MapRect MapRectangle(TileRect rect, float tileSize) =>
        new(rect.X * tileSize, rect.Z * tileSize, rect.Width * tileSize, rect.Height * tileSize);
}
