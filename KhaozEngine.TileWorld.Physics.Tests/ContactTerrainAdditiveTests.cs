using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainAdditiveTests
{
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void TerrainOptInKeepsEverySharpDirectionOnTheRimByTheSharpRoute(float height)
    {
        // #1697. A box brink beside the analytic rim exercises both policies in one capture.
        var document = ContactTerrainScene.Document(height, 0.25f,
            x => x <= 1 ? height : x >= 2.25f ? 0 : height * (2.25f - x) / 1.25f,
            widthMetres: 3, depthMetres: 2);
        using var scene = new ContactTerrainScene(height, document: document, extra: (world, builder) =>
        {
            Vector3 min = new(3.0625f, -0.1f, -3), max = new(5, 4, 0);
            var handle = world.AddStatic(new BoxShape((max - min) * 0.5f), Pose.At((min + max) * 0.5f));
            builder.Add(handle, 0, int.MaxValue, new("world", "sharp-brink"), min, max);
        });
        // Avoid lattice-vertex raw ties while retaining steep faces in the lower anchor's projected columns.
        var options = ContactTerrainOptInTests.Options with
        {
            CaptureTerrainDescentEvidence = true,
            CellSize = 0.5f,
            MinX = 0.0625f,
            MaxX = 5.0625f,
            MinZ = -2.9375f,
            MaxZ = 0.0625f
        };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact,
            CapsuleRadius = 0.2f,
            CapsuleHalfHeight = 0.75f,
            StepHeight = 0.4f,
            MaxSlopeRadians = options.MaxSlopeRadians,
            WalkSpeed = 3,
            RunSpeed = 5,
            TractionHysteresisRadians = 0,
            Water = new(WaterTraversalMode.DryOnly)
        };
        var sharp = capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var terrain = capture.BuildProfile(tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true });
        var directions = sharp.Graph.Links.Where(link => link.Kind == NavLinkKind.Drop).ToArray();
        Assert.NotEmpty(directions);
        Assert.Contains(directions, link => ContactTerrainDropCertificate.HasSteep(capture.DescentEvidence!, tuning,
            ContactDropProof.Feet(sharp.Space.Layers[link.FromLayer], link.FromX, link.FromZ),
            ContactDropProof.Feet(sharp.Space.Layers[link.ToLayer], link.ToX, link.ToZ)));
        foreach (var link in directions)
        {
            Assert.Contains(link, terrain.Graph.Links);
            Assert.Equal(ContactDropRoute.Sharp, terrain.Terrain!.Route(link));
            Assert.False(terrain.Terrain.TryGet(link, out _));
        }
    }
}
