using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class BoundaryContractTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void SurfaceEdge_ResolvesExactCornerLegacyMidpointAndSubdivisionHeights(int segments)
    {
        var (surface, patch) = CompilerFixtures.Row(MapPresencePolicy.LegacyTileWorld,
            (CompilerFixtures.Full with { Overlay = 2, Cut = MapOverlayCut.CornerQuarter }, true));
        patch.Heights = new[] { 101, 239, 367, 503 };
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, segments));
        var chain = new MapBoundaryChain("rim", MapChainKind.SurfaceEdge, patch.Key, new[]
        {
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(0, 0)), null),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Create(1, 0, segments)), null),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Create(1, 0, 2)), null),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(1, 0)), null),
        });
        patch.Records.Add(chain);
        MapScopedSurfaces view = CompleteView(new[] { surface }, patch);
        MapExactValue corner = MapExactValue.FromSingle(101 * 0.01f);
        MapExactValue midpoint = MapExactValue.FromSingle((101 * 0.01f + 239 * 0.01f) * 0.5f);
        MapExactValue subdivision = corner.Add(midpoint.Subtract(corner).Multiply(new(2, segments)));
        MapExactPoint[] expected =
        {
            new(new(0, 1), corner, new(0, 1)),
            new(new(1, segments), subdivision, new(0, 1)),
            new(new(1, 2), midpoint, new(0, 1)),
            new(new(1, 1), MapExactValue.FromSingle(239 * 0.01f), new(0, 1)),
        };

        MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);

        Assert.Equal(MapResolveStatus.Resolved, resolved.Status);
        Assert.Null(resolved.Detail);
        Assert.Equal(expected, resolved.Points);
    }

    [Fact]
    public void SurfaceEdge_UnrepresentableSourcePatchRefusesWithoutPartialPoints()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 100, 200, 300, CompilerFixtures.Full);
        patch.Key = new(surface.Id, long.MaxValue / 64, 0);
        patch.CellMinX = 63;
        var chain = new MapBoundaryChain("overflow-rim", MapChainKind.SurfaceEdge, patch.Key, new[]
        {
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(long.MaxValue, 0)), null),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(long.MaxValue, 1)), null),
        });
        patch.Records.Add(chain);
        AssertValid(new[] { surface }, patch);
        Assert.True((Int128)patch.Key.SlotX * 64 + 63 + 1 > long.MaxValue);
        MapDocument doc = AcquisitionBoundFixtures.Document();
        doc.Surfaces.Refs.Add(surface);
        doc.Surfaces.Patches.Add(patch.Key, patch);
        var probe = new R2AcquisitionProbeSource(doc, new[] { patch.Key });
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope());
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.Equal(MapPatchStatus.Present, view.Patch(patch.Key).Status);

        MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);

        Assert.Equal(MapResolveStatus.Invalid, resolved.Status);
        Assert.Empty(resolved.Points);
        Assert.NotNull(resolved.Detail);
        Assert.Contains("not representable", resolved.Detail);
        Assert.DoesNotContain("CapacityExceeded", resolved.Detail);
        Assert.DoesNotContain("budget", resolved.Detail);
    }

    [Theory]
    [InlineData(MapPatchStatus.KnownEmpty)]
    [InlineData(MapPatchStatus.Unloaded)]
    [InlineData(MapPatchStatus.Missing)]
    [InlineData(MapPatchStatus.Corrupt)]
    public void SurfaceEdge_UnavailableSourceReturnsMissingGeometry(MapPatchStatus status)
    {
        var (view, chain, _) = UnavailableSource(status);

        MapChainResolution resolved = MapBoundaryGeometry.ResolveChain(chain, view);

        Assert.Equal(MapResolveStatus.MissingGeometry, resolved.Status);
        Assert.Empty(resolved.Points);
        Assert.NotNull(resolved.Detail);
        Assert.Contains("unresolved surface patch", resolved.Detail);
    }

    [Theory]
    [InlineData(MapPatchStatus.KnownEmpty)]
    [InlineData(MapPatchStatus.Unloaded)]
    [InlineData(MapPatchStatus.Missing)]
    [InlineData(MapPatchStatus.Corrupt)]
    public void CornerDependency_UnavailableOwnerReportsUnresolved(MapPatchStatus status)
    {
        var (view, _, dependent) = UnavailableSource(status);

        string finding = Assert.Single(MapSeamValidator.ValidateCornerDependencies(dependent, view));

        Assert.Contains("unresolved corner owner patch", finding);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, true)]
    public void WallStrip_EqualLengthDifferentOrderedWorldPositionsRefuse(int shiftX, int shiftZ, bool reverse)
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var lower = new MapBoundaryChain("lower", MapChainKind.Authored, null, new[]
        {
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(0, 0)), 0),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(1, 0)), 0),
        });
        var upper = new MapBoundaryChain("upper", MapChainKind.Authored, null,
            Enumerable.Range(0, 2).Select(i => new MapChainVertex(new(surface.Id,
                MapLatticeAddress.Corner(shiftX + (reverse ? 1 - i : i), shiftZ)), 300)).ToArray());
        var strip = new MapWallStrip("wall", new(lower.Id, patch.Key), new(upper.Id, patch.Key), MapStripFacing.Front, 1);
        patch.Records.AddRange(new MapTopologyRecord[] { lower, upper, strip });
        MapScopedSurfaces view = CompleteView(new[] { surface }, patch);
        MapChainResolution l = MapBoundaryGeometry.ResolveChain(lower, view);
        MapChainResolution u = MapBoundaryGeometry.ResolveChain(upper, view);
        Assert.Equal(MapResolveStatus.Resolved, l.Status);
        Assert.Equal(MapResolveStatus.Resolved, u.Status);
        Assert.Equal(new[] { new MapExactPoint(new(0, 1), new(0, 1), new(0, 1)), new(new(1, 1), new(0, 1), new(0, 1)) }, l.Points);
        Assert.Equal(Enumerable.Range(0, 2).Select(i => new MapExactPoint(
            new(shiftX + (reverse ? 1 - i : i), 1), new(3, 1), new(shiftZ, 1))), u.Points);
        Assert.Equal(l.Points.Count, u.Points.Count);

        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(strip, l, u));

        Assert.Contains("vertex sequence", error.Message);
    }

    [Fact]
    public void Seam_DifferentWorldPositionsReportPositionDespiteCompleteEdgeCoverage()
    {
        var (originalView, originalSeam) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        var seam = originalSeam with
        {
            Pairs = originalSeam.Pairs.Select((pair, i) =>
                (pair.First, originalSeam.Pairs[originalSeam.Pairs.Count - 1 - i].Second)).ToArray(),
        };
        MapSurfacePatch coarse = originalView.Patch(seam.First.Patch).Patch!;
        MapSurfacePatch fine = originalView.Patch(seam.Second.Patch).Patch!;
        coarse.Records.Clear();
        coarse.Records.Add(seam);
        MapScopedSurfaces view = CompleteView(originalView.Surfaces.ToArray(), coarse, fine);
        Assert.Equal(MapLatticeAddress.Corner(0, 1), seam.Pairs[0].First.Address);
        Assert.Equal(MapLatticeAddress.Corner(2, 2), seam.Pairs[0].Second.Address);
        // The first pair is world X 0 versus 1 on the same world Z 1.

        var findings = MapSeamValidator.Validate(seam, view);

        Assert.Equal(2, findings.Count(f => f.Contains("position", StringComparison.Ordinal)));
        Assert.DoesNotContain(findings, f => f.Contains("subdivide", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MapStripFacing.Front)]
    [InlineData(MapStripFacing.Back)]
    [InlineData(MapStripFacing.TwoSided)]
    public void WallStrip_EachTriangleUsesTheLowerStartToUpperEndDiagonal(MapStripFacing facing)
    {
        MapExactPoint[] lower =
        {
            new(new(0, 1), new(0, 1), new(0, 1)),
            new(new(1, 1), new(1, 1), new(0, 1)),
            new(new(2, 1), new(1, 2), new(0, 1)),
        };
        MapExactPoint[] upper =
        {
            new(new(0, 1), new(2, 1), new(0, 1)),
            new(new(1, 1), new(4, 1), new(0, 1)),
            new(new(2, 1), new(7, 2), new(0, 1)),
        };

        MapCompiledStrip strip = BoundaryFixtures.CompileStrip(new[] { 0, 100, 50 }, new[] { 200, 400, 350 }, facing);

        Assert.Equal(facing == MapStripFacing.TwoSided ? 8 : 4, strip.Faces.Count);
        Assert.Equal(lower.Concat(upper), strip.ExactVertices);
        MapSide[] sides = facing == MapStripFacing.TwoSided ? new[] { MapSide.Front, MapSide.Back }
            : new[] { facing == MapStripFacing.Front ? MapSide.Front : MapSide.Back };
        foreach (MapSide side in sides)
            for (int segment = 0; segment < 2; segment++)
                for (byte triangle = 0; triangle < 2; triangle++)
                {
                    var key = new MapFaceKey("w", null, segment, triangle, 0, side);
                    MapCompiledFace face = Assert.Single(strip.Faces, f => f.Key == key);
                    MapExactPoint[] expected = triangle == 0
                        ? new[] { lower[segment], lower[segment + 1], upper[segment + 1] }
                        : new[] { lower[segment], upper[segment + 1], upper[segment] };
                    MapExactPoint[] actual = { strip.ExactVertices[face.A], strip.ExactVertices[face.B], strip.ExactVertices[face.C] };
                    Assert.Equal(expected.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z),
                        actual.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z));
                }
    }

    static (MapScopedSurfaces View, MapBoundaryChain Chain, MapSurfacePatch Dependent) UnavailableSource(MapPatchStatus status)
    {
        var (surface, source) = CompilerFixtures.OneCell(101, 239, 367, 503, CompilerFixtures.Full);
        var chain = new MapBoundaryChain("rim", MapChainKind.SurfaceEdge, source.Key, new[]
        {
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(0, 0)), null),
            new MapChainVertex(new(surface.Id, MapLatticeAddress.Corner(1, 0)), null),
        });
        source.Records.Add(chain);
        var (_, dependent) = CompilerFixtures.OneCell(101, 101, 101, 101, CompilerFixtures.Full);
        dependent.Key = new(surface.Id, -1, 0);
        dependent.CellMinX = 63;
        dependent.CornerDependencies.Add(new(1, 0, new(source.Key, MapLatticeAddress.Corner(0, 0))));
        AssertValid(new[] { surface }, source, dependent);
        MapDocument doc = AcquisitionBoundFixtures.Document();
        doc.Surfaces.Refs.Add(surface);
        doc.Surfaces.Patches.Add(source.Key, source);
        doc.Surfaces.Patches.Add(dependent.Key, dependent);
        var probe = new R2AcquisitionProbeSource(doc, new[] { source.Key, dependent.Key });
        probe.Reads[source.Key] = new(source.Key, status, null, null, "source unavailable", 0);
        MapScopedSurfaces view = ScopeFixtures.Acquire(probe, AcquisitionConformanceFixtures.Scope());
        Assert.Equal(status == MapPatchStatus.KnownEmpty ? MapAcquireStatus.Complete : MapAcquireStatus.Incomplete, view.Status);
        Assert.Equal(status == MapPatchStatus.KnownEmpty ? MapPatchStatus.KnownEmpty : MapPatchStatus.Unloaded, view.Patch(source.Key).Status);
        Assert.Null(view.Patch(source.Key).Patch);
        if (status != MapPatchStatus.KnownEmpty)
            Assert.Contains(view.Witness.Unavailable, u => u.Key == source.Key && u.Status == status);
        return (view, chain, dependent);
    }

    static MapScopedSurfaces CompleteView(MapSurfaceRef[] surfaces, params MapSurfacePatch[] patches)
    {
        AssertValid(surfaces, patches);
        var set = new MapSurfaceSet();
        set.Refs.AddRange(surfaces);
        foreach (MapSurfacePatch patch in patches) set.Patches.Add(patch.Key, patch);
        return MapScopedSurfaces.CompleteView(set);
    }

    static void AssertValid(MapSurfaceRef[] surfaces, params MapSurfacePatch[] patches)
    {
        foreach (MapSurfacePatch patch in patches) Assert.Empty(patch.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(surfaces, patches));
    }
}
