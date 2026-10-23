using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The legacy-tile compatibility arithmetic over native lattices: smoothed corner normals from central differences
/// over one surface's global corner lattice, and the released float interpolation inside one cell. Corner heights follow
/// the released precedence: the corner's own slot, then the slots west, south and south-west of it, then the policy's
/// known-absence boundary height. A slot that is advertised but unavailable refuses rather than reading as absent, so the
/// boundary height is compatibility metadata for known absence only, never missing-data recovery.</summary>
internal static class MapFoliageNormals
{
    /// <summary>The smoothed normal at lattice corner (<paramref name="x"/>, <paramref name="z"/>) of
    /// <paramref name="surfaceId"/>, in the surface's local frame. Throws <see cref="MapFoliageRefusal"/> when a height it
    /// reads is unavailable.</summary>
    internal static Vector3 Corner(MapScopedSurfaces view, string surfaceId, long x, long z, float boundaryHeight) =>
        new MapFoliageLattice(view, MapSpaceGeometry.Surface(view, surfaceId), boundaryHeight).Normal(x, z);

    /// <summary>The corner height in metres the normals read, under the released precedence.</summary>
    internal static float CornerHeight(MapScopedSurfaces view, string surfaceId, long x, long z, float boundaryHeight) =>
        new MapFoliageLattice(view, MapSpaceGeometry.Surface(view, surfaceId), boundaryHeight).Height(x, z);

    /// <summary>One cell's ground point under the released interpolation: the cell's lattice triangles in canonical tie
    /// order, the first whose barycentric weights all reach -1e-5, height and normal blended from its three vertices.
    /// Mid-edge vertices average their corner heights and normalize the sum of their corner normals.</summary>
    internal static MapFoliageGround Ground(in MapFoliageCellCorners corners, MapSurfaceCell cell, bool splitSwNe, float localX, float localZ)
    {
        Span<MapLatticeTriangle> triangles = stackalloc MapLatticeTriangle[4];
        int count = MapSurfaceTopology.Triangulate(cell.Overlay == 0 ? MapOverlayCut.Full : cell.Cut, cell.Rotation, splitSwNe, triangles);
        var point = new Vector2(localX, localZ);
        for (int i = 0; i < count; i++)
        {
            MapLatticeTriangle triangle = triangles[i];
            Vertex a = At(triangle.A, corners), b = At(triangle.B, corners), c = At(triangle.C, corners);
            if (!Barycentric(point, a.Local, b.Local, c.Local, out Vector3 weights)) continue;
            float height = a.Height * weights.X + b.Height * weights.Y + c.Height * weights.Z;
            Vector3 normal = Normalize(a.Normal * weights.X + b.Normal * weights.Y + c.Normal * weights.Z);
            return new(height, normal, (byte)i, triangle.Overlay);
        }
        throw new MapFoliageRefusal(MapFoliageStatus.NotRepresentable, MapFoliageSupport.NoWork,
            "legacy triangulation does not cover the local cell point");
    }

    /// <summary>The released float conversion of native height units to metres. Centimetre lattices keep the released
    /// <c>units * 0.01f</c> product, other units round their exact metre value once.</summary>
    internal static float Metres(MapLatticeFrame frame, int units) =>
        frame.HeightUnitMetres == new MapRational(1, 100) ? units * 0.01f : frame.Metres(new(units, 1)).ToSingle();

    readonly record struct Vertex(Vector2 Local, float Height, Vector3 Normal);

    static Vertex At(MapLatticePoint point, in MapFoliageCellCorners c)
    {
        (MapLatticePoint first, MapLatticePoint second) = MapSurfaceTopology.Ends(point);
        Vertex a = Pick(first, c);
        if (first == second) return a;
        Vertex b = Pick(second, c);
        return new((a.Local + b.Local) * 0.5f, (a.Height + b.Height) * 0.5f, Normalize(a.Normal + b.Normal));
    }

    static Vertex Pick(MapLatticePoint point, in MapFoliageCellCorners c) => point switch
    {
        MapLatticePoint.Se => new(new(1f, 0f), c.Se, c.SeNormal),
        MapLatticePoint.Nw => new(new(0f, 1f), c.Nw, c.NwNormal),
        MapLatticePoint.Ne => new(new(1f, 1f), c.Ne, c.NeNormal),
        _ => new(Vector2.Zero, c.Sw, c.SwNormal),
    };

    static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
    {
        float denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        float wa = (((b.Y - c.Y) * (p.X - c.X)) + ((c.X - b.X) * (p.Y - c.Y))) / denominator;
        float wb = (((c.Y - a.Y) * (p.X - c.X)) + ((a.X - c.X) * (p.Y - c.Y))) / denominator;
        float wc = 1f - wa - wb;
        weights = new Vector3(wa, wb, wc);
        return wa >= -1e-5f && wb >= -1e-5f && wc >= -1e-5f;
    }

