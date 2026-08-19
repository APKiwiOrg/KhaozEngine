using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The view's half of the water path: planes collected once per region-plane mesh, submitted through
/// the scene seam every frame, re-collected when the region is remeshed or the look changes, and dropped when
/// the region goes. Driven through the recording fake, so none of it needs a device.</summary>
public class TileWorldViewWaterTests
{
    const ushort Water = 4;
    const ushort Grass = TileRenderTestData.Grass;

    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(12f, 0f, -12f);

    static TileWorldDocument GrassWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-view-water", DisplayName = "Tile view water" };
        doc.GetOrCreateRegion(Origin);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, Grass);
        return doc;
    }

    static void Paint(TileWorldDocument doc, int x, int z, int width, int height)
    {
        for (int tz = z; tz < z + height; tz++)
            for (int tx = x; tx < x + width; tx++)
                doc.SetUnderlay(tx, tz, 0, Water);
    }

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver());

    // One frame: the ordinary draw, then the water submit that a caller adds after it.
    static void Frame(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearFrame();
        scene.ClearWater();
        view.Draw(Focus);
        view.DrawWaterPlanes();
    }

    [Fact]
    public void ALoadedRegionQueuesItsWaterEveryFrame()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 6);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(Origin);

        Frame(view, scene);
        WaterPlane first = Assert.Single(scene.WaterDraws);
        Assert.Equal(11.5d, first.CenterX, 4);
        Assert.Equal(-13d, first.CenterZ, 4);
        Assert.Equal(1.5d, first.HalfExtentX, 4);
        Assert.Equal(3d, first.HalfExtentZ, 4);

        // The pass clears its queue every Begin, so the plane has to be resubmitted rather than uploaded once.
        Frame(view, scene);
        Assert.Single(scene.WaterDraws);
    }

    [Fact]
    public void ARegionWithNoWaterQueuesNothing()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, GrassWorld());
        view.LoadRegion(Origin);

        Frame(view, scene);
        Assert.Empty(scene.WaterDraws);
    }

    [Fact]
    public void ARemeshReplacesTheCachedPlanes()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 6);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(Origin);
        Frame(view, scene);
        Assert.Single(scene.WaterDraws);

        // A second body, well clear of the first, plus the mark an editor would raise.
        Paint(doc, 30, 30, 4, 4);
        view.MarkDirty(Origin, 0);
        Frame(view, scene);

        Assert.Equal(2, scene.WaterDraws.Count);
        Assert.Equal(-13d, scene.WaterDraws[0].CenterZ, 4);
        Assert.Equal(-32d, scene.WaterDraws[1].CenterZ, 4);
    }

    [Fact]
    public void UnloadingARegionDropsItsWater()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 6);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(Origin);
        Frame(view, scene);
        Assert.Single(scene.WaterDraws);

        view.UnloadRegion(Origin);
        Frame(view, scene);

        Assert.Empty(scene.WaterDraws);
        Assert.Equal(0, view.WaterCacheCount);
    }

    [Fact]
    public void ThePlanesCarryTheRiverLookUnlessOverridden()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 6);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(Origin);

        Frame(view, scene);
        Assert.Same(TileWaterLooks.River, Assert.Single(scene.WaterDraws).Look);

        // Swapping the look re-collects even though nothing was remeshed.
        var mine = new WaterLook { Opacity = 0.5f };
        view.WaterLook = mine;
        Frame(view, scene);
        Assert.Same(mine, Assert.Single(scene.WaterDraws).Look);

        // Null means the scene's own water settings, which is what a plane with no look draws with.
        view.WaterLook = null;
        Frame(view, scene);
        Assert.Null(Assert.Single(scene.WaterDraws).Look);
    }

    [Fact]
    public void TheRiverLookIsCalmProceduralWaterWithASiltyShoreBand()
    {
        // The values the design pinned, so a retune has to be deliberate rather than a drift.
        WaterLook river = TileWaterLooks.River;
        Assert.Equal(WaterWaveSource.Procedural, river.WaveSource);
        Assert.Equal(0f, river.SwellAmplitude);
        Assert.Equal(0.05f, river.NormalStrength);
        Assert.Equal(0f, river.SurfStrength);
        Assert.Equal(0f, river.FoamCrestCoverage);
        Assert.Equal(0.8f, river.ShallowDepth);
        Assert.NotNull(river.ShallowColor);
        Assert.NotNull(river.FoamShoreWidth);
        Assert.True(river.FoamStrength > 0f, "the shore band is the one foam source a river keeps.");
    }

    [Fact]
    public void DrawingWaterAfterDisposeThrows()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldView view = View(scene, GrassWorld());
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => view.DrawWaterPlanes());
    }

    [Fact]
    public void TheSeamDefaultsToDrawingNoWater()
    {
        // DrawWater is a default interface member, so an ITileWorldScene written before water existed still
        // compiles and simply draws none. Pinned because that default is what keeps the seam additive.
        ITileWorldScene silent = new SilentTileWorldScene();
        silent.DrawWater(new WaterPlane(0f, 0f, 0f, 1f));
    }

    // A seam implementation that overrides nothing optional, which is exactly the pre-water shape.
    sealed class SilentTileWorldScene : ITileWorldScene
    {
        public MeshHandle LoadMesh(GltfMesh mesh) => default;
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) { }
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
                             IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                             Vector3 focus, float drawRadius) => 0;
    }
}
