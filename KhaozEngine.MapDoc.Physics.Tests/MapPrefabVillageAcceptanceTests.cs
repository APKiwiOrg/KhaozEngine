using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapPrefabVillageAcceptanceTests
{
    [Fact]
    public void IndependentHeads_ResolveIdenticalEffectiveVillage()
    {
        var f = PrefabFixtures.Village();
        string text = MapDocumentFile.SaveText(f.Document);
        var client = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), PrefabFixtures.ReloadVillageAssets(f.Assets), PrefabPhysicsFixtures.Options());
        var server = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), PrefabFixtures.ReloadVillageAssets(f.Assets), PrefabPhysicsFixtures.Options());
        Assert.NotSame(client.Document, server.Document);
        Assert.NotSame(client.Document.EffectiveResources, server.Document.EffectiveResources);
        Assert.NotEmpty(client.Statics);
        Assert.Single(client.Water);
        Assert.Equal(client.BuildHash, server.BuildHash);
        Assert.Equal(client.Statics.Select(x => (x.OwnerId, x.Digest)), server.Statics.Select(x => (x.OwnerId, x.Digest)));
        Assert.Equal(client.Water.Select(x => x.Digest), server.Water.Select(x => x.Digest));
        Assert.Equal(client.Document.EffectiveResources!.AnnotationDigest, server.Document.EffectiveResources!.AnnotationDigest);
        Assert.Equal(client.Lights.ToArray(), server.Lights.ToArray());
        Assert.Equal(client.Markers.Select(m => (m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, string.Join("|", m.Tags), m.InstanceId)),
            server.Markers.Select(m => (m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, string.Join("|", m.Tags), m.InstanceId)));
        foreach (var p in PrefabFixtures.VillageProbes(client.Document.EffectiveResources))
        {
            var a = new MapSpaceMembership(client.Surfaces).Query(p);
            var b = new MapSpaceMembership(server.Surfaces).Query(p);
            Assert.Equal((a.Status, a.SpaceId), (b.Status, b.SpaceId));
            Assert.Equal(a.DomainKeys.ToArray(), b.DomainKeys.ToArray());
            Assert.Equal(a.ParentSpaceIds.ToArray(), b.ParentSpaceIds.ToArray());
            Assert.Equal(MapRoofPolicy.Evaluate(client.Document.EffectiveResources, client.Surfaces, p, MapRoofMode.Auto).Roofs.ToArray(),
                MapRoofPolicy.Evaluate(server.Document.EffectiveResources, server.Surfaces, p, MapRoofMode.Auto).Roofs.ToArray());
            var aw = new MapWaterQuery(client.Surfaces, client.Document.EffectiveResources.Water!).Sample(p);
            var bw = new MapWaterQuery(server.Surfaces, server.Document.EffectiveResources.Water!).Sample(p);
            Assert.Equal((aw.Status, aw.BodyId, aw.SpaceId, aw.LowerY, aw.UpperY, aw.UpperIsFreeSurface),
                (bw.Status, bw.BodyId, bw.SpaceId, bw.LowerY, bw.UpperY, bw.UpperIsFreeSurface));
        }
    }

    [Fact]
    public void MissingClosureFailsBothHeadsBeforeStaticInstall()
    {
        var f = PrefabFixtures.Village();
        string text = MapDocumentFile.SaveText(f.Document);
        foreach (string head in new[] { "client", "server" })
        {
            MapBuiltWorld? installed = null;
            var error = Assert.Throws<MapDocumentException>(() => installed = MapWorldBuilder.Build(
                MapDocumentFile.LoadText(text), PrefabFixtures.ReloadVillageAssets(f.Assets, "bath"), PrefabPhysicsFixtures.Options()));
            Assert.Contains("bath", error.Message);
            Assert.True(installed is null, head + " must refuse before static installation");
        }
    }

    [Fact]
    public void VillageDoorwayAndSmallTarget_RetainPhysicalClearanceAndReachEnvelope()
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Village());
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var relation = new MapPhysicalRelations(registration);
        Assert.Equal(MapPhysicalCertainty.Clear, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, false), MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, true), MoveTuning.Default).Certainty);
        var item = world.Placements.Single(p => p.PlacementId == "a/item");
        Assert.Null(item.Collider);
        Assert.Equal(1, item.Envelope.Bounds.MaxY - item.Envelope.Bounds.MinY, 6);
        Assert.Equal(1 - 0.2 * 0.8, item.Envelope.RaiseMetres, 6);
    }
}
