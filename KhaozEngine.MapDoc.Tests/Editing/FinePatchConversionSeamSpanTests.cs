using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FinePatchConversionSeamSpanTests
{
    const int Subdivision = 3;
    const string CoarseId = "coarse";
    const string FineId = "coarse-fine-1";

    [Theory]
    [InlineData(21, MapCellEdge.South)]
    [InlineData(21, MapCellEdge.East)]
    [InlineData(21, MapCellEdge.North)]
    [InlineData(21, MapCellEdge.West)]
    [InlineData(-22, MapCellEdge.South)]
    [InlineData(-22, MapCellEdge.East)]
    [InlineData(-22, MapCellEdge.North)]
    [InlineData(-22, MapCellEdge.West)]
    public void Conversion_CrossSlotRimHasOneCompleteExactSeamPerContiguousSpan(int cell, MapCellEdge edge)
    {
        MapSurfaceSet input = SlopedPatch(cell);
        MapSurfacePatch original = Assert.Single(input.Patches.Values);
        string originalDigest = MapSurfaceSemantics.PatchDigest(original);
        var request = new MapFinePatchRequest(CoarseId,
            new[] { new MapCellRect(cell, cell, cell + 1, cell + 1) }, Subdivision, FineId, false);
        MapConversionResult result = MapFinePatchConversion.Convert(input, request);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(result.Candidate);
        Assert.Equal(MapCellConversionClass.ExactCoplanar, Assert.Single(result.Cells).Class);
        Assert.Equal(originalDigest, MapSurfaceSemantics.PatchDigest(original));
        Assert.False(result.Candidate.Patches[original.Key].IsPresent(1, 1));
        Assert.Empty(MapTopologyReferenceValidator.Validate(result.Candidate.Refs, result.Candidate.Patches.Values));
        Assert.All(result.Candidate.Patches.Values, patch => Assert.Empty(patch.ValidateLocal()));
        Assert.All(result.Candidate.AllRecords().OfType<MapSurfaceSeam>(),
            seam => Assert.Empty(MapSeamValidator.Validate(seam, view)));

        bool horizontal = edge is MapCellEdge.South or MapCellEdge.North;
        int low = cell * Subdivision, high = (cell + 1) * Subdivision;
        int fixedCoordinate = edge is MapCellEdge.North or MapCellEdge.East ? high : low;
        int interiorCoordinate = edge is MapCellEdge.North or MapCellEdge.East ? high - 1 : low;
        // The single interior multiple of 64 divides this edge into two closed spans.
        int slotBoundary = (FloorSlot(low) + 1) * 64;
        Assert.InRange(slotBoundary, low + 1, high - 1);
        var spans = new[] { (Start: low, End: slotBoundary), (Start: slotBoundary, End: high) };
        MapSurfaceSeam[] seams = result.Candidate.AllRecords().OfType<MapSurfaceSeam>()
            .Where(seam => OnRim(seam.First.From) && OnRim(seam.First.To))
            .OrderBy(seam => seam.First.From.Address).ToArray();
        Assert.Equal(spans.Length, seams.Length);

        var stitched = new List<(MapLatticeVertex First, MapLatticeVertex Second)>();
        var finePatchVertices = new HashSet<(MapPatchKey Patch, MapLatticeVertex Vertex)>();
        for (int spanIndex = 0; spanIndex < spans.Length; spanIndex++)
        {
            var span = spans[spanIndex];
            MapPatchKey expectedPatch = horizontal
                ? new(FineId, FloorSlot(span.Start), FloorSlot(interiorCoordinate))
                : new(FineId, FloorSlot(interiorCoordinate), FloorSlot(span.Start));
            var expectedPairs = Enumerable.Range(span.Start, span.End - span.Start + 1).Select(Pair).ToArray();
            MapSurfaceSeam seam = seams[spanIndex];
            Assert.Equal(new MapSurfaceEdgeRef(original.Key, expectedPairs[0].First, expectedPairs[^1].First), seam.First);
            Assert.Equal(new MapSurfaceEdgeRef(expectedPatch, expectedPairs[0].Second, expectedPairs[^1].Second), seam.Second);
            Assert.Equal(expectedPairs, seam.Pairs);
            Assert.Equal(expectedPairs.Length, seam.Pairs.Select(pair => pair.First).Distinct().Count());
            Assert.Equal(expectedPairs.Length, seam.Pairs.Select(pair => pair.Second).Distinct().Count());
            Assert.Empty(MapSeamValidator.Validate(seam, view));

            IReadOnlyList<MapExactPoint> coarsePoints = Resolve(seam.First.Patch, seam.Pairs.Select(pair => pair.First), view);
            IReadOnlyList<MapExactPoint> finePoints = Resolve(seam.Second.Patch, seam.Pairs.Select(pair => pair.Second), view);
            MapSurfacePatch finePatch = result.Candidate.Patches[expectedPatch];
            for (int i = 0; i < expectedPairs.Length; i++)
            {
                MapLatticeAddress address = expectedPairs[i].Second.Address;
                int x = (int)address.X, z = (int)address.Z;
                int heightUnits = 300 + 6 * (x - Subdivision * (cell - 1)) + 9 * (z - Subdivision * (cell - 1));
                var expectedPoint = new MapExactPoint(new(x, 6), new(heightUnits, 300), new(cell < 0 ? -z : z, 6));
                Assert.Equal(expectedPoint, coarsePoints[i]);
                Assert.Equal(expectedPoint, finePoints[i]);
                Assert.Equal(heightUnits, finePatch.Height(x - (int)expectedPatch.SlotX * 64 - finePatch.CellMinX,
                    z - (int)expectedPatch.SlotZ * 64 - finePatch.CellMinZ));
                Assert.True(finePatchVertices.Add((expectedPatch, seam.Pairs[i].Second)));
            }

            // Adjacent closed seams share their endpoint. Count it once in the stitched edge.
            if (spanIndex != 0) Assert.Equal(stitched[^1], seam.Pairs[0]);
            stitched.AddRange(spanIndex == 0 ? seam.Pairs : seam.Pairs.Skip(1));
        }
        Assert.Equal(Enumerable.Range(low, Subdivision + 1).Select(Pair).ToArray(), stitched);
        Assert.Equal(Subdivision + 1, stitched.Select(pair => pair.First).Distinct().Count());
        Assert.Equal(Subdivision + 1, stitched.Select(pair => pair.Second).Distinct().Count());
        Assert.Equal(Subdivision + 2, finePatchVertices.Count);
        var occurrences = seams.SelectMany(seam => seam.Pairs).GroupBy(pair => pair).ToArray();
        Assert.Equal(Subdivision + 1, occurrences.Length);
        Assert.All(occurrences, group => Assert.Equal(group.Key == Pair(slotBoundary) ? 2 : 1, group.Count()));

        bool OnRim(MapLatticeVertex vertex)
        {
            MapLatticeAddress address = vertex.Address;
            return vertex.SurfaceId == CoarseId &&
                new MapExactValue(horizontal ? address.Z : address.X, address.Denominator) == new MapExactValue(fixedCoordinate, Subdivision);
        }

        (MapLatticeVertex First, MapLatticeVertex Second) Pair(int along)
        {
            int x = horizontal ? along : fixedCoordinate, z = horizontal ? fixedCoordinate : along;
            return (new(CoarseId, MapLatticeAddress.Create(x, z, Subdivision)), new(FineId, MapLatticeAddress.Corner(x, z)));
        }
    }

    static MapSurfaceSet SlopedPatch(int cell)
    {
        int slot = FloorSlot(cell - 1);
        var patch = new MapSurfacePatch
        {
            Key = new(CoarseId, slot, slot),
            CellMinX = cell - 1 - slot * 64,
            CellMinZ = cell - 1 - slot * 64,
            Width = 3,
            Depth = 3,
            Heights = new int[16],
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), 9).ToArray(),
            Presence = new ulong[1],
        };
        for (int z = 0; z <= 3; z++)
            for (int x = 0; x <= 3; x++) patch.Heights[z * 4 + x] = 100 + 6 * x + 9 * z;
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++) patch.SetPresent(x, z, true);
        var frame = new MapLatticeFrame(new(1, 2), new(1, 100),
            cell < 0 ? MapRowDirection.NegativeZ : MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var surface = new MapSurfaceRef(CoarseId, frame, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        Assert.Empty(patch.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values));
        _ = MapSurfaceCompiler.Compile(surface, patch);
        return set;
    }

    static IReadOnlyList<MapExactPoint> Resolve(MapPatchKey patch, IEnumerable<MapLatticeVertex> vertices, MapScopedSurfaces view)
    {
        var chain = new MapBoundaryChain("test-rim", MapChainKind.SurfaceEdge, patch,
            vertices.Select(vertex => new MapChainVertex(vertex, null)).ToArray());
        MapChainResolution resolution = MapBoundaryGeometry.ResolveChain(chain, view);
        Assert.Equal(MapResolveStatus.Resolved, resolution.Status);
        Assert.Null(resolution.Detail);
        return resolution.Points;
    }

    static int FloorSlot(int coordinate) => coordinate >= 0 ? coordinate / 64 : (coordinate - 63) / 64;
}
