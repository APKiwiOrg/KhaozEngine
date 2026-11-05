using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactTerrainBandStorageTests
{
    // #1697 F3. Index payload is bounded by occupied band cells, not world cells times components.
    [Fact]
    public void DisconnectedSmallBandsRetainOnlyTheirLocalCells()
    {
        var evidence = Evidence(64, 1, 4096, 4);
        var index = new ContactTerrainBandIndex(evidence, Enumerable.Range(0, 4).Select(i => new[] { i }), Tuning);
        // Each one-metre triangle and its unchanged 0.3 m dilation fits within three by three cells.
        Assert.InRange(index.Bytes, 1, 4 * 9 * 36L);
    }

    [Fact]
    public void AggregateBandStorageRefusesBeforeExceedingTheCaptureBudget()
    {
        var evidence = Evidence(8, 0.25f, 64, 8);
        var refusal = Assert.Throws<PhysicsNavDescentEvidenceException>(() =>
            new ContactTerrainBandIndex(evidence, Enumerable.Range(0, 8).Select(i => new[] { i }), Tuning));
        Assert.Equal(PhysicsNavDescentEvidenceReason.CapacityExceeded, refusal.Reason);
        Assert.Equal(PhysicsEvidenceStatus.CapacityExceeded, refusal.Status);
    }

    static MoveTuning Tuning => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f);
    static PhysicsNavDescentEvidence Evidence(int width, float cellSize, int budget, int count)
    {
        var faces = Enumerable.Range(0, count).Select(i =>
        {
            double x = cellSize == 1 ? 2 + i * 4 : 0.6 + i * 0.05;
            double size = cellSize == 1 ? 1 : 0.02;
            return new DescentFace(new("authored", i, 0), new("world", "terrain"), true,
                PhysicsEvidenceKind.Triangle, [new(x, 4, 0.6), new(x + size, 0, 0.6), new(x, 4, 0.6 + size)], default, 0, 0);
        }).ToArray();
        var options = new PhysicsNavBakeOptions(0, 0, width * cellSize, width * cellSize, cellSize, 10, 14, MathF.PI / 4, width * width, budget);
        return new(options, width, width, faces, [], [], [], [], faces.Select(_ => Array.Empty<int>()).ToArray());
    }
}