    internal static Vector3 Normalize(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;
}

/// <summary>A cell's four corner heights in metres and their smoothed normals, in the surface's local frame.</summary>
internal readonly record struct MapFoliageCellCorners(float Sw, float Se, float Nw, float Ne,
    Vector3 SwNormal, Vector3 SeNormal, Vector3 NwNormal, Vector3 NeNormal);

/// <summary>The ground point of one cell sample: height, local normal, the parent triangle in canonical order and
/// whether that triangle carries the cell's overlay.</summary>
internal readonly record struct MapFoliageGround(float Height, Vector3 Normal, byte Parent, bool Overlay);

/// <summary>One surface's global corner lattice inside an acquisition, with each slot read once. Records every slot it
/// consulted, so preparation can name exactly the patches a normal depended on.</summary>
internal sealed class MapFoliageLattice
{
    readonly MapScopedSurfaces _view;
    readonly MapSurfaceRef _surface;
    readonly float _boundary;
    readonly float _span;
    readonly Dictionary<(long X, long Z), MapSurfacePatch?> _slots = new();
    readonly Dictionary<(long X, long Z), Vector3> _normals = new();

    internal MapFoliageLattice(MapScopedSurfaces view, MapSurfaceRef surface, float boundaryHeight)
    {
        _view = view;
        _surface = surface;
        _boundary = boundaryHeight;
        // Differences use the bound surface's cell metres, never the raster cell size.
        _span = 2f * surface.Frame.CellUnitMetres.Exact().ToSingle();
    }

    /// <summary>Every slot read so far, with its patch or null for a known-absent slot.</summary>
    internal IReadOnlyDictionary<(long X, long Z), MapSurfacePatch?> Slots => _slots;

    internal Vector3 Normal(long x, long z)
    {
        if (_normals.TryGetValue((x, z), out Vector3 cached)) return cached;
        float hx = Height(checked(x + 1), z) - Height(checked(x - 1), z);
        float hz = Height(x, checked(z + 1)) - Height(x, checked(z - 1));
        // Lattice z runs opposite to world z on a NegativeZ frame, so the world-z gradient is -hz/span there and its
        // negation leaves +hz/span. A PositiveZ frame keeps the plain negated gradient.
        Vector3 normal = _surface.Frame.RowDirection == MapRowDirection.NegativeZ
            ? Vector3.Normalize(new Vector3(-hx / _span, 1f, hz / _span))
            : Vector3.Normalize(new Vector3(-hx / _span, 1f, -hz / _span));
        _normals.Add((x, z), normal);
        return normal;
    }

    internal float Height(long x, long z)
    {
        // Own slot, then west, south and south-west, as the released global lattice resolves a corner.
        foreach (var (cellX, cellZ) in new[] { (x, z), (x - 1, z), (x, z - 1), (x - 1, z - 1) })
        {
            long slotX = MapSurfaceRanges.Floor(cellX, MapPatchKey.SlotCells), slotZ = MapSurfaceRanges.Floor(cellZ, MapPatchKey.SlotCells);
            if (Slot(slotX, slotZ, x, z) is not { } patch) continue;
            long localX = x - slotX * MapPatchKey.SlotCells - patch.CellMinX, localZ = z - slotZ * MapPatchKey.SlotCells - patch.CellMinZ;
            if (localX < 0 || localX > patch.Width || localZ < 0 || localZ > patch.Depth) continue;
            return MapFoliageNormals.Metres(_surface.Frame, patch.Height((int)localX, (int)localZ));
        }
        return _boundary;
    }

    MapSurfacePatch? Slot(long slotX, long slotZ, long cornerX, long cornerZ)
    {
        if (_slots.TryGetValue((slotX, slotZ), out MapSurfacePatch? known)) return known;
        var key = new MapPatchKey(_surface.Id, slotX, slotZ);
        MapSurfacePatch? patch;
        if (_view.TryAcquiredPatch(key, out MapSurfacePatch? acquired, out MapPatchStatus status)) patch = acquired;
        else if (_view.HasRead(key) || _view.Status != MapAcquireStatus.Complete)
            throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, MapFoliageSupport.NoWork, $"patch {key}: {status}");
        else if (!InScope(cornerX, cornerZ))
            throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, MapFoliageSupport.NoWork, $"patch {key}: normal halo outside the acquired scope");
        // A complete acquisition reads every patch that meets its scope, so an unread slot holding an in-scope corner has none.
        else patch = null;
        _slots.Add((slotX, slotZ), patch);
        return patch;
    }

    bool InScope(long x, long z)
    {
        MapExactXz world = MapEffectiveGeometry.Vertex(_view, new MapLatticeVertex(_surface.Id, MapLatticeAddress.Corner(x, z)));
        MapExactRect scope = _view.Scope.WorldRectangle();
        return world.X.CompareTo(scope.MinX) >= 0 && world.X.CompareTo(scope.MaxX) <= 0 &&
            world.Z.CompareTo(scope.MinZ) >= 0 && world.Z.CompareTo(scope.MaxZ) <= 0;
    }
}
