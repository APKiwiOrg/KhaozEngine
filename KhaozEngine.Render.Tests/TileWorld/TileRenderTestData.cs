using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Shared greybox worlds for the tile renderer tests: a hill, a road and a small house, each authored
/// through the document's public API over one region of grass.</summary>
public static class TileRenderTestData
{
    /// <summary>The single region every world here authors into.</summary>
    public static RegionCoord Region { get; } = new(0, 0);

    /// <summary>Greybox ground material id for grass.</summary>
    public const ushort Grass = 1;
    /// <summary>Greybox ground material id for the dirt the river banks are painted with.</summary>
    public const ushort Dirt = 2;
    /// <summary>Greybox ground material id for water, the only one of the greybox six whose kind is
    /// <see cref="GroundMaterialKind.Water"/>.</summary>
    public const ushort Water = 4;
    /// <summary>Greybox ground material id for the wood floor the house stands on.</summary>
    public const ushort WoodFloor = 5;
    /// <summary>Greybox ground material id for the road.</summary>
    public const ushort Road = 6;

    /// <summary>Height in centimetres the hill's raised corners sit at.</summary>
    public const short HillHeightCm = 200;
    /// <summary>Lowest x and z of the hill's raised corner block.</summary>
    public const int HillMin = 20;
    /// <summary>Highest x and z of the hill's raised corner block, inclusive.</summary>
    public const int HillMax = 22;

    /// <summary>Tile z the road runs along.</summary>
    public const int RoadZ = 10;
    /// <summary>Lowest tile x of the road's full-tile run.</summary>
    public const int RoadMinX = 5;
    /// <summary>Highest tile x of the road's full-tile run, inclusive.</summary>
    public const int RoadMaxX = 8;
    /// <summary>Tile x of the road's diagonal-half end, rotation 0.</summary>
    public const int RoadDiagonalX = 9;

    /// <summary>Lowest tile x of the house floor.</summary>
    public const int HouseMinX = 10;
    /// <summary>Highest tile x of the house floor, inclusive.</summary>
    public const int HouseMaxX = 12;
    /// <summary>Lowest tile z of the house floor, its south row.</summary>
    public const int HouseMinZ = 10;
    /// <summary>Highest tile z of the house floor, its north row.</summary>
    public const int HouseMaxZ = 11;
    /// <summary>Tile x of the doorway in the house's south wall.</summary>
    public const int HouseDoorX = 11;
    /// <summary>Plane the house roof sits on.</summary>
    public const int RoofPlane = 1;

    /// <summary>Lowest tile x of the river's three-tile water strip.</summary>
    public const int RiverMinX = 30;
    /// <summary>Highest tile x of the river's three-tile water strip, inclusive.</summary>
    public const int RiverMaxX = 32;
    /// <summary>Height in centimetres the river's two interior corner columns are carved down to. The rim columns
    /// the water shares with its banks stay at 0, so the body's rim is 0 and its surface lands two centimetres
    /// under that: 68 cm of water over the bed, and a bank 2 cm proud of the surface rather than under it.</summary>
    public const short RiverBedCm = -70;

    /// <summary>A fresh copy of the engine's greybox catalogs, which every world here is authored against.
    /// Fresh per call because the catalogs are mutable and test classes run in parallel.</summary>
    public static TileWorldCatalogs Catalogs => TileWorldCatalogs.Greybox();

