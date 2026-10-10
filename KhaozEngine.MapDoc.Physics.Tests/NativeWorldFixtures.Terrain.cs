using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Terrain documents for the physics chunk tests, built through public R2 APIs only.</summary>
internal static partial class NativeWorldFixtures
{
    /// <summary>The capsule the feature query tests install: radius, length and the gap <see cref="ProbeNear"/> leaves
    /// between its nearest point and the face.</summary>
    const float ProbeRadius = 0.2f, ProbeLength = 0.4f, ProbeGap = 0.005f;

    // Every compiled face a terrain fixture registers, keyed by face. Fixture surface and strip IDs are unique per
    // geometry, so one face never registers two normals.
    static readonly ConcurrentDictionary<MapFaceKey, Vector3> CompiledNormals = new();

    /// <summary>A stacked cave in metre cells over [0, 6) by [0, 6): a lower floor at y 0 and a lower ceiling at y 3
    /// that drops to y 1.2 over corners x and z at most 2, an upper floor at y 3.5 and an upper ceiling at y 6.5.
    /// The shaft cells [3, 5) by [3, 5) are open through the lower ceiling and the upper floor, and four wall strips
    /// join those two edges around the shaft. Spaces: the lower room, the shaft and the upper room, linked through
    /// the shaft.</summary>
    internal static StackedCaveFixture StackedCave()
    {
        var set = new MapSurfaceSet();
        Func<int, int, bool> solid = (x, z) => !(x is >= 3 and < 5 && z is >= 3 and < 5);
        MapSurfacePatch lowerFloor = AddPatch(set, "cave-lower-floor", MapSurfaceRole.SupportFloor, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 0);
        MapSurfacePatch lowerCeiling = AddPatch(set, "cave-lower-ceiling", MapSurfaceRole.Ceiling, CentimetreHeights,
            0, 0, 6, 6, (x, z) => x <= 2 && z <= 2 ? 120 : 300, solid);
        MapSurfacePatch upperFloor = AddPatch(set, "cave-upper-floor", MapSurfaceRole.SupportFloor, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 350, solid);
        MapSurfacePatch upperCeiling = AddPatch(set, "cave-upper-ceiling", MapSurfaceRole.Ceiling, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 650);
        int[] shaft = { 195, 196, 259, 260 };
        int[] room = Enumerable.Range(0, 6).SelectMany(z => Enumerable.Range(0, 6).Where(x => solid(x, z))
            .Select(x => z * 64 + x)).ToArray();
        MapRecordRef Ref(string id, MapSurfacePatch anchor) => new(id, anchor.Key);
        MapBoundRef Bound(MapBoundKind kind, MapSurfacePatch surface) => new(kind, surface.Key.SurfaceId, null);
        MapBoundRef Opening(MapSurfacePatch anchor) => new(MapBoundKind.HorizontalOpening, null,
            Ref(anchor == lowerCeiling ? "lower-shaft-opening" : "upper-shaft-opening", anchor));

        lowerCeiling.Records.Add(new MapHorizontalOpening("lower-shaft-opening", lowerCeiling.Key, shaft));
        upperFloor.Records.Add(new MapHorizontalOpening("upper-shaft-opening", upperFloor.Key, shaft));
        var walls = new List<MapBoundaryRef>();
        foreach (var (name, x0, z0, x1, z1, facing) in new[]
        {
            ("west", 3, 3, 3, 5, MapStripFacing.Front), ("east", 5, 3, 5, 5, MapStripFacing.Back),
            ("south", 3, 3, 5, 3, MapStripFacing.Back), ("north", 3, 5, 5, 5, MapStripFacing.Front),
        })
        {
            string id = "shaft-" + name;
            lowerFloor.Records.Add(EdgeChain(id + "-lower", lowerCeiling.Key, x0, z0, x1, z1));
            lowerFloor.Records.Add(EdgeChain(id + "-upper", upperFloor.Key, x0, z0, x1, z1));
            lowerFloor.Records.Add(new MapWallStrip(id, Ref(id + "-lower", lowerFloor), Ref(id + "-upper", lowerFloor), facing, 1));
            walls.Add(new(Ref(id, lowerFloor), facing == MapStripFacing.Front ? MapSide.Front : MapSide.Back));
        }
        MapRecordRef[] link = { Ref("shaft-link", lowerFloor) };
        lowerFloor.Records.Add(CaveSpace("lower-room", Array.Empty<MapBoundaryRef>(), link));
        lowerFloor.Records.Add(CaveSpace("shaft", walls, Array.Empty<MapRecordRef>()));
        lowerFloor.Records.Add(new MapSpaceFootprint("lower-room-cells", Ref("lower-room", lowerFloor), lowerFloor.Key, room,
            Bound(MapBoundKind.SupportFloor, lowerFloor), Bound(MapBoundKind.Ceiling, lowerCeiling)));
        lowerFloor.Records.Add(new MapSpaceFootprint("lower-room-shaft-cells", Ref("lower-room", lowerFloor), lowerFloor.Key,
            shaft, Bound(MapBoundKind.SupportFloor, lowerFloor), Opening(lowerCeiling)));
        lowerFloor.Records.Add(new MapSpaceFootprint("shaft-cells", Ref("shaft", lowerFloor), lowerFloor.Key, shaft,
            Opening(lowerCeiling), Opening(upperFloor)));
        lowerFloor.Records.Add(new MapVerticalLink("shaft-link", Ref("upper-room", upperFloor), Ref("lower-room", lowerFloor),
            new[] { Ref("lower-shaft-opening", lowerCeiling), Ref("upper-shaft-opening", upperFloor) },
            Array.Empty<MapRecordRef>(), walls.Select(w => w.Record).ToArray()));
        upperFloor.Records.Add(CaveSpace("upper-room", Array.Empty<MapBoundaryRef>(), link));
        upperFloor.Records.Add(new MapSpaceFootprint("upper-room-cells", Ref("upper-room", upperFloor), upperFloor.Key, room,
            Bound(MapBoundKind.SupportFloor, upperFloor), Bound(MapBoundKind.Ceiling, upperCeiling)));
        upperFloor.Records.Add(new MapSpaceFootprint("upper-room-shaft-cells", Ref("upper-room", upperFloor), upperFloor.Key,
            shaft, Opening(upperFloor), Bound(MapBoundKind.Ceiling, upperCeiling)));

        TerrainFixture f = Terrain(set);
        return new StackedCaveFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount,
            new Vector3(1.5f, 0f, 4.5f), new Vector3(0.5f, 1.2f, 0.5f), new Vector3(4f, 3.25f, 4f), new Vector3(1.5f, 3.5f, 4.5f));
    }

    /// <summary>The one physics chunk of the stacked cave's lower ceiling: its 64 faces fit one whole-slot
    /// block.</summary>
    internal const string CeilingChunkId = "cave-lower-ceiling/0,0/0,0,64";

    /// <summary>The one physics chunk of the stacked cave's lower floor: its 72 faces fit one whole-slot block.</summary>
    internal const string LowerFloorChunkId = "cave-lower-floor/0,0/0,0,64";

    // Every flat cave cell splits along its south-west to north-east diagonal, so local (0.65, 0.35) lies inside the
    // cell's south-east triangle, at least 0.21 m from each of its edges.

    /// <summary>A point in the stacked cave's lower room between the floor and the 3 m ceiling, inside one triangle of
    /// each.</summary>
    internal static readonly Vector3 InLowerRoom = new(1.65f, 1.5f, 4.35f);

    /// <summary>A point in the stacked cave's shaft between the lower ceiling and the upper floor, under the interior
    /// of one upper ceiling triangle.</summary>
    internal static readonly Vector3 InShaftBelowUpperFloor = new(3.65f, 3.25f, 4.35f);

    /// <summary>A lower floor point inside one triangle, at least 0.21 m from each of its edges.</summary>
    internal static readonly Vector3 InsideOneLowerFloorTriangle = new(1.65f, 0f, 4.35f);

    /// <summary>A lower floor point on the diagonal two triangles of cell (1, 4) share, away from its ends.</summary>
    internal static readonly Vector3 OnLowerFloorTriangleEdge = new(1.5f, 0f, 4.5f);

    /// <summary>An edit that lowers the stacked cave's low ceiling corner from y 1.2 to y 1, reported over the lower
    /// ceiling patch inside storage tile (0, 0).</summary>
    internal static MapNativeEditEffects CeilingEditEffects() => new(
        new MapBox3(0, 1.2, 0, 6, 3, 6), new MapBox3(0, 1, 0, 6, 3, 6),
        new[] { new MapPatchKey("cave-lower-ceiling", 0, 0) }, Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<MapDigestChange>(), MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav);

    /// <summary>One 8 by 8 metre-cell floor rising 0.02 m per metre along x from y 1.2, with every patch-boundary
    /// cell edge subdivided into 4 segments. Under a cap of 64 each 4 by 4 quadrant is over the cap, so the chunks
    /// are the 2 by 2 cell blocks and their boundaries run along x and z 2, 4 and 6 inside the one patch. The probe
    /// points lie on those boundaries, including shared vertices.</summary>
    internal static FinePatchFixture FinePatch()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch patch = AddPatch(set, "fine", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 8, 8,
            (x, _) => 120 + 2 * x);
        for (int i = 0; i < 8; i++)
        {
            patch.EdgeSubdivisions.Add(new(i, 0, MapCellEdge.South, 4));
            patch.EdgeSubdivisions.Add(new(i, 7, MapCellEdge.North, 4));
            patch.EdgeSubdivisions.Add(new(0, i, MapCellEdge.West, 4));
            patch.EdgeSubdivisions.Add(new(7, i, MapCellEdge.East, 4));
        }
        TerrainFixture f = Terrain(set);
        var points = new (float X, float Z)[]
        {
            (2f, 1f), (2f, 2f), (2f, 3.5f), (4f, 4f), (4f, 5.25f), (6f, 0.75f),
            (1.5f, 2f), (3f, 4f), (6.5f, 6f), (5f, 2f), (6f, 6f), (4f, 7.5f),
        };
        return new FinePatchFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount,
            points.Select(p => new Vector3(p.X, FineHeight(p.X), p.Z)).ToArray());
    }

    internal static float FineHeight(float x) => (float)(1.2 + 0.02 * x);

    /// <summary>A LegacyTileWorld row of four cells under the legacy exterior recipe: cell 1 is NoDraw, cell 2 has no
    /// underlay and cell 3 is absent, so cells 1 and 2 are fallback cells.</summary>
    internal static LegacyFallbackFixture LegacyExteriorWithFallback()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch row = AddPatch(set, "legacy-row", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            0, 0, 4, 1, (_, _) => 0, policy: MapPresencePolicy.LegacyTileWorld);
        row.Heights = new[] { 0, 100, 300, 300, 300, 0, 200, 600, 600, 600 };
        row.Cells[1] = row.Cells[1] with { Flags = MapCellFlags.NoDraw };
        row.Cells[2] = row.Cells[2] with { Underlay = 0 };
        row.SetPresent(3, 0, false);
        row.Records.Add(new MapSpaceDoc("legacy-world", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        row.Records.Add(new MapSpaceFootprint("legacy-world-cells", new("legacy-world", row.Key), row.Key, new[] { 0, 1, 2 },
            new(MapBoundKind.LegacyExteriorV1, "legacy-row", null), new(MapBoundKind.OpenTop, null, null)));
        TerrainFixture f = Terrain(set);
        return new LegacyFallbackFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount, row.Key, new[] { 1, 2 });
    }

    /// <summary>One flat metre cell with all four edges subdivided into 32 segments: 2 x (31 + 31 + 3) = 130 faces in
    /// one slot cell.</summary>
    internal static TerrainFixture DenseCell()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch patch = AddPatch(set, "dense", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 1, 1, (_, _) => 0);
        foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>()) patch.EdgeSubdivisions.Add(new(0, 0, edge, 32));
        return Terrain(set);
    }

    /// <summary>The patch that records wall strip <c>wall-1</c>, and not <c>wall-10</c>.</summary>
    internal static readonly MapPatchKey WallOneYard = new("wall-one-yard", 0, 0);

    /// <summary>Two front-facing walls 2.5 m tall made of one 4 m segment each: <c>wall-1</c> at z 0.5 recorded in
    /// <see cref="WallOneYard"/>, and <c>wall-10</c> at z 20.5 recorded in a second yard. Each compiles to the one
    /// chunk <c>stripId/0/Front</c>.</summary>
    internal static TerrainFixture WallPrefixYards()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch one = AddPatch(set, WallOneYard.SurfaceId, MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 2, 2, 2,
            (_, _) => 0);
        MapSurfacePatch ten = AddPatch(set, "wall-ten-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 22, 2, 2,
            (_, _) => 0);
        AddStrip(one, "wall-1", new (long, long)[] { (1, 1), (9, 1) }, MapStripFacing.Front);
        AddStrip(ten, "wall-10", new (long, long)[] { (1, 41), (9, 41) }, MapStripFacing.Front);
        return Terrain(set);
    }

    /// <summary>A straight front-facing wall 2.5 m tall at z 0.5, from x 0.5 to x 0.5 plus
    /// <paramref name="lengthMetres"/> in 4 m segments with a shorter last one, hosted on a 2 by 2 cell yard
    /// floor.</summary>
    internal static TerrainFixture LongWallStrip(float lengthMetres)
    {
        long halves = checked((long)Math.Round(lengthMetres * 2.0));
        string id = "long-wall-" + halves;
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, id + "-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 2, 2, 2, (_, _) => 0);
        var points = new List<(long X, long Z)>();
        for (long h = 0; h < halves; h += 8) points.Add((1 + h, 1));
        points.Add((1 + halves, 1));
        AddStrip(yard, id, points, MapStripFacing.Front);
        return Terrain(set);
    }

    /// <summary>A 4 by 4 cell LegacyTileWorld patch with every corner at <paramref name="heightMetres"/>.</summary>
    internal static TerrainFixture HighLegacyPatch(float heightMetres)
    {
        int centimetres = checked((int)Math.Round(heightMetres * 100.0));
        var set = new MapSurfaceSet();
        AddPatch(set, "high-legacy-" + centimetres, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            0, 0, 4, 4, (_, _) => centimetres, policy: MapPresencePolicy.LegacyTileWorld);
        return Terrain(set);
    }

    /// <summary>A two-sided wall 2.5 m tall at x 1.5, from z 0.5 to z 8.5 in 2 m segments, hosted on a yard
    /// floor.</summary>
    internal static TerrainFixture TwoSidedStrip()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "two-sided-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 4, 0, 2, 2, (_, _) => 0);
        AddStrip(yard, "two-sided-wall", new (long, long)[] { (3, 1), (3, 5), (3, 9), (3, 13), (3, 17) }, MapStripFacing.TwoSided);
        return Terrain(set);
    }

    /// <summary>One metre cell with its south-west corner 200 m above the other three and the diagonal forced through
    /// that corner, so both faces span 200 m vertically and neither fits any anchor.</summary>
    internal static TerrainFixture TallCornerCell()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch patch = AddPatch(set, "tall-corner", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 1, 1,
            (x, z) => x == 0 && z == 0 ? 20000 : 0);
        patch.Cells[0] = patch.Cells[0] with { Topology = MapCellTopology.ForceSwNe };
        return Terrain(set);
    }

    /// <summary>A front-facing wall 2.5 m tall made of one 200 m segment at z 0.5, from x 0.5 to x 200.5, so neither
    /// of its two faces fits any anchor. Its yard floor sits at cells [0, 2) by [2, 4).</summary>
    internal static TerrainFixture LongSegmentStrip()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "long-segment-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 2, 2, 2,
            (_, _) => 0);
        AddStrip(yard, "long-segment", new (long, long)[] { (1, 1), (401, 1) }, MapStripFacing.Front);
        return Terrain(set);
    }

    /// <summary>Whether <paramref name="point"/> lies within the closed world XZ bounds of the chunk's vertices.</summary>
    internal static bool CoversXz(MapTerrainChunk chunk, Vector3 point)
    {
        var anchor = new Vector3(chunk.Anchor.X, chunk.Anchor.Y, chunk.Anchor.Z);
        Vector3[] world = chunk.Shape.Vertices.Select(v => anchor + v).ToArray();
        return point.X >= world.Min(v => v.X) && point.X <= world.Max(v => v.X) &&
            point.Z >= world.Min(v => v.Z) && point.Z <= world.Max(v => v.Z);
    }

    /// <summary>An acquired view that is not complete: its yard holds a wall strip whose chains do not exist.</summary>
    internal static MapScopedSurfaces IncompleteView()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "dangling-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 2, 2, (_, _) => 0);
        yard.Records.Add(new MapWallStrip("dangling-wall", new("missing-lower", yard.Key), new("missing-upper", yard.Key),
            MapStripFacing.Front, 1));
        var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(-1f, -1f), new Vector2(3f, 3f), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
        MapScopedSurfaces view = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(Resolve(set).Document), scope,
            Array.Empty<string>());
        return view.Status == MapAcquireStatus.Incomplete ? view
            : throw new InvalidOperationException("incomplete view fixture acquired as " + view.Status);
    }

    /// <summary>A complete view whose wall strip's upper chain runs along a declared ceiling that has no patch.</summary>
    internal static MapScopedSurfaces UnresolvedStripView()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "unresolved-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 2, 2, (_, _) => 0);
        set.Refs.Add(new MapSurfaceRef("unresolved-lid", CentimetreHeights, MapSurfaceRole.Ceiling, MapPresencePolicy.Native,
            null, null, ""));
        yard.Records.Add(EdgeChain("unresolved-lower", yard.Key, 0, 0, 2, 0));
        yard.Records.Add(EdgeChain("unresolved-upper", new("unresolved-lid", 0, 0), 0, 0, 2, 0));
        yard.Records.Add(new MapWallStrip("unresolved-wall", new("unresolved-lower", yard.Key), new("unresolved-upper", yard.Key),
            MapStripFacing.Front, 1));
        return MapScopedSurfaces.CompleteView(set);
    }

    /// <summary>Whether the backend front of chunk triangle <paramref name="t"/>, <c>cross(C - A, B - A)</c> of its
    /// emitted vertices, points along the normal R2 compiled for its face. Throws when no terrain fixture registered
    /// the face.</summary>
    internal static bool BackendFrontAlongCompiledNormal(MapTerrainChunk chunk, int t)
    {
        if (!CompiledNormals.TryGetValue(chunk.TriangleOwners[t], out Vector3 normal))
            throw new InvalidOperationException("chunk " + chunk.ChunkId + " triangle " + t + " owner " +
                chunk.TriangleOwners[t] + " has no registered compiled normal");
        (Vector3 a, Vector3 b, Vector3 c) = Triangle(chunk, t);
        return Vector3.Dot(Vector3.Normalize(Vector3.Cross(c - a, b - a)), normal) > 0.999f;
    }

    /// <summary>The world pose of an upright probe capsule whose nearest point lies <see cref="ProbeGap"/> in front of
    /// one of the chunk's faces. A floor or ceiling face takes the cap end nearest its centroid. A vertical wall face
    /// is parallel to an upright capsule, which the feature query refuses as ambiguous, so a wall probe sits just above
    /// the midpoint of a top edge, in front of the face.</summary>
    internal static Pose ProbeNear(MapTerrainChunk chunk)
    {
        var anchor = new Vector3(chunk.Anchor.X, chunk.Anchor.Y, chunk.Anchor.Z);
        for (int t = 0; t < chunk.TriangleOwners.Count; t++)
        {
            (Vector3 a, Vector3 b, Vector3 c) = Triangle(chunk, t);
            Vector3 front = Vector3.Normalize(Vector3.Cross(c - a, b - a));
            Vector3 reach = front * (ProbeRadius + ProbeGap);
            if (front.Y != 0f)
                return Pose.At(anchor + (a + b + c) / 3f + reach + Vector3.UnitY * MathF.CopySign(ProbeLength / 2f, front.Y));
            float top = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            Vector3[] edge = new[] { a, b, c }.Where(v => v.Y == top).ToArray();
            if (edge.Length == 2)
                return Pose.At(anchor + (edge[0] + edge[1]) / 2f + reach + Vector3.UnitY * (0.001f + ProbeLength / 2f));
        }
        throw new InvalidOperationException("chunk " + chunk.ChunkId + " has no probe face");
    }

    static (Vector3 A, Vector3 B, Vector3 C) Triangle(MapTerrainChunk chunk, int t) => (
        chunk.Shape.Vertices[chunk.Shape.Indices[3 * t]], chunk.Shape.Vertices[chunk.Shape.Indices[3 * t + 1]],
        chunk.Shape.Vertices[chunk.Shape.Indices[3 * t + 2]]);

    /// <summary>Validates the topology references, resolves the document and registers every face R2 compiles from
    /// its present patches and wall strips.</summary>
    static TerrainFixture Terrain(MapSurfaceSet set)
    {
        IReadOnlyList<string> errors = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (errors.Count != 0)
            throw new InvalidOperationException("terrain fixture reference validation failed: " + string.Join(", ", errors));
        NativeFixture f = Resolve(set);
        int faces = 0;
        foreach (MapPatchKey key in f.View.Witness.Present.Select(p => p.Key))
        {
            MapSurfaceRef surface = f.View.Surfaces.Single(s => s.Id == key.SurfaceId);
            if (surface.Role != MapSurfaceRole.PaintOverride)
                faces += Register(MapSurfaceCompiler.Compile(surface, f.View.Patch(key).Patch!).Faces);
            foreach (MapWallStrip strip in f.View.RecordsIn(key).OfType<MapWallStrip>())
                faces += Register(MapWallStripCompiler.Compile(strip, Chain(strip.LowerChain), Chain(strip.UpperChain)).Faces);
        }
        return new TerrainFixture(f.Document, f.Assets, f.Resolved, f.View, faces);

        MapChainResolution Chain(MapRecordRef reference)
        {
            f.View.TryRecord(reference, out MapTopologyRecord? record, out _);
            return MapBoundaryGeometry.ResolveChain((MapBoundaryChain)record!, f.View);
        }
    }

    static int Register(IReadOnlyList<MapCompiledFace> faces)
    {
        foreach (MapCompiledFace face in faces)
            if (CompiledNormals.GetOrAdd(face.Key, face.Normal) != face.Normal)
                throw new InvalidOperationException("terrain fixture face " + face.Key + " registered with two normals");
        return faces.Count;
    }

    /// <summary>A full rectangle of cells [minX, minX + width) by [minZ, minZ + depth) in slot (0, 0) of a new surface.
    /// <paramref name="height"/> maps a lattice corner to height units and <paramref name="present"/> a cell to its
    /// presence.</summary>
    static MapSurfacePatch AddPatch(MapSurfaceSet set, string id, MapSurfaceRole role, MapLatticeFrame frame, int minX,
        int minZ, int width, int depth, Func<int, int, int> height, Func<int, int, bool>? present = null,
        MapPresencePolicy policy = MapPresencePolicy.Native)
    {
        set.Refs.Add(new MapSurfaceRef(id, frame, role, policy, null, null, ""));
        var patch = new MapSurfacePatch
        {
            Key = new(id, 0, 0),
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = new int[(width + 1) * (depth + 1)],
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None,
                MapCellTopology.Auto), width * depth).ToArray(),
            Presence = new ulong[(width * depth + 63) / 64],
        };
        for (int z = 0; z <= depth; z++)
            for (int x = 0; x <= width; x++)
                patch.Heights[z * (width + 1) + x] = height(minX + x, minZ + z);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                patch.SetPresent(x, z, present?.Invoke(minX + x, minZ + z) ?? true);
        set.Patches.Add(patch.Key, patch);
        return patch;
    }

    /// <summary>A source-edge chain along whole lattice corners of <paramref name="source"/>.</summary>
    static MapBoundaryChain EdgeChain(string id, MapPatchKey source, int x0, int z0, int x1, int z1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        return new(id, MapChainKind.SurfaceEdge, source, Enumerable.Range(0, steps + 1).Select(i => new MapChainVertex(
            new(source.SurfaceId, MapLatticeAddress.Corner(x0 + Math.Sign(x1 - x0) * i, z0 + Math.Sign(z1 - z0) * i)),
            null)).ToArray());
    }

    /// <summary>An authored strip from y 0 to y 2.5 through half-metre lattice points of the host's surface.</summary>
    static void AddStrip(MapSurfacePatch host, string id, IReadOnlyList<(long X, long Z)> halves, MapStripFacing facing)
    {
        MapChainVertex[] Chain(int height) => halves.Select(p => new MapChainVertex(
            new(host.Key.SurfaceId, MapLatticeAddress.Create(p.X, p.Z, 2)), height)).ToArray();
        host.Records.Add(new MapBoundaryChain(id + "-lower", MapChainKind.Authored, null, Chain(0)));
        host.Records.Add(new MapBoundaryChain(id + "-upper", MapChainKind.Authored, null, Chain(250)));
        host.Records.Add(new MapWallStrip(id, new(id + "-lower", host.Key), new(id + "-upper", host.Key), facing, 1));
    }

    static MapSpaceDoc CaveSpace(string id, IReadOnlyList<MapBoundaryRef> walls, IReadOnlyList<MapRecordRef> links) =>
        new(id, MapSpaceKind.Cave, null, null, Array.Empty<string>(), walls, Array.Empty<MapBoundaryRef>(), links);
}

