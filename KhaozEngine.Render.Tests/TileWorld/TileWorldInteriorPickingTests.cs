using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldInteriorPickingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoorwayPicksFloorOrBackWallBeforeCutTree(bool cut)
    {
        using var rig = new Rig(cut);
        var hits = new List<TileObjectHit>();
        var doorway = new Vector3(11.5f, 1f, -9.9f);
        Assert.True(rig.View.PickObjects(0, doorway, -Vector3.UnitZ, 6f, rig.Bounds, hits) > 0);
        TileObject wall = rig.Doc.AllObjects().Single(o => o.ArchetypeId == "wall" &&
            o.X == 11 && o.Z == 11 && o.Rotation == 1);
        Assert.Equal(cut ? wall.Id : rig.Tree.Id, hits[0].ObjectId);
        Assert.Contains(hits, h => h.ObjectId == rig.Tree.Id);

        var inside = new Vector3(11.5f, 1f, -10.5f);
        var down = new Vector3(0f, -1f, -0.2f);
        rig.View.PickObjects(0, inside, down, 2f, rig.Bounds, hits);
        if (cut) Assert.DoesNotContain(hits, h => h.ObjectId == rig.Tree.Id);
        else Assert.Equal(rig.Tree.Id, hits[0].ObjectId);
        TileHit surface = Assert.IsType<TileHit>(rig.View.PickSurface(0, inside, down, 2f));
        Assert.Equal(cut ? 0f : 0.75f, surface.Point.Y, 4);
    }

    [Fact]
    public void UnoptedWalkSurfacesRemainPickableInsideTheAppliedField()
    {
        using var rig = new Rig(true);
        rig.Catalogs.Archetype("wall")!.WalkSurfaces = [new TileWalkSurface { Height = 0.5f }];
        rig.Doc.AddObject("wall", 11, 10, 0, 0);
        Assert.True(rig.View.AppliedInteriorCut!.Contains(new Vector3(11.5f, 0.5f, -10.5f)));
        TileHit hit = Assert.IsType<TileHit>(rig.View.PickSurface(0,
            new Vector3(11.5f, 1f, -10.5f), -Vector3.UnitY, 2f));
        Assert.Equal(0.5f, hit.Point.Y);
    }

    [Fact]
    public void PicksWaitForThePublishedField()
    {
        using var rig = new Rig(true, complete: false);
        var hits = new List<TileObjectHit>();
        var origin = new Vector3(11.5f, 1f, -10.5f);
        Assert.Null(rig.View.AppliedInteriorCut);
        Assert.Equal(1, rig.View.PickObjects(0, origin, -Vector3.UnitY, 1f, rig.Bounds, hits));
        Assert.Equal(rig.Tree.Id, hits[0].ObjectId);
        Assert.Equal(0.75f, rig.View.PickSurface(0, origin, -Vector3.UnitY, 2f)!.Value.Point.Y);
        rig.Complete();
        Assert.NotNull(rig.View.AppliedInteriorCut);
        Assert.Equal(0, rig.View.PickObjects(0, origin, -Vector3.UnitY, 1f, rig.Bounds, hits));
        Assert.Equal(0f, rig.View.PickSurface(0, origin, -Vector3.UnitY, 2f)!.Value.Point.Y);
    }

    internal sealed class Rig : IDisposable
    {
        readonly ManualTileBuildDispatcher _dispatcher = new();
        readonly TileObjectBoundsCache _bounds;
        public TileWorldDocument Doc { get; } = TileRenderTestData.HouseWorld();
        public TileWorldCatalogs Catalogs { get; } = TileRenderTestData.Catalogs;
        public TileWorldView View { get; }
        public TileObject Tree { get; }

        public Rig(bool cut, bool complete = true)
        {
            Tree = Doc.AddObject("tree", 11, 10, 0, 0);
            Catalogs.Archetype("tree")!.WalkSurfaces = [new TileWalkSurface { Height = 0.75f }];
            var resolver = new GreyboxMeshResolver();
            _bounds = new TileObjectBoundsCache(resolver);
            var options = new TileWorldViewOptions
            {
                PropLayers = [new TilePropLayerDefinition
                {
                    Id = "trees", ArchetypeIds = new HashSet<string> { "tree" },
                    DrawRadius = 192f, LodDistance = 64f, CutInsideInteriors = cut,
                }],
            };
            View = new TileWorldView(new RecordingTileWorldScene(), Doc, Catalogs, resolver,
                options, null, _dispatcher);
            View.InteriorMeshes = new InteriorTestMeshes();
            View.LoadRegion(default);
            View.SettleForCapture(default);
            InteriorTestWorker.Run(_dispatcher);
            if (complete) Complete();
        }

        public bool Bounds(TileObjectArchetype archetype, out Vector3 min, out Vector3 max)
        {
            if (archetype.Id == "tree")
            {
                min = new Vector3(-0.4f, 0f, -2f);
                max = new Vector3(0.4f, 5f, 0.4f);
                return true;
            }
            // The open doorway's posts lie beside these rays. Its combined box would cover the opening.
            if (archetype.Id != "doorway") return _bounds.TryGetBounds(archetype, out min, out max);
            min = max = default;
            return false;
        }

        public void Complete()
        {
            View.Draw(Vector3.Zero);
            InteriorTestWorker.Run(_dispatcher);
            View.Draw(Vector3.Zero);
        }

        public void Dispose()
        {
            InteriorTestWorker.Run(_dispatcher);
            View.Dispose();
        }
    }
}
