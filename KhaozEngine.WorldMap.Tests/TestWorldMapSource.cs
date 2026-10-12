using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;

namespace KhaozEngine.Tests.WorldMap;

internal sealed class TestWorldMapSource : IWorldMapSource
{
    readonly WorldMapSurfaceGrid _surface;

    public TestWorldMapSource(MapRect bounds, float cellSize = 1f)
    {
        Bounds = bounds;
        SurfaceCellSize = cellSize;
        _surface = new WorldMapSurfaceGrid(new Vector2(bounds.X, bounds.Y), cellSize,
            (int)MathF.Ceiling(bounds.Width / cellSize), (int)MathF.Ceiling(bounds.Height / cellSize));
    }

    public MapRect Bounds { get; }
    public float SurfaceCellSize { get; }
    public long Revision { get; set; }
    public event Action<MapRect>? Changed;
    public List<WorldMapFootprint> Footprints { get; } = [];
    public List<WorldMapMarker> Markers { get; } = [];
    public Action<MapRect>? BeforeSample { get; set; }
    public int SampleCalls { get; private set; }

    public void SetCell(int column, int row, Color ground, float height, float water = float.NaN)
    {
        int index = _surface.Index(column, row);
        _surface.Ground[index] = ground;
        _surface.SurfaceHeight[index] = height;
        _surface.WaterSurface[index] = water;
    }

    public WorldMapSurfaceGrid SampleSurface(MapRect area)
    {
        SampleCalls++;
        BeforeSample?.Invoke(area);
        int left = Math.Clamp((int)MathF.Floor((area.X - Bounds.X) / SurfaceCellSize), 0, _surface.Width);
        int bottom = Math.Clamp((int)MathF.Floor((area.Y - Bounds.Y) / SurfaceCellSize), 0, _surface.Height);
        int right = Math.Clamp((int)MathF.Ceiling((area.Right - Bounds.X) / SurfaceCellSize), left, _surface.Width);
        int top = Math.Clamp((int)MathF.Ceiling((area.Top - Bounds.Y) / SurfaceCellSize), bottom, _surface.Height);
        var grid = new WorldMapSurfaceGrid(_surface.Origin + new Vector2(left, bottom) * SurfaceCellSize,
            SurfaceCellSize, right - left, top - bottom);
        for (int row = 0; row < grid.Height; row++)
            for (int column = 0; column < grid.Width; column++)
            {
                int from = _surface.Index(left + column, bottom + row);
                int to = grid.Index(column, row);
                grid.Ground[to] = _surface.Ground[from];
                grid.SurfaceHeight[to] = _surface.SurfaceHeight[from];
                grid.WaterSurface[to] = _surface.WaterSurface[from];
            }
        return grid;
    }

    public void CollectFootprints(MapRect area, List<WorldMapFootprint> into)
    {
        foreach (WorldMapFootprint footprint in Footprints)
        {
            float cos = MathF.Abs(MathF.Cos(footprint.Rotation));
            float sin = MathF.Abs(MathF.Sin(footprint.Rotation));
            var extent = new Vector2(cos * footprint.HalfExtents.X + sin * footprint.HalfExtents.Y,
                sin * footprint.HalfExtents.X + cos * footprint.HalfExtents.Y);
            var bounds = new MapRect(footprint.Center.X - extent.X, footprint.Center.Y - extent.Y,
                extent.X * 2f, extent.Y * 2f);
            if (bounds.Intersects(area)) into.Add(footprint);
        }
    }

    public void CollectMarkers(MapRect area, List<WorldMapMarker> into)
    {
        foreach (WorldMapMarker marker in Markers)
            if (area.Contains(marker.Position)) into.Add(marker);
    }

    public Vector2 ToMap(Vector3 worldPosition) => new(worldPosition.X, -worldPosition.Z);

    public float ToMapHeading(Vector3 worldForward) =>
        worldForward.X == 0f && worldForward.Z == 0f ? 0f : MathF.Atan2(worldForward.X, -worldForward.Z);

    public void RaiseChanged(MapRect area) => Changed?.Invoke(area);
}