/// <summary>A terrain fixture with the number of faces R2 compiles from its present patches and wall strips.</summary>
internal record TerrainFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount) : NativeFixture(Document, Assets, Resolved, View)
{
    /// <summary>See <see cref="NativeWorldFixtures.ProbeNear"/>.</summary>
    internal Pose ProbeNear(MapTerrainChunk chunk) => NativeWorldFixtures.ProbeNear(chunk);
}

/// <summary><see cref="NativeWorldFixtures.FinePatch"/>, with world points on its internal chunk boundaries.</summary>
internal sealed record FinePatchFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, IReadOnlyList<Vector3> SeamProbePoints)
    : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount)
{
    /// <summary>The analytic floor height, which depends on x only.</summary>
    internal float HeightAt(float x, float z) => NativeWorldFixtures.FineHeight(x);
}

/// <summary><see cref="NativeWorldFixtures.LegacyExteriorWithFallback"/>, with its fallback slot cells.</summary>
internal sealed record LegacyFallbackFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, MapPatchKey Row, IReadOnlyList<int> FallbackCells)
    : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount)
{
    internal int FallbackCellCount => FallbackCells.Count;

    internal bool IsFallbackFace(MapFaceKey key) => key.Patch == Row && FallbackCells.Contains(key.Primitive);
}

/// <summary><see cref="NativeWorldFixtures.StackedCave"/>, with a point on the lower floor, under the low ceiling,
/// inside the shaft between the slabs and on the upper floor.</summary>
internal sealed record StackedCaveFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, Vector3 LowerFloorPoint, Vector3 LowCeilingPoint, Vector3 ShaftPoint,
    Vector3 UpperFloorPoint) : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount);
