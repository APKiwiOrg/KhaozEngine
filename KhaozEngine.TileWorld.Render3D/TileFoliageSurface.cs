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
    readonly bool _smoothNormals;
    readonly HashSet<(int X, int Z)> _solid = new();
    readonly HashSet<(int X, int Z)> _roofed = new();
    readonly List<Vector2> _doors = new();

    public TileFoliageSurface(TileWorldDocument doc, TileWorldCatalogs catalogs, TileFoliageLayer layer,
        bool smoothNormals = true)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _smoothNormals = smoothNormals;
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
        GroundPoint ground = GroundAt(x, z, fx, fz);
        if (density > 0f && TileAllowed(x, z, ground.Overlay))
        {
            density *= DoorFactor(worldX, worldZ);
            density *= EdgeFactor(tileX, tileZ, x, z);
        }
        else
        {
            density = 0f;
        }

        return new GroundCoverSample(ground.Height, ground.Normal, Math.Clamp(density, 0f, 1f));
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

    bool TileAllowed(int x, int z, float localX = 0.5f, float localZ = 0.5f) =>
        TileAllowed(x, z, OverlayAt(x, z, localX, localZ));

    bool TileAllowed(int x, int z, bool overlayAtPoint)
    {
        if (_doc.GetRegion(RegionCoord.Of(x, z)) is null) return false;
        ushort underlay = _doc.GetUnderlay(x, z, _layer.Plane);
        if (underlay == 0) return false;
        ushort overlay = _doc.GetOverlay(x, z, _layer.Plane);
        ushort visible = overlay != 0 && overlayAtPoint ? overlay : underlay;
        if (_layer.AllowedUnderlays.Count > 0 && !Contains(_layer.AllowedUnderlays, visible)) return false;
        if (_catalogs.Material(visible)?.Kind == GroundMaterialKind.Water) return false;
        if (_layer.ExcludeIndoors && (_doc.GetSettings(x, z, _layer.Plane) & TileSettings.Indoors) != 0) return false;
        if (_solid.Contains((x, z)) || _roofed.Contains((x, z))) return false;
        return true;
    }

    GroundPoint GroundAt(int x, int z, float localX, float localZ)
    {
        short h00 = _doc.CornerHeightCm(x, z, _layer.Plane);
        short h10 = _doc.CornerHeightCm(x + 1, z, _layer.Plane);
        short h01 = _doc.CornerHeightCm(x, z + 1, _layer.Plane);
        short h11 = _doc.CornerHeightCm(x + 1, z + 1, _layer.Plane);
        TileOverlayShape authored = _doc.GetOverlayShape(x, z, _layer.Plane);
        int rotation = _doc.GetOverlayRotation(x, z, _layer.Plane);
        bool split = TileTriangulation.SplitSwNe(h00, h10, h01, h11, authored, rotation);
        TileOverlayShape shape = _doc.GetOverlay(x, z, _layer.Plane) == 0
            ? TileOverlayShape.Full
            : authored;
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int count = TileTriangulation.Triangulate(shape, rotation, split, triangles);

        GroundVertex sw = Corner(x, z, h00, TileLatticePoint.Sw);
        GroundVertex se = Corner(x + 1, z, h10, TileLatticePoint.Se);
        GroundVertex nw = Corner(x, z + 1, h01, TileLatticePoint.Nw);
        GroundVertex ne = Corner(x + 1, z + 1, h11, TileLatticePoint.Ne);
        var point = new Vector2(localX, localZ);
        for (int i = 0; i < count; i++)
        {
            TileLatticeTriangle triangle = triangles[i];
            GroundVertex a = At(triangle.A, sw, se, nw, ne);
            GroundVertex b = At(triangle.B, sw, se, nw, ne);
            GroundVertex c = At(triangle.C, sw, se, nw, ne);
            if (!Barycentric(point, a.Local, b.Local, c.Local, out Vector3 weights)) continue;
            float height = a.Height * weights.X + b.Height * weights.Y + c.Height * weights.Z;
            Vector3 normal = _smoothNormals
                ? Normalize(a.Normal * weights.X + b.Normal * weights.Y + c.Normal * weights.Z)
                : FaceNormal(a, b, c);
            return new GroundPoint(height, normal, triangle.Overlay);
        }
        throw new InvalidOperationException($"Tile triangulation did not cover local point ({localX}, {localZ}).");
    }

    GroundVertex Corner(int x, int z, short heightCm, TileLatticePoint point) => new(
        TileTriangulation.Local(point), heightCm * 0.01f,
        TileGroundMesher.CornerNormal(_doc, x, z, _layer.Plane));

    static GroundVertex At(TileLatticePoint point, in GroundVertex sw, in GroundVertex se,
        in GroundVertex nw, in GroundVertex ne)
    {
        TileTriangulation.Ends(point, out TileLatticePoint first, out TileLatticePoint second);
        GroundVertex a = Pick(first, sw, se, nw, ne);
        if (first == second) return a;
        GroundVertex b = Pick(second, sw, se, nw, ne);
        return new GroundVertex((a.Local + b.Local) * 0.5f, (a.Height + b.Height) * 0.5f,
            Normalize(a.Normal + b.Normal));
    }

    static GroundVertex Pick(TileLatticePoint point, in GroundVertex sw, in GroundVertex se,
        in GroundVertex nw, in GroundVertex ne) => point switch
        {
            TileLatticePoint.Se => se,
            TileLatticePoint.Nw => nw,
            TileLatticePoint.Ne => ne,
            _ => sw,
        };

    Vector3 FaceNormal(in GroundVertex a, in GroundVertex b, in GroundVertex c)
    {
        float tileSize = _doc.TileSize;
        Vector3 pa = new(a.Local.X * tileSize, a.Height, -a.Local.Y * tileSize);
        Vector3 pb = new(b.Local.X * tileSize, b.Height, -b.Local.Y * tileSize);
        Vector3 pc = new(c.Local.X * tileSize, c.Height, -c.Local.Y * tileSize);
        Vector3 normal = Normalize(Vector3.Cross(pb - pa, pc - pa));
        return normal.Y < 0f ? -normal : normal;
    }

    static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
    {
        float denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        float wa = (((b.Y - c.Y) * (p.X - c.X)) + ((c.X - b.X) * (p.Y - c.Y))) / denominator;
        float wb = (((c.Y - a.Y) * (p.X - c.X)) + ((a.X - c.X) * (p.Y - c.Y))) / denominator;
        float wc = 1f - wa - wb;
        weights = new Vector3(wa, wb, wc);
        return wa >= -1e-5f && wb >= -1e-5f && wc >= -1e-5f;
    }

    static Vector3 Normalize(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;

    readonly record struct GroundVertex(Vector2 Local, float Height, Vector3 Normal);
    readonly record struct GroundPoint(float Height, Vector3 Normal, bool Overlay);

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
