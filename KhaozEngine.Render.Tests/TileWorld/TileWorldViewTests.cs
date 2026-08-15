using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The view over the scene seam: one ground mesh per drawable region-plane, prop handles kept for the
/// whole catalog, idempotent loads, dirty coalescing that rebuilds nothing else, the roof rule, and the
/// placeholder an unresolved archetype falls back to. Every test drives a recording fake, so none of this needs
/// a device.</summary>
public class TileWorldViewTests
{
    // Near enough to the house that every one of its props is inside the default draw radius, so a missing draw
    // is the roof rule or a lost handle, never the distance cull.
    static readonly Vector3 HouseFocus = new(11f, 0f, 10.5f);

    // The house's ten wall-family objects on plane 0 and its six roof tiles on plane 1.
    const int HouseGroundProps = 10;
    const int HouseRoofProps = 6;

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc,
                              ITileMeshResolver? resolver = null, TileWorldViewOptions? options = null) =>
        new(scene, doc, TileRenderTestData.Catalogs, resolver ?? new GreyboxMeshResolver(), options);

    // The house with the roof plane floored too, so the region has TWO drawable planes and a rebuild of one can
    // be told apart from a rebuild of the other.
    static TileWorldDocument TwoPlaneHouse()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        for (int z = TileRenderTestData.HouseMinZ; z <= TileRenderTestData.HouseMaxZ; z++)
            for (int x = TileRenderTestData.HouseMinX; x <= TileRenderTestData.HouseMaxX; x++)
                doc.SetUnderlay(x, z, TileRenderTestData.RoofPlane, TileRenderTestData.Grass);
        return doc;
    }

    [Fact]
    public void LoadRegion_creates_one_mesh_per_drawable_plane_and_props()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);

        Assert.Equal(1, view.LoadedRegionCount);
        Assert.Contains(TileRenderTestData.Region, view.LoadedRegions);
        // Only plane 0 has drawable tiles in HouseWorld, so the three empty planes upload nothing.
        Assert.Single(scene.MeshLoads);

        view.Draw(HouseFocus);
        Assert.Single(scene.Drawn);
        Assert.Equal(TileGroundMesher.WorldMatrix(doc, TileRenderTestData.Region), scene.Drawn[0].World);
        Assert.Contains(scene.PropDraws, r => r.Placements.Any(p => p.Id == "wall"));
        Assert.Contains(scene.PropDraws, r => r.Placements.Any(p => p.Id == "roof_flat"));
        Assert.Equal(HouseGroundProps + HouseRoofProps, view.LastDrawnProps);
    }

    [Fact]
    public void LoadRegion_of_a_loaded_region_rebuilds_it_rather_than_leaking()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TileRenderTestData.HouseWorld());
        view.LoadRegion(TileRenderTestData.Region);
        view.LoadRegion(TileRenderTestData.Region);

        Assert.Equal(1, view.LoadedRegionCount);
        Assert.Equal(2, scene.MeshLoads.Count);
        Assert.Single(scene.MeshUnloads);
        Assert.Equal(scene.MeshLoads[0].Index, scene.MeshUnloads[0].Index);

        view.Draw(HouseFocus);
        Assert.Single(scene.Drawn);
        Assert.Equal(scene.MeshLoads[1].Index, scene.Drawn[0].Handle.Index);
    }

    [Fact]
    public void UnloadRegion_frees_every_handle()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoPlaneHouse());
        int archetypeMeshes = scene.AliveMeshCount;   // uploaded once by the constructor, not per region
        Assert.True(archetypeMeshes > 0);

        view.LoadRegion(TileRenderTestData.Region);
        Assert.Equal(archetypeMeshes + 2, scene.AliveMeshCount);

        view.UnloadRegion(TileRenderTestData.Region);
        Assert.Equal(archetypeMeshes, scene.AliveMeshCount);
        Assert.Equal(2, scene.MeshUnloads.Count);
        Assert.Equal(0, view.LoadedRegionCount);
        Assert.Empty(view.LoadedRegions);

        view.Draw(HouseFocus);
        Assert.Empty(scene.Drawn);
        Assert.Equal(0, view.LastDrawnProps);
    }

    [Fact]
    public void MarkDirty_then_Draw_rebuilds_only_the_dirty_region_plane()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoPlaneHouse());
        view.LoadRegion(TileRenderTestData.Region);
        Assert.Equal(2, scene.MeshLoads.Count);

        view.Draw(HouseFocus);
        Assert.Equal(2, scene.Drawn.Count);
        int plane0 = scene.Drawn[0].Handle.Index, plane1 = scene.Drawn[1].Handle.Index;
        scene.ClearFrame();

        view.MarkDirty(TileRenderTestData.Region, 0);
        view.Draw(HouseFocus);
        Assert.Equal(2, scene.Drawn.Count);
        Assert.NotEqual(plane0, scene.Drawn[0].Handle.Index);
        Assert.Equal(plane1, scene.Drawn[1].Handle.Index);
        Assert.Single(scene.MeshUnloads);
        Assert.Equal(plane0, scene.MeshUnloads[0].Index);

        int rebuiltPlane0 = scene.Drawn[0].Handle.Index;
        scene.ClearFrame();

        // The world-rect overload has to land on the same region, and only on the plane it names.
        var rect = new TileRect(TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 3, 2);
        view.MarkDirty(rect, TileRenderTestData.RoofPlane);
        view.Draw(HouseFocus);
        Assert.Equal(2, scene.Drawn.Count);
        Assert.Equal(rebuiltPlane0, scene.Drawn[0].Handle.Index);
        Assert.NotEqual(plane1, scene.Drawn[1].Handle.Index);
        Assert.Equal(2, scene.MeshUnloads.Count);
        Assert.Equal(plane1, scene.MeshUnloads[1].Index);
    }

    [Fact]
    public void Roofs_are_hidden_above_an_indoor_observer_and_shown_otherwise()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TileRenderTestData.HouseWorld());
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = new TileCoord(TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 0);
        Assert.True(view.ObserverIndoors);
        view.Draw(HouseFocus);
        Assert.DoesNotContain(scene.PropDraws, r => r.Placements.Any(p => p.Id == "roof_flat"));
        Assert.Equal(HouseGroundProps, view.LastDrawnProps);
        scene.ClearFrame();

        view.Observer = new TileCoord(0, 0, 0);
        Assert.False(view.ObserverIndoors);
        view.Draw(HouseFocus);
        Assert.Contains(scene.PropDraws, r => r.Placements.Any(p => p.Id == "roof_flat") && r.Drawn == HouseRoofProps);
        Assert.Equal(HouseGroundProps + HouseRoofProps, view.LastDrawnProps);
    }

    [Fact]
    public void Dispose_unloads_all()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldView view = View(scene, TwoPlaneHouse());
        view.LoadRegion(TileRenderTestData.Region);
        Assert.True(scene.AliveMeshCount > 2);

        view.Dispose();
        Assert.Equal(0, scene.AliveMeshCount);
        Assert.Equal(0, view.LoadedRegionCount);

        view.Dispose();   // idempotent: a second dispose must not double free
        Assert.Equal(0, scene.AliveMeshCount);
    }

    [Fact]
    public void Missing_archetype_mesh_gets_a_placeholder_and_one_log_line()
    {
        var scene = new RecordingTileWorldScene();
        var log = new List<string>();
        using TileWorldView view = View(scene, TileRenderTestData.HouseWorld(), new WallLessResolver(),
                                        new TileWorldViewOptions { Log = log.Add });

        Assert.Single(log);
        Assert.Contains("wall", log[0], StringComparison.Ordinal);

        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(HouseFocus);

        // The placeholder took the wall archetype's slot, so its placements still draw.
        Assert.Contains(scene.PropDraws, r => r.Placements.Any(p => p.Id == "wall") && r.Drawn > 0);
        Assert.Equal(HouseGroundProps + HouseRoofProps, view.LastDrawnProps);
    }

    // Greybox everywhere except "wall", which resolves to nothing so the view has to fall back.
    sealed class WallLessResolver : ITileMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype)
        {
            ArgumentNullException.ThrowIfNull(archetype);
            return archetype.Id == "wall" ? null : _inner.Resolve(archetype);
        }
    }
}
