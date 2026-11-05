using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainDescentCaptureTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("void")]
    [InlineData("forbidden-roof")]
    [InlineData("off-grid")]
    public void CorridorRefusesOneChangedCoverageOrFirstObstruction(string fault)
    {
        Assert.True(Check(false));
        Assert.False(Check(true));
        bool Check(bool changed)
        {
            var doc = ContactTerrainScene.Document(4);
            if (changed && fault == "void") doc.SetSettings(1, 1, 0, TileSettings.NoDraw);
            using var scene = new ContactTerrainScene(4, document: doc, extra: (world, builder) =>
            {
                if (!(changed && fault == "forbidden-roof")) return;
                var handle = world.AddStatic(new BoxShape(new(0.3f, 0.1f, 0.5f)), Pose.At(new(2.6f, 1.9f, -1.375f)));
                builder.Add(handle, 0, int.MaxValue, new("world", "roof"), new(2.3f, 1.8f, -1.875f), new(2.9f, 2, -0.875f));
            });
            var options = new PhysicsNavBakeOptions(changed && fault == "off-grid" ? 0.25f : 0, -3, 4, 0, 0.25f, 10, 14,
                ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 2048);
            uint Classify(Vector3 p) => p.X >= 2.3f && p.X <= 2.9f && p.Y > 1 && p.Y < 3 ? 1u : 0u;
            using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, Classify);
            var evidence = capture.DescentEvidence!;
            if (changed && fault == "missing") evidence = evidence.MissingColumnForTests(6, 6);
            var footprint = new NavAreaFootprint(capture.Columns, options, ContactTerrainScene.Tuning, new(0, 1));
            return new ContactTerrainDropCorridor(evidence, footprint, ContactTerrainScene.Tuning, scene.From, scene.To, 6).Certify();
        }
    }
    [Fact]
    public void CoincidentDistinctOwnersCannotBindOneRawSurface()
    {
        var options = new PhysicsNavBakeOptions(0, -3, 4, 0, 0.25f, 10, 14, ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 2048);
        using (var control = new ContactTerrainScene(4))
        using (var captured = PhysicsNavBake.CaptureDescent(control.Context, options, _ => 0))
            Assert.True(captured.DescentEvidence!.RawCount > 0);
        using var scene = new ContactTerrainScene(4, extra: (world, builder) =>
        {
            var handle = world.AddStatic(new BoxShape(new(0.5f, 0.1f, 0.5f)), Pose.At(new(2.5f, -0.1f, -1.5f)));
            builder.Add(handle, 0, int.MaxValue, new("world", "coincident-deck"), new(2, -0.2f, -2), new(3, 0, -1));
        });
        var error = Assert.Throws<PhysicsNavDescentEvidenceException>(() => PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0));
        Assert.Equal(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous, error.Reason);
    }

}
