using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class DescentEvidenceLookupTests
{
    // #1697. Preserve exact coordinate/height-bit identity, including the old ambiguous out result.
    [Fact]
    public void RawLookupMatchesTheOldScanForUnorderedRowsAndAmbiguity()
    {
        DescentRawBinding[] rows = [new(1, 2, 0, 0, 3), new(4, 5, 0, 42, 8), new(1, 2, 1, int.MinValue, 4),
            new(4, 5, 1, 42, 9), new(4, 5, 2, 42, 10), new(-1, 2, 0, 0, 7), new(1, 2, 2, 17, 6)];
        var lookup = new DescentEvidenceLookup([], rows, new());
        var queries = new[] { (1, 2, 0), (1, 2, int.MinValue), (4, 5, 42), (-1, 2, 0), (1, 2, 17), (1, 3, 0) };
        Assert.NotEmpty(queries);
        foreach (var (x, z, bits) in queries)
        {
            DescentRawBinding expected = default; bool found = false, ambiguous = false;
            foreach (var row in rows)
            {
                if (row.X != x || row.Z != z || row.HeightBits != bits) continue;
                if (found) { ambiguous = true; break; }
                expected = row; found = true;
            }
            Assert.Equal(found && !ambiguous, lookup.TryRaw(x, z, bits, out var actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SolidLookupReturnsOnlyTheOriginalMatchingGroupInFaceOrder()
    {
        var faces = new[] { Face("one", 7, 0, height: 2), Face("one", 8, 0, height: -100), Face("one", 7, 1, PhysicsEvidenceKind.Triangle, 1000),
            Face("two", 7, 0, height: -200), Face("one", 7, 2, height: -4) };
        var lookup = new DescentEvidenceLookup(faces, [], new());
        Assert.Equal(new[] { 0, 4 }, lookup.Solid(faces[0]).Members.ToArray());
        Assert.Equal(-4, lookup.Solid(faces[0]).Minimum);
        Assert.Equal(2, lookup.Solid(faces[0]).Maximum);
        Assert.Equal(new[] { 3 }, lookup.Solid(faces[3]).Members.ToArray());
        Assert.Empty(lookup.Solid(Face("absent", 7, 0)).Members.ToArray());
    }

    [Fact]
    public void LookupStorageParticipatesInTheCheckedAggregateBeforeAllocation()
    {
        var budget = new DescentCaptureIndexBudget(0);
        var refusal = Assert.Throws<PhysicsNavDescentEvidenceException>(() =>
            new DescentEvidenceLookup([], [new(0, 0, 0, 0, 0)], budget));
        Assert.Equal(PhysicsNavDescentEvidenceReason.IndexStorageExceeded, refusal.Reason);
        Assert.Equal(PhysicsEvidenceStatus.CapacityExceeded, refusal.Status);
        Assert.Equal(0, refusal.MaximumIndexBytes);
        Assert.True(refusal.RequestedIndexBytes > refusal.MaximumIndexBytes);
    }

    [Fact]
    public void ReceiptConstructionAndForeignFacesDoNotInventDependencies()
    {
        var face = Face("one", 0, 0);
        var foreign = Face("one", 0, 0);
        var options = new PhysicsNavBakeOptions(0, 0, 1, 1, 1, 10, 14, 0.7f, 1, 16);
        var e = new PhysicsNavDescentEvidence(options, 1, 1, [face], [0, 0], [],
            [DescentCoverage.Complete], [], [[]]);
        var observed = new ContactTerrainEvidenceReads(e);
        using (observed.Observe())
        {
            _ = new ContactTerrainEvidenceReads(e);
            Assert.Empty(observed.FaceRecords);
            _ = foreign.Points.Length;
            Assert.Empty(observed.Points);
            _ = e.Face(0).Points.Length;
        }
        Assert.Equal(new[] { 0 }, observed.FaceRecords);
        Assert.Equal(new[] { face.Key }, observed.Points);
    }

    static DescentFace Face(string source, long address, int part, PhysicsEvidenceKind kind = PhysicsEvidenceKind.SolidPolygon, double height = 0)
        => new(new(source, address, part), new("world", "owner"), false, kind,
            [new(0, height, 0), new(1, height, 0), new(0, height, 1)], default, 0, 0);
}
