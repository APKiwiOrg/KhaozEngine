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

// Bounded one-pose functional validation of the synthetic cost fixtures through real construction, public binding
// and render certification on the recording device. A counting clock replaces elapsed time, so nothing is timed.
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

    // One pose per row, lifted (0) or bent (1). The same pose repeats so point shadow request, base residency and
    // transient demand reach their third boundary stage before the asserted frame.
    [Theory]
    // Typical rows bind two actors. Limit rows bind one, because level 4 render certification over every pass is the
    // slowest bounded case and two actors add no binding coverage the typical rows lack.
    [InlineData(false, false, 0, 2)]
    [InlineData(false, true, 1, 2)]
    [InlineData(true, false, 1, 1)]
    [InlineData(true, true, 0, 1)]
    public void Every_fixture_actor_is_covered_through_public_binding_and_every_pass(bool limit, bool gpuSkinning, int pose, int actors)
    {
        using var scene = new CoverageCostRecordingScene();
        long ticks = 0;
        var geometry = CoverageCostGeometry.Build(limit ? CoverageCostFixtureMode.Limit : CoverageCostFixtureMode.Typical);
        using var workload = new CoverageCostWorkload(scene.Scene, geometry, actors, true, gpuSkinning, CoverageCostPassSet.Full, () => ++ticks);
        var sample = new CoverageCostFrameSample();
        for (int frame = 0; frame < 4; frame++)
        {
            sample = default;
            workload.Submit(pose, ref sample);
            workload.Reserve(CoverageCostRecordingScene.Width, CoverageCostRecordingScene.Height, ref sample);
            scene.Render();
            workload.Collect(ref sample);
        }

        Assert.True(sample.Declined == 0, $"declined {sample.Declined}: {workload.FirstDecline}");
        Assert.Equal((actors, actors, 0), (sample.ActorFrames, sample.Accepted, sample.Declined));
        Assert.True(sample.Fallback == 0, $"{workload.FirstFallback} {string.Join(", ", workload.ReadRefusals())}");
        Assert.Equal((actors, 0), (sample.Covered, sample.Fallback));
        Assert.Equal((actors, actors, actors), (sample.Skinned, sample.Evaluated, sample.Certified));
        Assert.Equal((actors, 0), scene.Scene.CoverageDiagnosticsForTests);
        Assert.Empty(workload.ReadRefusals());
        Assert.True(sample.Passes > 0);
        Assert.True(sample.PointFacePasses > 0, "The full pass set must capture point shadow faces.");
        Assert.Equal(1, sample.OutlineGroups);
        Assert.All(new[] { sample.PrepareTicks, sample.SkinningTicks, sample.GeometryTicks, sample.ResolutionTicks,
            sample.CertificationTicks, sample.SurfaceTicks }, t => Assert.True(t > 0));
        Assert.True(sample.SkinningTicks + sample.GeometryTicks < sample.PrepareTicks);
        Assert.True(sample.CertificationTicks < sample.ResolutionTicks);
        var topology = workload.Topology;
        Assert.Equal(geometry.Topology, topology);
        Assert.Contains(workload.ReadCapacities(), c => c.Name == "actor.scratch.vertices" && c.Value == topology.FinalVertices);
        Assert.Contains(workload.ReadCapacities(), c => c.Name == "shared.cpuSkinnedVertexBuffer.bytes" && c.Status == CoverageCostStatus.Measured);
    }

    [Fact]
    public void Colour_only_and_uncovered_workloads_run_the_same_fixture_without_coverage_passes()
    {
        using var scene = new CoverageCostRecordingScene();
        long ticks = 0;
        using var workload = new CoverageCostWorkload(scene.Scene, CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical), 2, false,
            false, CoverageCostPassSet.ColourOnly, () => ++ticks);
        var sample = new CoverageCostFrameSample();
        workload.Submit(0, ref sample);
        workload.Reserve(CoverageCostRecordingScene.Width, CoverageCostRecordingScene.Height, ref sample);
        scene.Render();
        workload.Collect(ref sample);
        Assert.Equal((2, 0, 0, 0), (sample.ActorFrames, sample.Accepted, sample.Covered, sample.Skinned));
        Assert.Equal((0, 0), scene.Scene.CoverageDiagnosticsForTests);
        Assert.Equal(0, sample.OutlineGroups);
        Assert.True(sample.PrepareTicks > 0);
    }

    [Fact]
    public void Stage_observer_nests_inside_preparation_and_render_resolution()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out var failure), failure.ToString());
        var events = new List<string>();
        f.Scene.CoverageStageObserverForTests = (span, entering) => events.Add($"{span}:{(entering ? "in" : "out")}");
        f.Scene.CoverageAllocationObserverForTests = entering => events.Add($"Resolution:{(entering ? "in" : "out")}");
        f.Scene.Begin();
        events.Add("prepare");
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out failure), failure.ToString());
        events.Add("prepared");
        f.Submit(frame, binding.PreparedEquipmentDraws.ToArray());
        f.Render();
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(new[] { "prepare", "Skinning:in", "Skinning:out", "GeometryEvaluation:in", "GeometryEvaluation:out", "prepared",
            "Resolution:in", "RenderCertification:in", "RenderCertification:out", "Resolution:out" }, events);

        // Removing the observer restores the silent path.
        f.Scene.CoverageStageObserverForTests = null;
        f.Scene.CoverageAllocationObserverForTests = null;
        events.Clear();
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out frame, out failure), failure.ToString());
        f.Submit(frame, binding.PreparedEquipmentDraws.ToArray());
        f.Render();
        Assert.Empty(events);
    }

    [Fact]
    public void Refused_staging_reports_the_evaluator_span_without_a_certification_span()
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([equipment], out var failure), failure.ToString());
        var events = new List<string>();
        f.Scene.CoverageStageObserverForTests = (span, entering) => events.Add($"{span}:{(entering ? "in" : "out")}");
        f.Scene.Begin();
        // A large lift of the hip alone moves part of the core far from its equipment support.
        var palette = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 0, 3) };
        Assert.False(binding.TryPrepare(f.Skeleton, palette, Matrix4x4.Identity, f.Template, out _, out failure));
        Assert.Equal(new[] { "Skinning:in", "Skinning:out", "GeometryEvaluation:in", "GeometryEvaluation:out" }, events);
    }
}
