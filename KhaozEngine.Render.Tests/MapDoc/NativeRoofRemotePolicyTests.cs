using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;
using KhaozEngine.Tests.MapDoc.Scene;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofRemotePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedMultiPatchRoom_RemoteObserverHidesRoofsFromEitherWindow(bool grouped)
    {
        using var fixture = Fixture(grouped, false); using var documents = NativeSceneFixtures.OpenTiled(fixture);
        var source = MapSceneSource.Capture(documents, fixture.Assets, new(MaxPreparedBytes: 536870912)).Source!;
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "roof" }, [], 0, 1000, 30, 10, 0, true)], 1000);
        string? digest = null;
        foreach (float x in new[] { 8f, 16f })
        {
            var scene = source.Acquire(NativeRoofHlodPolicyTests.Window(x)).Scene!;
            var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
            var cell = Assert.Single(MapPlacementCellInventory.Capture(scene, routing).Cells);
            digest ??= cell.Scene.Scope.Surfaces.Digest;
            Assert.Equal(digest, cell.Scene.Scope.Surfaces.Digest);
            Assert.Equal(4, cell.Scene.SurfaceView.Witness.Present.Count(p => p.Key.SurfaceId == "ground"));
            var evaluation = cell.RoofEvaluation!;
            evaluation.Evaluate(new(WorldFrame.Origin, new(220, 1, -.5f)), MapRoofMode.Auto);
            Assert.Equal(MapMembershipStatus.Resolved, evaluation.Status);
            Assert.All(evaluation.Roofs, roof => Assert.False(roof.ColourVisible));
        }
    }

    [Fact]
    public void MissingRemoteParentSupport_RefusesBeforeAnyRoofProductPublishes()
    {
        using var fixture = Fixture(false, true); using var documents = NativeSceneFixtures.OpenTiled(fixture);
        var source = MapSceneSource.Capture(documents, fixture.Assets, new(MaxPreparedBytes: 536870912)).Source!;
        var scene = source.Acquire(NativeRoofHlodPolicyTests.Window(8)).Scene!;
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "roof" }, [], 0, 1000, 30, 10, 0, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        var error = Assert.Throws<MapSceneSourceException>(() => MapPlacementCellInventory.Capture(scene, routing));
        Assert.Equal(MapSceneStatus.Invalid, error.Status);
    }

    static NativeSceneFixture Fixture(bool grouped, bool missingSupport)
    {
        using var fixture = NativePresentationFixtures.Create(); var doc = fixture.Document;
        doc.TerrainOverrides = new(.03125f);
        doc.Placements = doc.Placements.Where(p => p.AssetId == "roof").ToList();
        doc.Placements[0].X = 8; doc.Placements[1].X = 16;
        doc.Bounds = new() { MinX = 0, MinZ = -64, MaxX = 320, MaxZ = 64 }; doc.PlayableBounds = doc.Bounds;
        doc.Surfaces = new(); var anchor = new MapPatchKey("ground", 0, 0); var parent = new MapRecordRef("outside", anchor);
        for (int slot = 0; slot < (missingSupport ? 3 : 4); slot++)
        {
            var patch = new MapSurfacePatch { Key = new("ground", slot, 0), Width = 64, Depth = 1,
                Heights = new int[130], Cells = new MapSurfaceCell[64], Presence = [ulong.MaxValue] };
            var owner = slot == 0 ? parent : new MapRecordRef("outside-" + slot, patch.Key);
            if (slot != 0) patch.Records.Add(new MapSpaceDoc(owner.Id, MapSpaceKind.Exterior, null, parent, [], [], [], []));
            patch.Records.Add(new MapSpaceFootprint("parent-" + slot, owner, patch.Key, Enumerable.Range(0, 64).ToArray(),
                new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
            if (slot == 0)
            {
                patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
                patch.Records.Add(new MapSpaceDoc("long-room", MapSpaceKind.Room, parent, null, [], [], [], []));
                patch.Records.Add(new MapPrismDomain("long-domain", 1, new("long-room", anchor), parent, "", "floor",
                    new(MapLatticeFrame.ImportedMetreCentimetre, [new(0, 0, 256, 1)], []), 0, 3));
            }
            doc.Surfaces.Patches.Add(patch.Key, patch);
        }
        var surface = new MapSurfaceRef("ground", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        doc.Surfaces.Refs.Add(surface with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            doc.Surfaces.Patches.Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))) });
        MapPrefabReference Ref(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);
        var roofs = doc.Placements.Select(p => Ref(MapPrefabChildKind.Placement, p.Id)).ToArray();
        doc.BuildingIntent = new(1, roofs.Select(r => new MapRoofLinkDoc(r.Id + "-link", r,
            grouped ? [] : [Ref(MapPrefabChildKind.Record, "long-room")], grouped ? [Ref(MapPrefabChildKind.RoofGroup, "group")] : [], 3)).ToArray(),
            grouped ? [new("group", [Ref(MapPrefabChildKind.Record, "long-room")])] : [], [], [])
        { Roofs = roofs, Storeys = [new(1, "floor", "Floor", null)] };
        return new(doc, fixture.Closure);
    }
}
