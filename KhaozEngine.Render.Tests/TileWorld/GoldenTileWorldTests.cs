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
}