    /// <summary>Saves a regionsX by regionsZ block of regions starting at (0, 0), every tile of plane 0 grass,
    /// and returns the world directory to open a <see cref="TileWorldSource"/> on. The streaming tests need a
    /// world on DISK rather than a document, because that is the only way a region is genuinely absent until
    /// something loads it.</summary>
    public static string SaveGrid(TempDir tmp, int regionsX, int regionsZ)
    {
        ArgumentNullException.ThrowIfNull(tmp);
        var doc = new TileWorldDocument { Id = "tile-render-grid", DisplayName = "Tile render grid" };
        FillGrass(doc, regionsX, regionsZ);
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);
        return dir;
    }

    /// <summary>Height in centimetres of the ridge <see cref="SaveRidgeGrid"/> raises.</summary>
    public const short RidgeHeightCm = 300;

    /// <summary>World tile x of the corner column the ridge stands on, which is the border between region (0, 0)
    /// and region (1, 0) and is OWNED by region (1, 0) (local x 0).</summary>
    public const int RidgeCornerX = TileRegion.Size;

    /// <summary>The same grid as <see cref="SaveGrid"/> with the corner column at <see cref="RidgeCornerX"/>
    /// raised to <see cref="RidgeHeightCm"/> on plane 0. That column is region (1, 0)'s to own and region
    /// (0, 0)'s to read across the border, so region (0, 0)'s east edge sits at 3 m with its neighbour resident
    /// and drops to 0 without it. A flat single-material world cannot show that difference at all, which is why
    /// the streaming tests need this one.</summary>
    public static string SaveRidgeGrid(TempDir tmp, int regionsX, int regionsZ)
    {
        ArgumentNullException.ThrowIfNull(tmp);
        if (regionsX < 2) throw new ArgumentOutOfRangeException(nameof(regionsX), "the ridge needs a region either side of it.");
        var doc = new TileWorldDocument { Id = "tile-render-ridge", DisplayName = "Tile render ridge" };
        FillGrass(doc, regionsX, regionsZ);
        for (int z = 0; z < regionsZ * TileRegion.Size; z++)
            doc.SetCornerHeightCm(RidgeCornerX, z, 0, RidgeHeightCm);
        string dir = tmp.Sub("ridge");
        TileWorldFile.Save(doc, dir);
        return dir;
    }

    /// <summary>The centre tile of a region on plane 0, the observer position the streaming tests move about.</summary>
    public static TileCoord CentreOf(RegionCoord region) =>
        new(region.OriginX + TileRegion.Size / 2, region.OriginZ + TileRegion.Size / 2, 0);

    static void FillGrass(TileWorldDocument doc, int regionsX, int regionsZ)
    {
        for (int rz = 0; rz < regionsZ; rz++)
            for (int rx = 0; rx < regionsX; rx++)
            {
                var region = new RegionCoord(rx, rz);
                doc.GetOrCreateRegion(region);
                for (int z = 0; z < TileRegion.Size; z++)
                    for (int x = 0; x < TileRegion.Size; x++)
                        doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0, Grass);
            }
    }

    /// <summary>Grass with a nine-corner block raised to 200 cm: the mesher's slope and lattice-normal case.</summary>
    public static TileWorldDocument HillWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHill(doc);
        return doc;
    }

    /// <summary>Grass with a road overlay: a run of full tiles plus one diagonal-half end, the shaped-overlay case.</summary>
    public static TileWorldDocument RoadWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddRoad(doc);
        return doc;
    }

    /// <summary>Grass with a six-tile indoor wood floor, walls and a doorway around it, and a roof on plane 1:
    /// the object, rotation and roof-rule case.</summary>
    public static TileWorldDocument HouseWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHouse(doc);
        return doc;
    }

    /// <summary>Hill, road and house in one world, the subject of the cross-backend golden.</summary>
    public static TileWorldDocument GreyboxWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHill(doc);
        AddRoad(doc);
        AddHouse(doc);
        return doc;
    }

    /// <summary>The greybox world with a river cut through it: a three-tile water strip running the region's whole
    /// north-south span at x <see cref="RiverMinX"/>..<see cref="RiverMaxX"/>, its bed carved to
    /// <see cref="RiverBedCm"/>, a dirt bank one tile either side, and every water tile blocked. The strip is
    /// eight tiles east of the hill and eighteen east of the house on purpose: a water body takes the MAXIMUM
    /// corner height over its own tiles as its rim, so a river that touched the 200 cm hill would surface at
    /// 1.98 m and flood everything around it.</summary>
    public static TileWorldDocument RiverWorld()
    {
        TileWorldDocument doc = GreyboxWorld();
        AddRiver(doc);
        return doc;
    }

    // One region of flat grass on plane 0, the base every world above decorates. The three features occupy
    // disjoint tiles, so GreyboxWorld can apply all of them to one base.
    static TileWorldDocument GrassWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-render-tests", DisplayName = "Tile render tests" };
        doc.GetOrCreateRegion(Region);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, Grass);
        return doc;
    }

    static void AddHill(TileWorldDocument doc)
    {
        for (int z = HillMin; z <= HillMax; z++)
            for (int x = HillMin; x <= HillMax; x++)
                doc.SetCornerHeightCm(x, z, 0, HillHeightCm);
    }

    static void AddRoad(TileWorldDocument doc)
    {
        for (int x = RoadMinX; x <= RoadMaxX; x++)
        {
            doc.SetOverlay(x, RoadZ, 0, Road);
            doc.SetOverlayShape(x, RoadZ, 0, TileOverlayShape.Full);
        }

        doc.SetOverlay(RoadDiagonalX, RoadZ, 0, Road);
        doc.SetOverlayShape(RoadDiagonalX, RoadZ, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(RoadDiagonalX, RoadZ, 0, 0);
    }

    static void AddHouse(TileWorldDocument doc)
    {
        for (int z = HouseMinZ; z <= HouseMaxZ; z++)
            for (int x = HouseMinX; x <= HouseMaxX; x++)
            {
                doc.SetOverlay(x, z, 0, WoodFloor);
                doc.SetOverlayShape(x, z, 0, TileOverlayShape.Full);
                doc.SetSettings(x, z, 0, TileSettings.Indoors);
                doc.AddObject("roof_flat", x, z, RoofPlane, 0);
            }

        // A wall occupies one EDGE of its tile (rotation 0 west, 1 north, 2 east, 3 south), so a tile can hold
        // several of them. The middle of the south run is a doorway rather than a wall.
        for (int x = HouseMinX; x <= HouseMaxX; x++)
        {
            doc.AddObject(x == HouseDoorX ? "doorway" : "wall", x, HouseMinZ, 0, 3);
            doc.AddObject("wall", x, HouseMaxZ, 0, 1);
        }

        for (int z = HouseMinZ; z <= HouseMaxZ; z++)
        {
            doc.AddObject("wall", HouseMinX, z, 0, 0);
            doc.AddObject("wall", HouseMaxX, z, 0, 2);
        }
    }

    static void AddRiver(TileWorldDocument doc)
    {
        for (int z = 0; z < TileRegion.Size; z++)
        {
            for (int x = RiverMinX; x <= RiverMaxX; x++)
            {
                doc.SetUnderlay(x, z, 0, Water);
                doc.SetSettings(x, z, 0, TileSettings.Blocked);
            }

            doc.SetUnderlay(RiverMinX - 1, z, 0, Dirt);
            doc.SetUnderlay(RiverMaxX + 1, z, 0, Dirt);

            // Only the two INTERIOR corner columns are carved. The rim columns (RiverMinX and RiverMaxX + 1) are
            // shared with the bank tiles either side, so leaving them at 0 keeps the bank dry, turns the outer
            // two water tiles into the sloped sides of the channel, and pins the body's rim at 0. The row of
            // corners at z = TileRegion.Size belongs to the region north of this one, which the world does not
            // have, so the northernmost tile ramps back up to 0 at the far edge of a 64 tile run.
            doc.SetCornerHeightCm(RiverMinX + 1, z, 0, RiverBedCm);
            doc.SetCornerHeightCm(RiverMaxX, z, 0, RiverBedCm);
        }
    }

    /// <summary>Texels on a side of every layer <see cref="CheckerMaterials"/> builds.</summary>
    public const int CheckerLayerSize = 8;

    /// <summary>Texels on a side of one checker square, so a layer is a four by four board. One texel a square
    /// would be gone by the first mip level and read as a flat average from a couple of metres out, which is a
    /// texture set that cannot show whether it was sampled at all.</summary>
    public const int CheckerSquareTexels = 2;

    /// <summary>A generated ground material set for these catalogs, so a textured world can be rendered with no
    /// files on disk: one <see cref="CheckerLayerSize"/> square layer per material in ascending id order and the
    /// reserved magenta layer last, which is exactly the slot map <see cref="TileGroundMaterials.Build"/> hands
    /// out for the same catalogs. Grass is a green checker, dirt and road are a brown and tan one, and every
    /// other material is a flat fill of its catalog colour. Built through the set's own constructor rather than
    /// through <c>Build</c> with an injected loader, because these pixels come from nowhere: there is no path to
    /// hand a loader, and the constructor is the API a game's own atlas comes through.</summary>
    public static TileGroundMaterialSet CheckerMaterials(TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);

        var ids = new List<ushort>(catalogs.Materials.Keys);
        ids.Sort();
        var layers = new TileGroundLayerImage[ids.Count + 1];
        for (int slot = 0; slot < ids.Count; slot++)
        {
            ushort id = ids[slot];
            layers[slot] = id switch
            {
                Grass => Checker(GrassLight, GrassDark),
                Dirt or Road => Checker(BankTan, BankBrown),
                _ => Checker(TileColors.Parse(catalogs.Materials[id]), TileColors.Parse(catalogs.Materials[id])),
            };
        }

        layers[^1] = Checker(TileGroundMesher.MissingMaterialColor, TileGroundMesher.MissingMaterialColor);
        return new TileGroundMaterialSet(CheckerLayerSize, CheckerLayerSize, ids, layers);
    }

    static readonly Vector4 GrassLight = new(0.36f, 0.60f, 0.26f, 1f);
    static readonly Vector4 GrassDark = new(0.17f, 0.34f, 0.14f, 1f);
    static readonly Vector4 BankTan = new(0.72f, 0.60f, 0.40f, 1f);
    static readonly Vector4 BankBrown = new(0.42f, 0.31f, 0.17f, 1f);

    // One layer of a two-colour checker, CheckerSquareTexels texels to a square, row major RGBA8. Alpha is forced
    // opaque for the same reason the flat fill in TileGroundMaterials forces it: the ground is the thing
    // everything else is drawn against. The two colours being equal is how this builds a flat layer.
    static TileGroundLayerImage Checker(Vector4 light, Vector4 dark)
    {
        var pixels = new byte[CheckerLayerSize * CheckerLayerSize * 4];
        for (int y = 0; y < CheckerLayerSize; y++)
            for (int x = 0; x < CheckerLayerSize; x++)
            {
                Vector4 c = ((x / CheckerSquareTexels + y / CheckerSquareTexels) & 1) == 0 ? light : dark;
                int at = (y * CheckerLayerSize + x) * 4;
                pixels[at] = Channel(c.X);
                pixels[at + 1] = Channel(c.Y);
                pixels[at + 2] = Channel(c.Z);
                pixels[at + 3] = 0xff;
            }

        return new TileGroundLayerImage
        {
            AlbedoRgba = pixels,
            TilesPerMetre = TileGroundMaterials.DefaultTilesPerMetre,
        };
    }

    static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}

