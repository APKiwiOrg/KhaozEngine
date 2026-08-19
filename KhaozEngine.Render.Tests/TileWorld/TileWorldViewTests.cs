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
    static readonly Vector3 HouseFocus = new(11f, 0f, -10.5f);

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

    // Two regions of grass side by side in x, so a dirty rect near their shared border has a neighbour to reach.
    static TileWorldDocument TwoRegionGrass() => GrassRow(2);

    // A west-to-east run of grass regions on plane 0, every one of them meshing to a real ground mesh.
    static TileWorldDocument GrassRow(int regions)
    {
        var doc = new TileWorldDocument { Id = "tile-view-tests", DisplayName = "Tile view tests" };
        for (int rx = 0; rx < regions; rx++)
        {
            var region = new RegionCoord(rx, 0);
            doc.GetOrCreateRegion(region);
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                    doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0, TileRenderTestData.Grass);
        }
        return doc;
    }

    // The ground-mesh handle a region drew this frame, found by its world transform rather than by draw order,
    // because the view iterates a dictionary and promises no order.
    static int HandleOf(RecordingTileWorldScene scene, TileWorldDocument doc, RegionCoord region)
    {
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        return scene.Drawn.Single(d => d.World == world).Handle.Index;
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
    public void MarkDirty_by_world_rect_reaches_into_the_neighbouring_region()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TwoRegionGrass();
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(west);
        view.LoadRegion(east);

        view.Draw(Vector3.Zero);
        int west0 = HandleOf(scene, doc, west), east0 = HandleOf(scene, doc, east);
        scene.ClearFrame();

        // One tile INSIDE the east region. The corner heights it moves are shared with the west region, and the
        // normal and colour rules read a corner further still, so the west mesh has to be rebuilt too.
        view.MarkDirty(new TileRect(TileRegion.Size + 1, 10, 1, 1), 0);
        view.Draw(Vector3.Zero);
        int west1 = HandleOf(scene, doc, west), east1 = HandleOf(scene, doc, east);
        Assert.NotEqual(west0, west1);
        Assert.NotEqual(east0, east1);
        scene.ClearFrame();

        // Well inside the west region: nothing the east mesh reads changed, so it must not be rebuilt.
        view.MarkDirty(new TileRect(30, 30, 1, 1), 0);
        view.Draw(Vector3.Zero);
        Assert.NotEqual(west1, HandleOf(scene, doc, west));
        Assert.Equal(east1, HandleOf(scene, doc, east));
    }

    [Fact]
    public void MaxRebuildsPerFlush_drains_the_queue_oldest_first()
    {
        var scene = new RecordingTileWorldScene();
        // Three regions, so all three marks below are real mesh-producing rebuilds rather than empty planes,
        // which the budget does not charge for.
        TileWorldDocument doc = GrassRow(3);
        var west = new RegionCoord(0, 0);
        var middle = new RegionCoord(1, 0);
        var east = new RegionCoord(2, 0);
        using TileWorldView view = View(scene, doc, options: new TileWorldViewOptions { MaxRebuildsPerFlush = 1 });
        view.LoadRegion(west);
        view.LoadRegion(middle);
        view.LoadRegion(east);

        view.Draw(Vector3.Zero);
        int west0 = HandleOf(scene, doc, west);
        int middle0 = HandleOf(scene, doc, middle);
        int east0 = HandleOf(scene, doc, east);
        scene.ClearFrame();

        view.MarkDirty(west, 0);
        view.MarkDirty(middle, 0);
        view.MarkDirty(east, 0);
        Assert.Equal(3, view.PendingRebuilds);

        // One a frame, oldest first: the other two marks are queued behind the west one and untouched.
        view.Draw(Vector3.Zero);
        Assert.Equal(2, view.PendingRebuilds);
        int west1 = HandleOf(scene, doc, west);
        Assert.NotEqual(west0, west1);
        Assert.Equal(middle0, HandleOf(scene, doc, middle));
        Assert.Equal(east0, HandleOf(scene, doc, east));
        scene.ClearFrame();

        view.Draw(Vector3.Zero);
        Assert.Equal(1, view.PendingRebuilds);
        Assert.NotEqual(middle0, HandleOf(scene, doc, middle));
        Assert.Equal(east0, HandleOf(scene, doc, east));
        Assert.Equal(west1, HandleOf(scene, doc, west));
        scene.ClearFrame();

        view.Draw(Vector3.Zero);
        Assert.Equal(0, view.PendingRebuilds);
        Assert.NotEqual(east0, HandleOf(scene, doc, east));
        Assert.Equal(west1, HandleOf(scene, doc, west));
    }

    [Fact]
    public void An_empty_region_plane_does_not_spend_the_rebuild_budget()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TwoRegionGrass();
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        using TileWorldView view = View(scene, doc, options: new TileWorldViewOptions { MaxRebuildsPerFlush = 1 });
        view.LoadRegion(west);
        view.LoadRegion(east);

        view.Draw(Vector3.Zero);
        int west0 = HandleOf(scene, doc, west), east0 = HandleOf(scene, doc, east);
        scene.ClearFrame();

        // Planes 1 to 3 of the west region hold no drawable tile, so they mesh to null. A budget of one still
        // reaches the real rebuild behind them in the same flush, which is the whole point: a four-plane document
        // with one authored plane must not spend three flushes on nothing.
        view.MarkDirty(west, 1);
        view.MarkDirty(west, 2);
        view.MarkDirty(west, 3);
        view.MarkDirty(east, 0);
        Assert.Equal(4, view.PendingRebuilds);

        view.Draw(Vector3.Zero);
        Assert.Equal(0, view.PendingRebuilds);
        Assert.NotEqual(east0, HandleOf(scene, doc, east));
        Assert.Equal(west0, HandleOf(scene, doc, west));
    }

    [Fact]
    public void Flush_rebuilds_the_dirty_planes_props_not_just_its_mesh()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);

        view.Draw(HouseFocus);
        Assert.DoesNotContain(scene.PropDraws, r => r.Placements.Any(p => p.Id == "tree"));
        int before = view.LastDrawnProps;
        scene.ClearFrame();

        // An object added after the load only reaches the scene if the flush rebuilt the placements as well.
        doc.AddObject("tree", TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ - 2, 0, 0);
        view.MarkDirty(TileRenderTestData.Region, 0);
        view.Draw(HouseFocus);

        Assert.Contains(scene.PropDraws, r => r.Placements.Any(p => p.Id == "tree"));
        Assert.Equal(before + 1, view.LastDrawnProps);
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
    public void Roofs_on_the_observers_own_plane_stay_drawn_indoors()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        // A roof on the GROUND plane, and the roof plane flagged indoors too, so the rule can be judged from an
        // observer standing on either storey.
        doc.AddObject("roof_flat", TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 0, 0);
        for (int z = TileRenderTestData.HouseMinZ; z <= TileRenderTestData.HouseMaxZ; z++)
            for (int x = TileRenderTestData.HouseMinX; x <= TileRenderTestData.HouseMaxX; x++)
                doc.SetSettings(x, z, TileRenderTestData.RoofPlane, TileSettings.Indoors);

        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);

        // Indoors on plane 0: the roof ON plane 0 is not above the observer, so it stays.
        view.Observer = new TileCoord(TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 0);
        Assert.True(view.ObserverIndoors);
        view.Draw(HouseFocus);
        Assert.Equal(HouseGroundProps + 1, view.LastDrawnProps);
        scene.ClearFrame();

        // Indoors on plane 1: plane 1's own roofs stay too, so an observer upstairs is not standing in the open.
        view.Observer = new TileCoord(TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ,
                                      TileRenderTestData.RoofPlane);
        Assert.True(view.ObserverIndoors);
        view.Draw(HouseFocus);
        Assert.Equal(HouseGroundProps + 1 + HouseRoofProps, view.LastDrawnProps);
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

    [Fact]
    public void LoadRegion_frees_the_planes_it_uploaded_when_a_later_plane_throws()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoPlaneHouse());
        // The archetype sets the constructor uploaded belong to the VIEW, not to this call, so they are the
        // baseline the rollback has to return to rather than anything it may free.
        int beforeLoad = scene.AliveMeshCount;

        // Plane 0 uploads and the roof plane refuses, which is the shape a device out of memory takes part way
        // through a region. The handle from plane 0 is reachable from nowhere once the exception is out, so
        // freeing it is this call's last chance.
        scene.ThrowOnMeshLoad = 2;
        Assert.Throws<InvalidOperationException>(() => view.LoadRegion(TileRenderTestData.Region));

        Assert.Equal(beforeLoad, scene.AliveMeshCount);
        Assert.Single(scene.MeshLoads);
        Assert.Single(scene.MeshUnloads);
        Assert.Equal(scene.MeshLoads[0].Index, scene.MeshUnloads[0].Index);
        Assert.Equal(0, view.LoadedRegionCount);
        Assert.Empty(view.LoadedRegions);

        // And the region is genuinely loadable afterwards: the rollback left no stale entry behind, and the fake
        // throws on a double free, so a rollback that had freed a handle twice would fail here.
        scene.ThrowOnMeshLoad = 0;
        view.LoadRegion(TileRenderTestData.Region);
        Assert.Equal(1, view.LoadedRegionCount);
        Assert.Equal(3, scene.MeshLoads.Count);
    }

    [Fact]
    public void Constructor_frees_the_archetype_sets_it_uploaded_when_a_later_one_throws()
    {
        var scene = new RecordingTileWorldScene();
        // A constructor that throws never produces the object whose Dispose would free these, so the sets the
        // first two archetypes uploaded come back here or they are stranded for the process's life.
        scene.ThrowOnPropMeshLoad = 3;

        Assert.Throws<InvalidOperationException>(() => new TileWorldView(
            scene, TileRenderTestData.HouseWorld(), TileRenderTestData.Catalogs, new GreyboxMeshResolver()));

        Assert.Equal(2, scene.PropMeshLoads.Count);
        Assert.Equal(0, scene.AliveMeshCount);
        Assert.Equal(scene.PropMeshLoads.Sum(parts => parts.Count), scene.MeshUnloads.Count);
    }

    [Fact]
    public void The_view_uploads_one_ground_material_and_every_mesh_goes_up_bound_to_it()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoPlaneHouse());
        view.LoadRegion(TileRenderTestData.Region);

        // One upload for the whole view rather than one per region-plane: the set is 64 layers of texture and the
        // upload is not a mid-frame call.
        Assert.Single(scene.MaterialLoads);
        Assert.Same(view.GroundMaterials, scene.MaterialLoads[0]);
        Assert.Equal(2, scene.MeshLoads.Count);
        Assert.Equal(scene.MeshLoads.Count, scene.MeshMaterials.Count);
        Assert.All(scene.MeshMaterials, handle => Assert.Equal(GroundMaterial(0), handle));
    }

    [Fact]
    public void The_default_material_set_is_built_from_the_catalogs_and_meshes_are_built_against_it()
    {
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions();
        using TileWorldView view = View(scene, TwoPlaneHouse(), options: options);

        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6 }, view.GroundMaterials.MaterialIds.ToArray());
        // The vertices name slots, and a slot only means anything against the set the mesh is drawn with, so the
        // mesher has to be pointed at that set and not at the identity stand-in it starts on.
        Assert.Same(view.GroundMaterials, options.Mesher.Slots);
    }

    [Fact]
    public void A_caller_provided_material_set_is_uploaded_as_it_stands()
    {
        var scene = new RecordingTileWorldScene();
        // A set that carries none of the greybox ids, so a view that quietly rebuilt one from the catalogs would
        // come back with six materials instead of this one.
        var set = new TileGroundMaterialSet(1, 1, new ushort[] { 42 }, new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        });
        var options = new TileWorldViewOptions { GroundMaterials = set };

        using TileWorldView view = View(scene, TwoPlaneHouse(), options: options);

        Assert.Same(set, scene.MaterialLoads.Single());
        Assert.Same(set, view.GroundMaterials);
        Assert.Same(set, options.Mesher.Slots);
    }

    [Fact]
    public void Dispose_frees_the_ground_material()
    {
        var scene = new RecordingTileWorldScene();
        var view = View(scene, TwoPlaneHouse());
        view.LoadRegion(TileRenderTestData.Region);
        Assert.Empty(scene.MaterialUnloads);

        view.Dispose();
        Assert.Equal(GroundMaterial(0), scene.MaterialUnloads.Single());

        // The fake throws on a double free, so this is the assertion that a second dispose stays a no-op.
        view.Dispose();
        Assert.Single(scene.MaterialUnloads);
    }

    [Fact]
    public void Constructor_frees_the_ground_material_when_an_archetype_upload_throws()
    {
        var scene = new RecordingTileWorldScene();
        // The material goes up before the archetypes, so it is on the device with nothing holding it once the
        // constructor throws and no Dispose is ever reached.
        scene.ThrowOnPropMeshLoad = 3;

        Assert.Throws<InvalidOperationException>(() => new TileWorldView(
            scene, TileRenderTestData.HouseWorld(), TileRenderTestData.Catalogs, new GreyboxMeshResolver()));

        Assert.Single(scene.MaterialLoads);
        Assert.Equal(GroundMaterial(0), scene.MaterialUnloads.Single());
    }

    // The Nth material handle the fake hands out, 0-based, which is what a test compares a recorded one against.
    static Scene3D.TileGroundMaterialHandle GroundMaterial(int index) => new(index);

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
