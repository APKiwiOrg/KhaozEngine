using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

// Cold historical fixture inputs survive without executing the retired measurement route.
public sealed class CoverageCostFixtureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 2, 200, 2)]
    [InlineData(true, 4, 200, 4)]
    public void Fixture_topology_is_measured_from_the_compiled_surface(bool limit, int commands, int eligible, int level)
    {
        var geometry = CoverageCostGeometry.Build(limit ? CoverageCostFixtureMode.Limit : CoverageCostFixtureMode.Typical);
        var topology = geometry.Topology;
        output.WriteLine($"{geometry.Mode}: {topology}");
        Assert.Equal(2000, geometry.Source.Triangles.Count);
        Assert.Equal(2000, topology.SourceTriangles);
        Assert.Equal(eligible, topology.EligibleTriangles);
        Assert.Equal(eligible, geometry.Map.Regions.Sum(r => r.TriangleCount));
        Assert.Equal(commands, geometry.Commands.Length);
        Assert.Equal(commands, topology.Commands);
        Assert.Equal(commands, geometry.Map.Regions.Count);
        Assert.Equal(2, topology.Materials);
        Assert.Equal(2, geometry.Source.Materials.Count);
        Assert.All(geometry.Map.Refinement, r => Assert.Equal(level, r.Level));
        Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), topology.RefinementLevels);
        Assert.Equal(geometry.Refinement.VertexCount, topology.FinalVertices);
        Assert.Equal(geometry.Refinement.TriangleCount, topology.FinalTriangles);
        Assert.Equal(geometry.Source.Vertices.Count, topology.SourceVertices);
        Assert.True(topology.FinalTriangles > topology.SourceTriangles);
        Assert.InRange(topology.FinalVertices, 1, HostCoverageMap.MaxSurfaceVertices - 1);
        Assert.InRange(topology.FinalTriangles, 1, HostCoverageMap.MaxTriangles - 1);
        // Regions are disjoint, each wholly inside one material slice, and both slices carry coverage.
        var owners = new Dictionary<(int, int), string>();
        foreach (var region in geometry.Map.Regions)
            foreach (var entry in region.Triangles)
                for (int t = 0; t < entry.Triangles.Count; t++)
                    Assert.True(owners.TryAdd((entry.Triangles.Primitive, entry.Triangles.FirstTriangle + t), region.Key));
        Assert.Equal(2, geometry.Map.Regions.Select(r => r.Triangles.Select(t => t.Triangles.Primitive).Distinct().Single()).Distinct().Count());
    }

    [Fact]
    public void Old_authorization_is_rejected_before_parsing_or_measurement_callbacks()
    {
        int callbacks = 0;
        Assert.Throws<InvalidOperationException>(() => CoverageCostHarness.RejectRetiredAuthorization("typical-a1-cpuskin-full"));
        byte[] historical = System.Text.Encoding.UTF8.GetBytes(CoverageCostContractTests.Valid);
        Assert.Throws<InvalidOperationException>(() => CoverageCostHarness.MeasureUnit(historical, "typical-a1-cpuskin-full", _ => callbacks++));
        Assert.Throws<InvalidOperationException>(() => CoverageCostHarness.MeasureUnit([], "retired", _ => callbacks++));
        Assert.Throws<InvalidOperationException>(() => CoverageCostHarness.Measure(null!, _ => callbacks++));
        Assert.Throws<InvalidOperationException>(() => CoverageCostHarness.MeasurePair(null!, _ => callbacks++));
        Assert.Equal(0, callbacks);
    }
}
