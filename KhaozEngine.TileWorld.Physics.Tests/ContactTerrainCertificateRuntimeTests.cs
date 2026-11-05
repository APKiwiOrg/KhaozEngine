using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainCertificateRuntimeTests
{
    [Fact]
    public async Task ConcurrentPreparationPublishesImmutableCertificatesWithIndependentAttemptCorridors()
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var links = nav.Graph.Links.Where(link => nav.Terrain!.Route(link) == ContactDropRoute.Terrain).ToArray();
        Assert.NotEmpty(links);
        Assert.Equal(0, nav.Terrain!.CertificateCountForTests);
        await Task.WhenAll(Task.Run(nav.PrepareTerrainDescents), Task.Run(nav.PrepareTerrainDescents));
        Assert.Equal(links.Length, nav.Terrain.CertificateCountForTests);
        Assert.True(nav.Terrain.TryGet(links[0], out var certificate));
        var frame = scene.Context.NavigationFrame;
        var tuning = ContactTerrainScene.Tuning;
        var first = new ContactTerrainDropPolicy(certificate!, frame, tuning, 6);
        var second = new ContactTerrainDropPolicy(certificate!, frame, tuning, 6);
        var ordinary = MovementBoundary.Rectangle(0, -3, 5, 0);
        var a = (IMovementRouteBoundary)first.Corridor.Compose(ordinary, frame);
        var b = (IMovementRouteBoundary)second.Corridor.Compose(ordinary, frame);
        Vector3 upper = certificate!.From + Vector3.UnitY * (tuning.CapsuleHalfHeight + 0.25f);
        a.RecordSupportedCentre(upper);
        Vector3 lower = certificate.From + Vector3.UnitY * (tuning.CapsuleHalfHeight - 5.875f);
        Assert.Equal(MovementAvailability.Known, a.ProveRouteSegment(lower, Vector3.Zero, lower, out bool borrowed));
        Assert.Equal(MovementAvailability.Known, b.ProveRouteSegment(lower, Vector3.Zero, lower, out bool own));
        Assert.False(borrowed);
        Assert.True(own);
    }
}