/// <summary>A throwaway directory under the OS temp root, deleted on dispose. A copy of the one in
/// <c>KhaozEngine.TileWorld.Tests</c> rather than a shared helper, because a test project references only the
/// engine projects its tests use and never another test project.</summary>
public sealed class TempDir : IDisposable
{
    /// <summary>The directory itself, created and empty.</summary>
    public string Path { get; }

    /// <summary>Creates the directory under a per-run GUID, so parallel test classes never collide.</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ke-tilerender-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    /// <summary>A path inside this directory, which need not exist yet.</summary>
    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Deletes the directory and everything under it, swallowing the races a temp cleanup can lose.</summary>
    public void Dispose()
    {
        try { System.IO.Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One prop-draw call as <see cref="RecordingTileWorldScene"/> saw it.</summary>
/// <param name="Placements">The placement list handed in, so a test can name the archetypes it carried.</param>
/// <param name="Focus">The focus point the call culled around.</param>
/// <param name="DrawRadius">The horizontal draw radius the call culled with.</param>
/// <param name="Drawn">How many placements survived the cull and had a loaded mesh.</param>
public sealed record TilePropDrawRecord(
    IReadOnlyList<PropPlacement> Placements, Vector3 Focus, float DrawRadius, int Drawn);

/// <summary>A device-free <see cref="ITileWorldScene"/> that hands out an incrementing handle per upload and
/// records every load, unload and draw, so a test asserts exact handle counts and exact draw contents. Unloading
/// a handle that is not live throws, which turns a double free in the view into a failing test rather than a
/// silent leak of the bug into a real device.</summary>
public sealed partial class RecordingTileWorldScene : ITileWorldScene
{
    readonly HashSet<int> _alive = new();
    int _next;

    /// <summary>Every ground-mesh handle handed out, in upload order.</summary>
    public List<MeshHandle> MeshLoads { get; } = new();

    /// <summary>Every handle freed, in unload order, ground meshes and prop parts alike.</summary>
    public List<MeshHandle> MeshUnloads { get; } = new();

    /// <summary>Every prop part-list handed out, in upload order, one entry per archetype uploaded.</summary>
    public List<IReadOnlyList<MeshHandle>> PropMeshLoads { get; } = new();

    /// <summary>Every ground material set uploaded, in upload order. A view uploads exactly one.</summary>
    public List<TileGroundMaterialSet> MaterialLoads { get; } = new();

    /// <summary>Every ground material handle freed, in unload order.</summary>
    public List<TileGroundMaterialHandle> MaterialUnloads { get; } = new();

    /// <summary>The ground material each <see cref="MeshLoads"/> entry was uploaded with, same order and same
    /// length, so a test can assert that every region-plane mesh went up bound to the set.</summary>
    public List<TileGroundMaterialHandle> MeshMaterials { get; } = new();

    /// <summary>The ground mesh behind each handle index, so a test can read the vertices a rebuild produced
    /// rather than only see that the handle changed.</summary>
    public Dictionary<int, GltfMesh> GroundMeshes { get; } = new();

    /// <summary>The mesh draws of the frames since the last <see cref="ClearFrame"/>, in submission order.</summary>
    public List<(MeshHandle Handle, Matrix4x4 World)> Drawn { get; } = new();

    /// <summary>The prop draws of the frames since the last <see cref="ClearFrame"/>, in submission order.</summary>
    public List<TilePropDrawRecord> PropDraws { get; } = new();

    /// <summary>How many handles are live right now, uploaded and not yet freed.</summary>
    public int AliveMeshCount => _alive.Count;

    /// <summary>Makes the Nth <see cref="LoadMesh(GltfMesh)"/> of this scene's life throw instead of uploading, 1-based,
    /// with 0 meaning never. Stands in for a device that runs out of memory or a mesher that trips over one
    /// plane, which is the only way to reach the half-uploaded paths a real device reaches at 3am.</summary>
    public int ThrowOnMeshLoad { get; set; }

    /// <summary>The same for <see cref="LoadPropMeshes"/>, counted over archetypes rather than parts.</summary>
    public int ThrowOnPropMeshLoad { get; set; }

    /// <summary>Forgets the recorded draws, so the next frame's records stand alone. Handles are untouched.</summary>
    public void ClearFrame()
    {
        Drawn.Clear();
        PropDraws.Clear();
    }

    /// <summary>Hands out a fresh live handle for a ground mesh, unless this is the upload
    /// <see cref="ThrowOnMeshLoad"/> names, which throws having uploaded nothing.</summary>
    public MeshHandle LoadMesh(GltfMesh mesh)
    {
        if (MeshLoads.Count + 1 == ThrowOnMeshLoad)
            throw new InvalidOperationException($"the fake refused ground-mesh upload {ThrowOnMeshLoad}.");

        MeshHandle handle = Next();
        MeshLoads.Add(handle);
        GroundMeshes[handle.Index] = mesh;
        return handle;
    }

    /// <summary>Records the material a ground mesh is uploaded with, then uploads it as usual.</summary>
    public MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material)
    {
        MeshMaterials.Add(material);
        try { return LoadMesh(mesh); }
        // The refusal is recorded as an attempt either way, so the two lists stay the same length and a test that
        // walks them in step is not thrown off by the plane the fake refused.
        catch { MeshMaterials.RemoveAt(MeshMaterials.Count - 1); throw; }
    }

    /// <summary>Hands out a live material handle and records the set it was built from.</summary>
    public TileGroundMaterialHandle LoadTileGroundMaterial(TileGroundMaterialSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        MaterialLoads.Add(set);
        return new TileGroundMaterialHandle(MaterialLoads.Count - 1);
    }

    /// <summary>Frees a live material handle. An invalid handle is a no-op, a double free throws.</summary>
    public void UnloadTileGroundMaterial(TileGroundMaterialHandle handle)
    {
        if (!handle.IsValid) return;
        if (MaterialUnloads.Contains(handle))
            throw new InvalidOperationException("the ground material was already unloaded.");
        MaterialUnloads.Add(handle);
    }

    /// <summary>Frees a live handle. A default handle is a no-op, a stale one throws.</summary>
    public void UnloadMesh(MeshHandle handle)
    {
        if (handle.Generation == 0) return;
        if (!_alive.Remove(handle.Index))
            throw new InvalidOperationException($"mesh {handle.Index} was already unloaded.");
        MeshUnloads.Add(handle);
    }

    /// <summary>Records one mesh draw at its world transform.</summary>
    public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Drawn.Add((handle, world));

    /// <summary>Every silhouette draw, in submission order, with the transform, colour and width it rode.</summary>
    public List<(MeshHandle Handle, Matrix4x4 World, KhaozEngine.Primitives.Color Color, float Width)> Silhouettes { get; } = new();

    /// <summary>Records one silhouette draw.</summary>
    public void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, KhaozEngine.Primitives.Color color, float widthMetres) =>
        Silhouettes.Add((handle, world, color, widthMetres));

    /// <summary>Hands out one fresh live handle per part, unless this is the archetype
    /// <see cref="ThrowOnPropMeshLoad"/> names, which throws having uploaded nothing.</summary>
    public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (PropMeshLoads.Count + 1 == ThrowOnPropMeshLoad)
            throw new InvalidOperationException($"the fake refused prop-mesh upload {ThrowOnPropMeshLoad}.");

        var handles = new MeshHandle[parts.Count];
        for (int i = 0; i < parts.Count; i++) handles[i] = Next();
        PropMeshLoads.Add(handles);
        return handles;
    }

    /// <summary>Frees every part handle of one archetype.</summary>
    public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        foreach (MeshHandle handle in handles) UnloadMesh(handle);
    }

    /// <summary>Counts the in-range placements whose archetype has an uploaded mesh, with the same horizontal
    /// cull the real prop path applies, and records the call.</summary>
    public int DrawProps(IReadOnlyList<PropPlacement> placements,
                         IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                         Vector3 focus, float drawRadius)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(parts);

        float r2 = drawRadius * drawRadius;
        int drawn = 0;
        foreach (PropPlacement p in placements)
        {
            float dx = p.X - focus.X, dz = p.Z - focus.Z;
            if (dx * dx + dz * dz > r2) continue;
            if (!parts.ContainsKey(p.Id)) continue;
            drawn++;
        }
        PropDraws.Add(new TilePropDrawRecord(placements, focus, drawRadius, drawn));
        return drawn;
    }

    MeshHandle Next()
    {
        var handle = new MeshHandle(++_next);
        _alive.Add(handle.Index);
        return handle;
    }
}
