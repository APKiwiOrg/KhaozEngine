using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainVertexFanTests
{
    [Fact]
    public void SixFaceGroundVertexBindsTheAnalyticFieldsCanonicalTriangle()
    {
        // #1709: the first raw ray meets six triangles, including point-only neighbours of face 7810.
        var document = TileWorldMovementNavigationTests.Drawn(0, 0, 64, 64);
        document.TileSize = 0.0625f;
        using var scene = new ContactTerrainScene(0, document: document);
        var options = new PhysicsNavBakeOptions(0, -4, 4, 0, 0.25f, 2, 4,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 32768);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var evidence = capture.DescentEvidence!;
        Assert.Equal(256, evidence.RawCount);
        Assert.True(evidence.TryRaw(0, 0, BitConverter.SingleToInt32Bits(0), out var binding));
        Assert.Equal(7940, binding.Face);
        var selected = evidence.Face(binding.Face);
        Assert.Equal(8589934654, selected.Key.Address); // Tile (2, 62).
        Assert.Equal(0, selected.Key.Part);
        Assert.Equal(MovementAvailability.Known, scene.Source.Describe(new(0.125f, -3.875f), out var named));
        Assert.Equal(named.Key, selected.Key);
        Assert.False(scene.Source.AreAdjacent(evidence.Face(7810).Key, evidence.Face(7938).Key));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("disconnected")]
    [InlineData("coincident-owner")]
    [InlineData("gap")]
    [InlineData("missing-canonical")]
    [InlineData("unresolved")]
    public void MalformedOrUnresolvedIncidentFansRemainAmbiguous(string fault)
    {
        // #1709: admitting an incident component must preserve each independent refusal.
        using (var scene = new ContactVertexFanScene(fault))
        {
            var error = Assert.Throws<PhysicsNavDescentEvidenceException>(() =>
                PhysicsNavBake.CaptureDescent(scene.Context, ContactVertexFanScene.Options, _ => 0));
            Assert.Equal(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous, error.Reason);
        }
        using var control = new ContactVertexFanScene();
        using var capture = PhysicsNavBake.CaptureDescent(control.Context, ContactVertexFanScene.Options, _ => 0);
        var raw = Assert.Single(capture.DescentEvidence!.RawBindings.ToArray());
        Assert.Equal(4, capture.DescentEvidence.Face(raw.Face).Key.Part);
    }
}
