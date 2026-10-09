using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class FinePatchConversionRimTests
{
    const string SourceId = "coarse";
    const string FineId = "fine";

    [Theory]
    [InlineData(2, true, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    public void Conversion_PartialLegacyRimPreservesCanonicalSeamsAndReportsOffsetDelta(int k, bool flat, bool midpoint)
    {
        MapSurfaceSet input = LegacyRow(flat, midpoint);
        MapSurfacePatch original = input.Patches[new(SourceId, 0, 0)];
        var digests = Digests(input);
        MapSurfaceRef source = Assert.Single(input.Refs);
        MapSurfacePatch measured = original.Clone();
        measured.SetPresent(1, 0, false);
        MapCompiledPatch old = MapSurfaceCompiler.Compile(source, measured);
        MapExactValue maximum = default;
        for (int i = 0; i < old.VertexIds.Count; i++)
        {
            MapLatticeAddress a = old.VertexIds[i].Address;
            Assert.Equal(1, a.Denominator);
            float canonical = (float)(original.Height((int)a.X, (int)a.Z) / 100.0);
            MapExactValue delta = MapExactValue.FromSingle(canonical).Subtract(MapExactValue.FromSingle(old.Offsets[i].Y));
            if (delta.Sign < 0) delta = delta.Negate();
            if (delta.CompareTo(maximum) > 0) maximum = delta;
        }
        if (!flat) Assert.True(maximum.Sign > 0);

        MapConversionResult result = MapFinePatchConversion.Convert(input, Request(k, true));

        Assert.Equal(digests, Digests(input));
        MapAuthoredDifference arithmetic = Assert.Single(result.Differences, d => d.Kind == MapDifferenceKind.ArithmeticPolicy);
        Assert.Equal((0L, 0L), (arithmetic.CellX, arithmetic.CellZ));
        Assert.Equal($"LegacyTileWorld to Native arithmetic, max delta {maximum} m in submitted offsets", arithmetic.Detail);
        AssertValid(result.Candidate);
        MapSurfaceSeam seam = Assert.Single(result.Candidate.AllRecords().OfType<MapSurfaceSeam>());
        MapExactValue[] fractions = midpoint
            ? new MapExactValue[] { new(0, 1), new(1, 3), new(1, 2), new(2, 3), new(1, 1) }
            : Enumerable.Range(0, k + 1).Select(i => new MapExactValue(i, k)).ToArray();
        Assert.Equal(fractions, seam.Pairs.Select(p => new MapExactValue(p.First.Address.Z, p.First.Address.Denominator)));
        MapExactPoint[] expected = fractions.Select(t => new MapExactPoint(new(1, 1),
            flat ? new(1, 100) : new MapExactValue(503, 100).Subtract(t.Multiply(new(264, 100))), t.Negate())).ToArray();
        Assert.Equal(expected, Resolve(result.Candidate, seam.First.Patch, seam.Pairs.Select(p => p.First)));
        Assert.Equal(expected, Resolve(result.Candidate, seam.Second.Patch, seam.Pairs.Select(p => p.Second)));
        MapSurfacePatch fine = result.Candidate.Patches[seam.Second.Patch];
        Assert.Equal(k + 1, fine.CornerDependencies.Count);
        Assert.All(fine.CornerDependencies, d => Assert.Equal(original.Key, d.Owner.Patch));
        if (midpoint) Assert.Contains(new MapEdgeSubdivision(2, 1, MapCellEdge.East, 2), fine.EdgeSubdivisions);
        MapSurfacePatch coarse = result.Candidate.Patches[original.Key];
        Assert.False(coarse.IsPresent(0, 0));
        Assert.True(coarse.IsPresent(1, 0));
        Assert.Contains(result.Differences, d => d.Kind == MapDifferenceKind.Retessellated && d.CellX == 1 && d.CellZ == 0);
    }

    [Theory]
    [InlineData(2, true, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    public void Conversion_PartialLegacyRimStillRequiresAcceptanceAtomically(int k, bool flat, bool midpoint)
    {
        MapSurfaceSet input = LegacyRow(flat, midpoint);
        var digests = Digests(input);
        MapSurfaceRef[] refs = input.Refs.ToArray();

        string message = Assert.Throws<MapDocumentException>(() => MapFinePatchConversion.Convert(input, Request(k, false))).Message;

        Assert.Contains("authored difference", message);
        Assert.Contains("cell (0, 0)", message);
        Assert.Equal(digests, Digests(input));
        Assert.Equal(refs, input.Refs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-22)]
    public void Conversion_RetargetsEveryExistingFractionalOwnerAtTheSameExactPoint(int cell)
    {
        MapSurfaceSet input = FractionalOwners(cell);
        var digests = Digests(input);
        MapPatchKey sourceKey = input.Patches.Keys.Single(p => p.SurfaceId == SourceId);
        var oldOwner = new MapVertexOwner(sourceKey, MapLatticeAddress.Create(2L * cell + 1, 0, 2));
        var newOwner = new MapVertexOwner(MapPatchKey.ForCell(FineId, 2L * cell + 1, 0),
            MapLatticeAddress.Corner(2L * cell + 1, 0));

        MapConversionResult result = MapFinePatchConversion.Convert(input, Request(2, false, cell));

        Assert.Equal(digests, Digests(input));
        AssertValid(result.Candidate);
        Assert.Equal(2, result.WriteSet.OwnerChanges.Count);
        foreach (string id in new[] { "dependent-a", "dependent-b" })
        {
            MapPatchKey key = input.Patches.Keys.Single(p => p.SurfaceId == id);
            MapSurfacePatch patch = result.Candidate.Patches[key];
            Assert.Contains(key, result.WriteSet.Patches);
            Assert.Contains(id, result.WriteSet.SurfaceIds);
            Assert.Equal(newOwner, Assert.Single(patch.CornerDependencies, d => d.CornerX == 0).Owner);
            Assert.Equal(new MapVertexOwner(sourceKey, MapLatticeAddress.Corner(cell + 1, 0)),
                Assert.Single(patch.CornerDependencies, d => d.CornerX == 1).Owner);
            Assert.Contains(new MapCornerOwnerChange(key, 0, 0, oldOwner, newOwner), result.WriteSet.OwnerChanges);
            Assert.Equal(new MapExactPoint(new(2L * cell + 1, 2), new(101, 100), new(0, 1)),
                Resolve(result.Candidate, newOwner.Patch, new[] { new MapLatticeVertex(FineId, newOwner.Address),
                    new MapLatticeVertex(FineId, MapLatticeAddress.Corner(2L * cell + 2, 0)) })[0]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-22)]
    public void Conversion_RefusesAnUnrepresentableFractionalOwnerBeforeMutating(int cell)
    {
        MapSurfaceSet input = FractionalOwners(cell);
        var digests = Digests(input);
        MapSurfaceRef[] refs = input.Refs.ToArray();

        string message = Assert.Throws<MapDocumentException>(() => MapFinePatchConversion.Convert(input, Request(3, false, cell))).Message;

        Assert.Contains("owner", message);
        Assert.Contains("dependent-a", message);
        Assert.Contains("corner (0, 0)", message);
        Assert.Contains($"({2L * cell + 1}/2, 0/2)", message);
        Assert.Equal(digests, Digests(input));
        Assert.Equal(refs, input.Refs);
    }

    [Fact]
    public void Conversion_ValidatesExistingUnchangedCornerDependenciesBeforeReturning()
    {
        MapSurfaceSet input = FractionalOwners(0);
        MapSurfacePatch dependent = input.Patches.Values.Single(p => p.Key.SurfaceId == "dependent-b");
        dependent.Heights[1]++;
        var digests = Digests(input);

        Assert.Contains("owner", Assert.Throws<MapDocumentException>(() =>
            MapFinePatchConversion.Convert(input, Request(2, false))).Message);

        Assert.Equal(digests, Digests(input));
    }

    static MapSurfaceSet LegacyRow(bool flat, bool midpoint)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new(SourceId, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
            MapPresencePolicy.LegacyTileWorld, null, null, ""));
        MapSurfaceCell cell = new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);
        var patch = new MapSurfacePatch
        {
            Key = new(SourceId, 0, 0),
            Width = 2,
            Depth = 1,
            Heights = flat ? Enumerable.Repeat(1, 6).ToArray() : new[] { 367, 503, 639, 101, 239, 377 },
            Cells = new[] { cell, midpoint ? cell with { Overlay = 2, Cut = MapOverlayCut.CornerQuarter } : cell },
            Presence = new ulong[] { 3 },
        };
        set.Patches.Add(patch.Key, patch);
        AssertValid(set);
        return set;
    }

    static MapSurfaceSet FractionalOwners(int cell)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new(SourceId, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
            MapPresencePolicy.Native, null, null, ""));
        MapPatchKey sourceKey = MapPatchKey.ForCell(SourceId, cell, 0);
        MapSurfaceCell full = new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);
        var source = new MapSurfacePatch
        {
            Key = sourceKey,
            CellMinX = (int)(cell - sourceKey.SlotX * 64),
            Width = 1,
            Depth = 1,
            Heights = new[] { 100, 102, 104, 106 },
            Cells = new[] { full },
            Presence = new ulong[] { 1 },
        };
        source.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, 2));
        set.Patches.Add(source.Key, source);
        foreach (string id in new[] { "dependent-a", "dependent-b" })
        {
            set.Refs.Add(new(id, new(new(1, 2), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
                MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
            MapPatchKey key = MapPatchKey.ForCell(id, 2L * cell + 1, 0);
            var patch = new MapSurfacePatch
            {
                Key = key,
                CellMinX = (int)(2L * cell + 1 - key.SlotX * 64),
                Width = 1,
                Depth = 1,
                Heights = new[] { 101, 102, 103, 104 },
                Cells = new[] { full },
                Presence = new ulong[] { 1 },
            };
            patch.CornerDependencies.Add(new(0, 0, new(sourceKey, MapLatticeAddress.Create(2L * cell + 1, 0, 2))));
            patch.CornerDependencies.Add(new(1, 0, new(sourceKey, MapLatticeAddress.Corner(cell + 1, 0))));
            set.Patches.Add(key, patch);
        }
        AssertValid(set);
        return set;
    }

    static MapFinePatchRequest Request(int k, bool accept, int cell = 0) =>
        new(SourceId, new[] { new MapCellRect(cell, 0, cell + 1, 1) }, k, FineId, accept);

    static Dictionary<MapPatchKey, string> Digests(MapSurfaceSet set) =>
        set.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));

    static void AssertValid(MapSurfaceSet set)
    {
        Assert.Empty(MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values));
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        Assert.All(set.Patches.Values, p =>
        {
            Assert.Empty(p.ValidateLocal());
            Assert.Empty(MapSeamValidator.ValidateCornerDependencies(p, view));
        });
        Assert.All(set.AllRecords().OfType<MapSurfaceSeam>(), s => Assert.Empty(MapSeamValidator.Validate(s, view)));
    }

    static IReadOnlyList<MapExactPoint> Resolve(MapSurfaceSet set, MapPatchKey patch, IEnumerable<MapLatticeVertex> vertices)
    {
        var chain = new MapBoundaryChain("test-owner", MapChainKind.SurfaceEdge, patch,
            vertices.Select(v => new MapChainVertex(v, null)).ToArray());
        MapChainResolution result = MapBoundaryGeometry.ResolveChain(chain, MapScopedSurfaces.CompleteView(set));
        Assert.Equal(MapResolveStatus.Resolved, result.Status);
        Assert.Null(result.Detail);
        return result.Points;
    }
}
