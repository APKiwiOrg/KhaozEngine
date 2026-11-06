using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainVertexFanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixFaceGroundVertexBindsTheAnalyticFieldsCanonicalTriangle(bool shiftedRayAxis)
    {
        // #1709: the first raw ray meets six triangles, including point-only neighbours of face 7810.
        var document = TileWorldMovementNavigationTests.Drawn(0, 0, 64, 64);
        document.TileSize = 0.0625f;
        using var scene = new ContactTerrainScene(0, document: document);
        var options = new PhysicsNavBakeOptions(0, -4, 4, 0, 0.25f, 2, 4,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 32768);
        if (shiftedRayAxis)
        {
            // #1709 F1: requested Z = -3.875 + 2^-20 becomes -131.875 locally, then -3.875 absolutely.
            scene.World.Rebase(new(0, 0, 128));
            options = options with { MinZ = -4 + 1f / 1048576, MaxX = 0.25f, MaxZ = -3.75f + 1f / 1048576 };
        }
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var evidence = capture.DescentEvidence!;
        Assert.Equal(shiftedRayAxis ? 1 : 256, evidence.RawCount);
        Assert.True(evidence.TryRaw(0, 0, BitConverter.SingleToInt32Bits(0), out var binding));
        // The one-cell capture retains fewer mesh chunks, so only the full capture has ordinal 7940.
        if (!shiftedRayAxis) Assert.Equal(7940, binding.Face);
        var selected = evidence.Face(binding.Face);
        Assert.Equal(8589934654, selected.Key.Address); // Tile (2, 62).
        Assert.Equal(0, selected.Key.Part);
        Assert.Equal(MovementAvailability.Known, scene.Source.Describe(new(0.125f, -3.875f), out var named));
        Assert.Equal(named.Key, selected.Key);
        if (!shiftedRayAxis) Assert.False(scene.Source.AreAdjacent(evidence.Face(7810).Key, evidence.Face(7938).Key));
    }

    [Theory]
    [InlineData("duplicate", false)]
    [InlineData("overlap", false)]
    [InlineData("disconnected", false)]
    [InlineData("coincident-owner", false)]
    [InlineData("gap", false)]
    [InlineData("missing-canonical", false)]
    [InlineData("unresolved", false)]
    [InlineData("duplicate", true)]
    [InlineData("gap", true)]
    public void MalformedOrUnresolvedIncidentFansRemainAmbiguous(string fault, bool shiftedRayAxis)
    {
        // #1709: admitting an incident component must preserve each independent refusal.
        var origin = shiftedRayAxis ? new Vector3(0, 0, 128) : default;
        var options = ContactVertexFanScene.Options;
        if (shiftedRayAxis)
            options = options with { MinZ = -4 + 1f / 1048576, MaxZ = -3.75f + 1f / 1048576 };
        using (var scene = new ContactVertexFanScene(fault, origin))
        {
            var error = Assert.Throws<PhysicsNavDescentEvidenceException>(() =>
                PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0));
            Assert.Equal(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous, error.Reason);
        }
        using var control = new ContactVertexFanScene(origin: origin);
        using var capture = PhysicsNavBake.CaptureDescent(control.Context, options, _ => 0);
        var raw = Assert.Single(capture.DescentEvidence!.RawBindings.ToArray());
        Assert.Equal(4, capture.DescentEvidence.Face(raw.Face).Key.Part);
    }
}
