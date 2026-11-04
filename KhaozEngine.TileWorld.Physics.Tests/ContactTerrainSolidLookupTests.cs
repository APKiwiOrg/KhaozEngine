using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainSolidLookupTests
{
    // #1697 index review F2. Buried solids and roofs retain the exact old interval/obstruction verdict.
    [Theory]
    [InlineData(-1.5f, true)]
    [InlineData(0.6f, false)]
    [InlineData(2.5f, false)]
    [InlineData(4f, true)]
    public void CachedSolidIntervalKeepsOldVerdictsAndSparseDependencies(float height, bool expected)
    {
        using var scene = new ContactTerrainScene(0, extra: (world, builder) =>
        {
            var local = world.AddStatic(new BoxShape(new(0.3f, 0.5f, 0.3f)), Pose.At(new(2.375f, height, -1.375f)));
            builder.Add(local, 0, int.MaxValue, new("world", "local"), new(2.075f, height - 0.5f, -1.675f), new(2.675f, height + 0.5f, -1.075f));
            var far = world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(4.5f, 2, -2.5f)));
            builder.Add(far, 0, int.MaxValue, new("world", "far"), new(4.25f, 1.75f, -2.75f), new(4.75f, 2.25f, -2.25f));
        });
        var options = new PhysicsNavBakeOptions(0, -3, 5, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 2048);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var e = capture.DescentEvidence!;
        var oldReads = new ContactTerrainEvidenceReads(e); var actualReads = new ContactTerrainEvidenceReads(e);
        bool oldVerdict, actualVerdict;
        using (oldReads.Observe()) oldVerdict = ContactTerrainCorridorOracle.CertifyColumn(e, 9, 6, -6, 0.75,
            2d * ContactTerrainScene.Tuning.CapsuleHalfHeight, default);
        using (actualReads.Observe()) actualVerdict = ContactTerrainDropCorridor.CertifyColumn(e, 9, 6, -6, 0.75,
            2d * ContactTerrainScene.Tuning.CapsuleHalfHeight, default);
        Assert.Equal(expected, oldVerdict);
        Assert.Equal(oldVerdict, actualVerdict);
        var actualSubset = ContactTerrainEvidenceSubset.Build(e, actualReads, ContactTerrainScene.Tuning);
        // The old global scan observes unrelated metadata. Only the production receipt is retained now.
        var canonical = TerrainEvidenceUnion.Build(e.Options, capture.Columns, [actualSubset]);
        var loaded = TerrainEvidenceDecoder.Decode(TerrainEvidenceWriter.Encode(canonical, capture.Columns), e.Options, capture.Columns);
        Assert.Equal(expected, ContactTerrainDropCorridor.CertifyColumn(loaded, 9, 6, -6, 0.75,
            2d * ContactTerrainScene.Tuning.CapsuleHalfHeight, default));
        int[] farFaces = Enumerable.Range(0, e.FaceCount).Where(i => e.Face(i).Owner.LocalId == "far").ToArray();
        Assert.NotEmpty(farFaces);
        foreach (int face in farFaces)
        {
            Assert.Contains(face, oldReads.FaceRecords);
            Assert.DoesNotContain(face, actualReads.FaceRecords);
        }
    }
}
