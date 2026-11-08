using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageRecessionFixtureTests(ITestOutputHelper output)
{
    [Fact]
    public void Native_publication_observer_reads_the_actual_per_draw_surface_windows()
    {
        using var device = new CoveragePublicationDevice();
        using var observed = new CoverageRecessionObservedDevice(device);
        using var scene = new CoverageRecessionNativeScene(observed, new(4, 4), 1, true);
        using var recorded = new RecordingGpuCommandList(new NullGpuCommandList())
        { CaptureBindings = true, CaptureInstancedBindings = true, CapturePayloads = true };
        var evidence = new List<CoveragePassEvidence>();
        scene.Scene.CoveragePassObserverForTests = evidence.Add;
        for (int frame = 0; frame < 2; frame++)
        {
            evidence.Clear(); recorded.Clear(); scene.Prepare(frame);
            recorded.Begin(); scene.Render(recorded); recorded.End(); scene.Submission.Submit(recorded);
        }
        var surface = scene.Owners[0].Request.Surface;
        Assert.NotEqual(surface.CurrentBase, surface.PreviousBase);
        CoverageRecessionPublicationGpuTests.AssertDrawBindings(observed, recorded, scene, surface, evidence.Count);
        // A wrong previous word base must fail this reader even though the two slots share one buffer.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageRecessionPublicationGpuTests.AssertDrawBindings(
            observed, recorded, scene, surface with { PreviousBase = surface.CurrentBase }, evidence.Count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Native_scene_fixture_publishes_every_actor_with_default_submission_capacity_including_holes(int actors)
    {
        using var device = new CoveragePublicationDevice();
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        using var scene = new CoverageRecessionNativeScene(device, fixture, actors, true);
        using var commands = new CommandTallyGpuCommandList(new NullGpuCommandList());
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Prepare(frame); commands.Clear(); commands.Begin(); scene.Render(commands); commands.End();
            scene.Submission.Submit(commands);
            scene.AssertPublished();
            Assert.True(commands.Tally[GpuCommandKind.Dispatch] >= 5);
            Assert.Equal(actors, scene.Owners.Select(o => o.Actor).Distinct().Count());
            Assert.All(scene.Owners, owner => Assert.Equal(frame == 2, owner.Request.Raw));
        }
    }

    [Fact]
    public void Body_size_extents_execute_indexed_refinement_and_gather_without_padding()
    {
        var fixture = new CoverageRecessionBodySizeFixture();
        var t = fixture.Topology;
        Assert.True(t.VertexCount >= 3424); Assert.True(t.PositionGroupCount >= 958);
        Assert.Equal(t.VertexCount, t.Indices.ToArray().Distinct().Count());
        Assert.Equal(fixture.Source.Vertices.Count, fixture.Source.Triangles.SelectMany(face => new[] { face.A, face.B, face.C }).Distinct().Count());
        Assert.Equal(t.PositionGroupCount, t.Vertices.Select(v => v.PositionGroup).Distinct().Count());
        Assert.True(t.Vertices.Count(v => v.SourceVertex < 0) > 0);
        Assert.True(t.Vertices.GroupBy(v => v.PositionGroup).Max(g => g.Count()) > 1);
        Assert.True(t.Indices.Length > t.VertexCount);
        Assert.Equal(fixture.FixtureHash, new CoverageRecessionBodySizeFixture().FixtureHash);
        var raised = new CoverageRecessionBodySizeFixture(20, 18);
        Assert.True(raised.Topology.VertexCount > t.VertexCount);
        Assert.True(raised.Topology.PositionGroupCount > t.PositionGroupCount);
        Assert.NotEqual(fixture.FixtureHash, raised.FixtureHash);
        Assert.Equal(64, fixture.TableHash.Length);
        output.WriteLine("SYNTHETIC_COVERAGE_FIXTURE=" + JsonSerializer.Serialize(fixture.Extents));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(32)]
    [InlineData(96)]
    public void Fixed_batches_keep_every_observation_and_alternate_layout_order(int count)
    {
        var batches = CoverageRecessionCostMeasurement.Schedule(count).ToArray();
        Assert.NotEmpty(batches);
        Assert.Equal(Enumerable.Range(0, count), batches.SelectMany(b => Enumerable.Range(b.Start, b.Count)));
        Assert.All(batches, b => Assert.InRange(b.Count, 1, 3));
        Assert.Equal(Enumerable.Range(0, batches.Length).Select(i => i % 2 != 0), batches.Select(b => b.Reverse));
    }

    [Fact]
    public void Analytic_fixture_freezes_center_recession_and_protected_boundaries()
    {
        var f = new CoverageRecessionBodySizeFixture(4, 4);
        var center = Enumerable.Range(0, f.Topology.VertexCount).First(v => f.RestPosition(v) == Vector3.Zero);
        Assert.Equal(new(0, 0, -.03125f), f.ExpectedPosition(center, 0, Vector3.UnitZ, 1, Vector3.Zero));
        Assert.Equal(new(-.03125f, 0, 0), f.ExpectedPosition(center, 1, Vector3.UnitX, .5f, Vector3.Zero));
        Assert.Equal(Vector3.Zero, f.ExpectedPosition(center, -1, Vector3.UnitZ, 1, Vector3.Zero));
        int[] protectedVertices = Enumerable.Range(0, f.Topology.VertexCount).Where(i => f.Topology.Vertices[i].Strength == 0).ToArray();
        Assert.NotEmpty(protectedVertices);
        Assert.All(protectedVertices, v => Assert.Equal(f.RestPosition(v), f.ExpectedPosition(v, 1, Vector3.UnitX, 1, Vector3.Zero)));
    }

    [Theory]
    [InlineData(0, .5f, 2)]
    [InlineData(1, .5f, 2)]
    [InlineData(1, 0f, 2)]
    [InlineData(-1, 1f, 2)]
    [InlineData(1, .5f, 3)]
    [InlineData(-1, 1f, 3)]
    public void Independent_native_expectations_also_hold_for_headless_carry(int entry, float transition, int level)
    {
        var f = new CoverageRecessionBodySizeFixture(4, 4, level);
        var output = new ModelVertex[f.Topology.VertexCount];
        Vector3 direction = entry == 1 ? Vector3.UnitX : Vector3.UnitZ;
        Vector3 translation = new(.125f, 0, .0625f);
        Assert.True(CoverageCarryReference.TryCarry(f.Topology, [Matrix4x4.CreateTranslation(translation)], f.Table,
            entry, [new(0, direction, transition)], new(f.Topology, 1), output, out var failure), failure.ToString());
        CoverageRecessionComputeGpuTests.AssertWords(f, MemoryMarshal.Cast<ModelVertex, uint>(output.AsSpan()).ToArray(),
            0, entry, direction, transition, translation);
    }
}
