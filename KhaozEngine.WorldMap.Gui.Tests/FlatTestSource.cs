using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;

namespace KhaozEngine.Tests.WorldMap.Gui;

internal sealed class FlatTestSource : IWorldMapSource
{
    internal FlatTestSource(MapRect? bounds = null) => Bounds = bounds ?? new MapRect(0f, 0f, 256f, 192f);

    public MapRect Bounds { get; }
    internal List<WorldMapMarker> Markers { get; } = [];
    internal List<WorldMapFootprint> Footprints { get; } = [];
    internal Color GroundColor { get; set; } = Color.FromHex("#527448");
    public float SurfaceCellSize => 8f;
    public long Revision { get; private set; }
    public event Action<MapRect>? Changed;

    public WorldMapSurfaceGrid SampleSurface(MapRect area, float maxCellSize)
    {
        float left = MathF.Floor(area.X / SurfaceCellSize) * SurfaceCellSize;
        float bottom = MathF.Floor(area.Y / SurfaceCellSize) * SurfaceCellSize;
        int width = (int)MathF.Ceiling((area.Right - left) / SurfaceCellSize);
        int height = (int)MathF.Ceiling((area.Top - bottom) / SurfaceCellSize);
        var grid = new WorldMapSurfaceGrid(new Vector2(left, bottom), SurfaceCellSize, width, height);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                var center = grid.Origin + new Vector2(column + 0.5f, row + 0.5f) * SurfaceCellSize;
                if (!Bounds.Contains(center)) continue;
                int index = grid.Index(column, row);
                grid.Ground[index] = GroundColor;
                grid.SurfaceHeight[index] = 0f;
            }
        return grid;
    }

    public void CollectFootprints(in WorldMapFootprintQuery query, List<WorldMapFootprint> into)
    {
        foreach (WorldMapFootprint footprint in Footprints)
        {
            float cos = MathF.Abs(MathF.Cos(footprint.Rotation));
            float sin = MathF.Abs(MathF.Sin(footprint.Rotation));
            var extent = new Vector2(cos * footprint.HalfExtents.X + sin * footprint.HalfExtents.Y,
                sin * footprint.HalfExtents.X + cos * footprint.HalfExtents.Y);
            var bounds = new MapRect(footprint.Center.X - extent.X, footprint.Center.Y - extent.Y,
                extent.X * 2f, extent.Y * 2f);
            if (bounds.Intersects(query.Area) &&
                2f * MathF.Max(footprint.HalfExtents.X, footprint.HalfExtents.Y) >= query.MinExtentMetres)
                into.Add(footprint);
        }
    }
    public void CollectMarkers(MapRect area, List<WorldMapMarker> into)
    {
        foreach (WorldMapMarker marker in Markers)
            if (area.Contains(marker.Position)) into.Add(marker);
    }
    public Vector2 ToMap(Vector3 worldPosition) => new(worldPosition.X, -worldPosition.Z);
    public float ToMapHeading(Vector3 worldForward) => MathF.Atan2(worldForward.X, -worldForward.Z);

    internal void Change(MapRect area)
    {
        Revision++;
        Changed?.Invoke(area);
    }
}

internal sealed record FakeTexture(byte[] Rgba, int Width, int Height)
{
    internal int Disposals { get; private set; }
    internal void Dispose() => Disposals++;
}

internal sealed class FakeTextures
{
    internal List<FakeTexture> Created { get; } = [];

    internal FakeTexture Create(byte[] rgba, int width, int height)
    {
        var texture = new FakeTexture(rgba, width, height);
        Created.Add(texture);
        return texture;
    }
}
