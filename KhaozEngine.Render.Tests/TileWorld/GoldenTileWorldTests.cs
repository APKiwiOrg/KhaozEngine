using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Cross-backend image regressions for the tile renderer, both captured through
/// <see cref="TileWorldSnapshot"/> over the greybox world (a hill, a road with a diagonal end, and a walled house
/// with a roof). The pair is complementary on the roof rule: the perspective shot stands its observer inside the
/// house, so the roofs are hidden and the walls, the doorway gap and the lit interior are what the camera sees,
/// while the top-down shot stands its observer above the world, so the same roofs are drawn over the floor. A
/// regression in the ground mesher, the corner blend, the lattice normals, the prop yaw convention or the roof
/// rule moves cells well past the golden tolerance. Skipped unless KE_GPU_TESTS=1.</summary>
public sealed class GoldenTileWorldTests
{
    const int PerspectiveWidth = 320;
    const int PerspectiveHeight = 240;

    const int TopDownTiles = 32;
    const int TopDownPxPerTile = 4;
    const int TopDownSize = TopDownTiles * TopDownPxPerTile;

    // The background pins the other 3D goldens use: no starfield, no outline, one flat colour. The tile renderer
    // is what these images exist to lock, and a procedural star field would spend cells of the comparison grid on
    // pixels that have nothing to do with it.
    static readonly Action<Scene3D> FlatBackground = scene =>
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.BackgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
    };

    /// <summary>The greybox world from an eye south-east of the house and above it, looking down at the house,
    /// which is north-west of the eye. World z is minus tile z, so the house's tile (12, 11) is world (12, -11)
    /// and south-east of it is +x and +z. The observer defaults to the tile under the target, which the house
    /// flags indoors, so this is the roofs-hidden half of the pair.</summary>
    [GpuFact]
    public void Golden3D_TileWorld_Greybox()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        byte[] rgba = TileWorldSnapshot.CapturePerspective(
            doc,
            TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            eye: new Vector3(30f, 18f, 6f),
            target: new Vector3(12f, 0f, -11f),
            PerspectiveWidth,
            PerspectiveHeight,
            configureScene: FlatBackground);

        GoldenCompare.AssertOrUpdate("tileworld_greybox", rgba, PerspectiveWidth, PerspectiveHeight);
    }

    /// <summary>The same world straight down over its south-west 32 tiles at four pixels a tile, which covers the
    /// road, the house and the hill. The observer stands above the world, so this is the roofs-drawn half. North
    /// is UP and east is RIGHT on the image, both at once, which is what the tile-to-world z flip buys.</summary>
    [GpuFact]
    public void Golden3D_TileWorld_TopDown()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        byte[] rgba = TileWorldSnapshot.CaptureTopDown(
            doc,
            TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            new TileRect(0, 0, TopDownTiles, TopDownTiles),
            plane: 0,
            TopDownPxPerTile,
            configureScene: FlatBackground);

        GoldenCompare.AssertOrUpdate("tileworld_topdown", rgba, TopDownSize, TopDownSize);
    }

    // The greybox golden's eye pulled in to 55% of its distance along the same ray, so the framing is that shot's
    // and the ground is close enough for an 8x8 checker at half a repeat per metre to read as a board rather than
    // as its own average. The target is the greybox shot's, unmoved.
    static readonly Vector3 TexturedEye = new(21.9f, 9.9f, -1.65f);
    static readonly Vector3 TexturedTarget = new(12f, 0f, -11f);

    /// <summary>The greybox world through the same pipeline with a GENERATED texture set: an eight texel checker
    /// per material instead of the flat colour fill a catalog with no textures falls back to. Nothing else moves,
    /// so this golden and <see cref="Golden3D_TileWorld_Greybox"/> differ by the material set alone, and a
    /// regression in the texture array upload, the mip chain, the per-layer tiling rate or the slot a vertex names
    /// changes this image while leaving the flat one alone. The greybox world has ONE underlay, so the corner blend
    /// this exercises is the overlay path: the road carries its own brown checker and the house floor a flat wood,
    /// both against checkered grass.</summary>
    [GpuFact]
    public void Golden3D_TileWorld_Textured()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var options = new TileWorldViewOptions { GroundMaterials = TileRenderTestData.CheckerMaterials(catalogs) };

        byte[] rgba = TileWorldSnapshot.CapturePerspective(
            doc,
            catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            eye: TexturedEye,
            target: TexturedTarget,
            PerspectiveWidth,
            PerspectiveHeight,
            options: options,
            configureScene: FlatBackground);

        GoldenCompare.AssertOrUpdate("tileworld_textured", rgba, PerspectiveWidth, PerspectiveHeight);
    }

    // An eye on the grass east of the river's dirt bank, looking north-west along the channel and down into it at
    // about forty degrees. Close enough that the water runs corner to corner across the lower half of the frame
    // rather than as a thread at the horizon, which is what puts it in enough comparison cells to be worth
    // holding, and steep enough to see the depth grade over the carved bed rather than a grazing sheet of glint.
    static readonly Vector3 RiverEye = new(35f, 9f, -2f);
    static readonly Vector3 RiverTarget = new(31.5f, -0.4f, -10f);

    /// <summary>The river world, which is the greybox one with a three-tile water strip carved 70 cm into it. The
    /// water is drawn by the engine's water pass, one plane per body at the body's rim height less two
    /// centimetres, with <c>TileWaterLooks.River</c>. This is the only golden where the ground pipeline's linear
    /// depth output is READ rather than merely written: the pass grades the surface by how far the bed sits under
    /// it and discards where the ground is above it, so a regression in that MRT turns the river into a flat slab,
    /// floods the banks, or deletes it outright.</summary>
    [GpuFact]
    public void Golden3D_TileWorld_River()
    {
        TileWorldDocument doc = TileRenderTestData.RiverWorld();
        byte[] rgba = TileWorldSnapshot.CapturePerspective(
            doc,
            TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            eye: RiverEye,
            target: RiverTarget,
            PerspectiveWidth,
            PerspectiveHeight,
            configureScene: FlatBackground);

        GoldenCompare.AssertOrUpdate("tileworld_river", rgba, PerspectiveWidth, PerspectiveHeight);
    }
}
