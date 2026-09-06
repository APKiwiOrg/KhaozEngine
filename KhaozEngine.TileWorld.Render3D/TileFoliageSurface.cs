using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Samples one authored foliage layer against loaded tile-world ground and exclusion rules.</summary>
public sealed class TileFoliageSurface
{
    readonly TileWorldDocument _doc;
    readonly TileWorldCatalogs _catalogs;
    readonly TileFoliageLayer _layer;
    readonly HashSet<(int X, int Z)> _solid = new();
    readonly HashSet<(int X, int Z)> _roofed = new();
    readonly List<Vector2> _doors = new();

    public TileFoliageSurface(TileWorldDocument doc, TileWorldCatalogs catalogs, TileFoliageLayer layer)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        if ((uint)layer.Plane >= (uint)doc.PlaneCount)
            throw new ArgumentException($"Foliage layer '{layer.Id}' uses plane {layer.Plane}, the world has planes 0 through {doc.PlaneCount - 1}.", nameof(layer));
        IndexObjects();
    }

    /// <summary>Samples height, upward normal and final normalized density at world XZ.</summary>
    public GroundCoverSample Sample(float worldX, float worldZ)
    {
        if (!Finite(worldX) || !Finite(worldZ))
            throw new ArgumentException("Foliage surface coordinates must be finite.");
        float density = RasterDensity(worldX, worldZ);
        float tileX = TileWorldSpace.TileX(worldX, _doc.TileSize);
        float tileZ = TileWorldSpace.TileZ(worldZ, _doc.TileSize);
        int x = (int)MathF.Floor(tileX);
        int z = (int)MathF.Floor(tileZ);
        float fx = tileX - x;
        float fz = tileZ - z;
        if (density > 0f && TileAllowed(x, z, fx, fz))
        {
            density *= DoorFactor(worldX, worldZ);
            density *= EdgeFactor(tileX, tileZ, x, z);
        }
        else
        {
            density = 0f;
        }

        float h00 = _doc.CornerHeight(x, z, _layer.Plane);
        float h10 = _doc.CornerHeight(x + 1, z, _layer.Plane);
        float h01 = _doc.CornerHeight(x, z + 1, _layer.Plane);
        float h11 = _doc.CornerHeight(x + 1, z + 1, _layer.Plane);
        float south = h00 + (h10 - h00) * fx;
        float north = h01 + (h11 - h01) * fx;
        float height = south + (north - south) * fz;
        float dhdx = ((h10 - h00) * (1f - fz) + (h11 - h01) * fz) / _doc.TileSize;
        float dhdTileZ = (north - south) / _doc.TileSize;
        Vector3 normal = Vector3.Normalize(new Vector3(-dhdx, 1f, dhdTileZ));
        return new GroundCoverSample(height, normal, Math.Clamp(density, 0f, 1f));
    }

    void IndexObjects()
    {
        foreach (TileObject obj in _doc.AllObjects())
        {
            TileObjectArchetype? archetype = _catalogs.Archetype(obj.ArchetypeId);
            if (archetype is null) continue;
            TileRect footprint = TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation);
            if (_layer.ExcludeSolidObjects && obj.Plane == _layer.Plane && archetype.CollisionKind == TileCollisionKind.Solid)
                AddTiles(_solid, footprint);
            if (_layer.ExcludeIndoors && archetype.IsRoof && obj.Plane > _layer.Plane)
                AddTiles(_roofed, footprint);
            if (_layer.DoorClearance > 0f && obj.Plane == _layer.Plane && IsDoor(obj, archetype))
            {
                float centreX = footprint.X + footprint.Width * 0.5f;
                float centreZ = footprint.Z + footprint.Height * 0.5f;
                _doors.Add(new Vector2(TileWorldSpace.WorldX(centreX, _doc.TileSize),
                    TileWorldSpace.WorldZ(centreZ, _doc.TileSize)));
            }
        }
    }

    static void AddTiles(HashSet<(int X, int Z)> into, TileRect rect)
    {
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++) into.Add((x, z));
    }

    static bool IsDoor(TileObject obj, TileObjectArchetype archetype) =>
        HasTag(obj.Tags, "door") || HasTag(archetype.Tags, "door");

    static bool HasTag(IReadOnlyList<string>? tags, string wanted)
    {
        for (int i = 0; i < (tags?.Count ?? 0); i++)
            if (string.Equals(tags![i], wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    bool TileAllowed(int x, int z, float localX = 0.5f, float localZ = 0.5f)
    {
        if (_doc.GetRegion(RegionCoord.Of(x, z)) is null) return false;
        ushort underlay = _doc.GetUnderlay(x, z, _layer.Plane);
        if (underlay == 0) return false;
        ushort overlay = _doc.GetOverlay(x, z, _layer.Plane);
        ushort visible = overlay != 0 && OverlayAt(x, z, localX, localZ) ? overlay : underlay;
        if (_layer.AllowedUnderlays.Count > 0 && !Contains(_layer.AllowedUnderlays, visible)) return false;
        if (_catalogs.Material(visible)?.Kind == GroundMaterialKind.Water) return false;
        if (_layer.ExcludeIndoors && (_doc.GetSettings(x, z, _layer.Plane) & TileSettings.Indoors) != 0) return false;
        if (_solid.Contains((x, z)) || _roofed.Contains((x, z))) return false;
        return true;
    }

    bool OverlayAt(int x, int z, float localX, float localZ)
    {
        TileOverlayShape shape = _doc.GetOverlayShape(x, z, _layer.Plane);
        int rotation = _doc.GetOverlayRotation(x, z, _layer.Plane);
        short h00 = _doc.CornerHeightCm(x, z, _layer.Plane);
        short h10 = _doc.CornerHeightCm(x + 1, z, _layer.Plane);
        short h01 = _doc.CornerHeightCm(x, z + 1, _layer.Plane);
        short h11 = _doc.CornerHeightCm(x + 1, z + 1, _layer.Plane);
        bool split = TileTriangulation.SplitSwNe(h00, h10, h01, h11, shape, rotation);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int count = TileTriangulation.Triangulate(shape, rotation, split, triangles);
        var point = new Vector2(localX, localZ);
        for (int i = 0; i < count; i++)
        {
            TileLatticeTriangle triangle = triangles[i];
            if (triangle.Overlay && Contains(point, TileTriangulation.Local(triangle.A),
                TileTriangulation.Local(triangle.B), TileTriangulation.Local(triangle.C))) return true;
        }
        return false;
    }

    static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float ab = Cross(b - a, p - a);
        float bc = Cross(c - b, p - b);
        float ca = Cross(a - c, p - c);
        return (ab >= -1e-6f && bc >= -1e-6f && ca >= -1e-6f) ||
            (ab <= 1e-6f && bc <= 1e-6f && ca <= 1e-6f);
    }

    static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    static bool Contains(IReadOnlyList<ushort> values, ushort value)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == value) return true;
        return false;
    }

    float DoorFactor(float worldX, float worldZ)
    {
        for (int i = 0; i < _doors.Count; i++)
        {
            float distance = Vector2.Distance(new Vector2(worldX, worldZ), _doors[i]);
            if (distance < _layer.DoorClearance) return 0f;
        }
        return 1f;
    }

    float EdgeFactor(float tileX, float tileZ, int x, int z)
    {
        if (_layer.EdgeFade <= 0f) return 1f;
        float fx = tileX - x;
        float fz = tileZ - z;
        float factor = 1f;
        if (!TileAllowed(x - 1, z)) factor = MathF.Min(factor, fx * _doc.TileSize / _layer.EdgeFade);
        if (!TileAllowed(x + 1, z)) factor = MathF.Min(factor, (1f - fx) * _doc.TileSize / _layer.EdgeFade);
        if (!TileAllowed(x, z - 1)) factor = MathF.Min(factor, fz * _doc.TileSize / _layer.EdgeFade);
        if (!TileAllowed(x, z + 1)) factor = MathF.Min(factor, (1f - fz) * _doc.TileSize / _layer.EdgeFade);
        return Math.Clamp(factor, 0f, 1f);
    }

    float RasterDensity(float worldX, float worldZ)
    {
        float u = (worldX - _layer.OriginX) / _layer.CellSize;
        float v = (worldZ - _layer.OriginZ) / _layer.CellSize;
        if (u < 0f || v < 0f || u > _layer.Width - 1 || v > _layer.Height - 1) return 0f;
        int x0 = Math.Min((int)MathF.Floor(u), _layer.Width - 1);
        int z0 = Math.Min((int)MathF.Floor(v), _layer.Height - 1);
        int x1 = Math.Min(x0 + 1, _layer.Width - 1);
        int z1 = Math.Min(z0 + 1, _layer.Height - 1);
        float fx = u - x0;
        float fz = v - z0;
        float south = Lerp(_layer.DensityAt(x0, z0), _layer.DensityAt(x1, z0), fx);
        float north = Lerp(_layer.DensityAt(x0, z1), _layer.DensityAt(x1, z1), fx);
        return Lerp(south, north, fz) / 255f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
