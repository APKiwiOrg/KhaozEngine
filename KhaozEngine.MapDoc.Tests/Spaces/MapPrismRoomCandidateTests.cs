using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed class MapPrismRoomCandidateTests
{
    static MapWaterDomainDoc Strips() => new(new(new(1, 64), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
        Enumerable.Range(0, 32).Select(i => new MapWaterRect(2 * i, 0, 2 * i + 1, 64)).ToArray(), []);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighFanOut_HoldsOnlyOneAdmittedRowAndUnwindsAnEarlyByteRefusal(bool accepted)
    {
        var domain = MapWaterDomain.Compile(Strips());
        var bounds = new MapWaterPolygons.Bounds(MapWideXz.From(new(new(0, 1), new(-1, 1))),
            MapWideXz.From(new(new(1, 1), new(0, 1))));
        var parents = Enumerable.Repeat(bounds, 128).ToArray();
        // One 32-piece index, then a 32-entry result's growth allowance and six traversal-stack slots.
        const long indexBytes = 512 + 512 * 32, rowPeak = 128 + 64 * 6 + 16 * 32;
        const long peak = 32768 + indexBytes + rowPeak;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: accepted ? peak : peak - 1));
        var counts = new MapRoofPreparationCounts();
        using (var validation = new MapPrismValidationWorkspace(admission, counts, 0))
        {
            using var candidates = new MapPrismRoomCandidates(domain, parents);
            Assert.Equal(32768 + indexBytes, admission.WorkspaceBytes);
            if (!accepted)
            {
                var refusal = Assert.Throws<MapSceneCapacityException>(() => candidates.Pieces(0).ToArray());
                Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", peak, peak - 1), refusal.Capacity);
                Assert.Equal(32768 + indexBytes, admission.WorkspaceBytes);
                Assert.Equal(0, admission.RetainedBytes);
            }
            else
                for (int parent = 0; parent < parents.Length; parent++)
                {
                    int piece = 0;
                    foreach (int candidate in candidates.Pieces(parent))
                    {
                        Assert.Equal(piece++, candidate);
                        Assert.Equal(32768 + indexBytes, admission.WorkspaceBytes);
                        Assert.Equal(4 * 32, admission.RetainedBytes);
                    }
                    Assert.Equal(32, piece);
                    Assert.Equal(0, admission.RetainedBytes);
                }
        }
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
        if (accepted)
        {
            Assert.Equal(32, counts.PeakValidationCandidates);
            Assert.Equal(peak, counts.ValidationPeakBytes);
        }
    }

    [Fact]
    public void MultiPieceRoom_UsesBoundedRowsWithoutChangingItsRefinedOccupancy()
    {
        var fixture = PrefabFixtures.StandalonePrism();
        foreach (var patch in fixture.Document.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Records.Count; i++)
                patch.Records[i] = patch.Records[i] switch
                {
                    MapSpaceFootprint footprint => footprint with { SlotCells = [0] },
                    MapPrismDomain prism => prism with { Footprint = Strips() },
                    var record => record
                };
        var view = PrefabFixtures.Effective(fixture).CompleteView();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2000000));
        using var active = admission.Enter();
        var counts = new MapRoofPreparationCounts();
        using (var validation = new MapPrismValidationWorkspace(admission, counts, 0))
        {
            var result = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(-1, 1)), new(new(1, 1), new(0, 1)), new(), false);
            Assert.Equal(MapRefinementStatus.Complete, result.Status);
            // Two root triangles, each clipped by every one of the 32 separated strips.
            Assert.Equal(66, result.Pieces.Count);
            Assert.Equal(64, result.Pieces.Count(p => p.SpaceId == "a/room"));
            Assert.Equal(32, counts.PeakValidationCandidates);
        }
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }
}
