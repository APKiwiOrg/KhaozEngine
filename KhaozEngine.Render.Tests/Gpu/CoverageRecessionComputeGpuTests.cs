using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionComputeGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Analytic_surface_respects_protected_records_selected_rows_and_current_command_directions()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        using var scene = new CoverageRecessionNativeScene(gpu.GpuDevice, fixture, 1, true);
        using var retired = GpuRetireQueue.CreateFrameCounted(gpu.GpuDevice, 3);
        using var batch = new CoverageCarryBatch(gpu.GpuDevice, retired);
        batch.Reserve(1, fixture.Source.Vertices.Count, fixture.Topology.VertexCount, fixture.Topology.TriangleCount);
        using var list = gpu.GpuDevice.Factory.CreateCommandList();
        // Exact permutation, with no trigonometric rounding. Row 2 is the current recession direction +X.
        Matrix4x4 turned = new(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);
        for (int frame = 0; frame < 4; frame++)
        {
            Vector3 translation = new(frame / 16f, 0, frame / 32f);
            scene.Palette[0] = Matrix4x4.CreateTranslation(translation);
            float transition = frame == 3 ? 0 : .5f;
            scene.Submission.Begin(); scene.Scene.BeginFrame(scene.Submission);
            scene.Queue(frame, transition, frame == 1 ? turned : Matrix4x4.Identity);
            var owner = scene.Owners[0];
            Assert.True(batch.TryAcquireOutput(owner, out int slot, out var failure), failure.ToString());
            bool staged = frame == 2 ? batch.TryStageRaw(owner, slot, scene.Palette, Matrix4x4.Identity, out failure)
                : batch.TryStage(owner, owner.Request.EntryIndex, slot, owner.Request.Palette, owner.Request.Model, owner.Uniforms.Commands, out failure);
            Assert.True(staged, failure.ToString());
            list.Begin(); batch.Record(list); list.End(); gpu.GpuDevice.Submit(list); gpu.GpuDevice.WaitForIdle();
            var surface = batch.Surface(owner, slot, -1);
            AssertSurface(gpu.GpuDevice, fixture, surface, frame == 2 ? -1 : frame % 2,
                frame == 1 ? Vector3.UnitX : Vector3.UnitZ, transition, translation);
            batch.AbandonRecording();
        }
        output.WriteLine($"Analytic native carry, {gpu.Backend}, fixture {fixture.FixtureHash}, table {fixture.TableHash}");
        scene.Submission.Dispose();
    }

    [GpuFact]
    public void Frame_N_reads_the_surface_slot_written_by_frame_N_minus_1()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var fixture = new CoverageRecessionBodySizeFixture(4, 4);
        using var scene = new CoverageRecessionNativeScene(gpu.GpuDevice, fixture, 1, true);
        using var a = gpu.GpuDevice.Factory.CreateCommandList();
        using var b = gpu.GpuDevice.Factory.CreateCommandList();
        uint bytes = checked((uint)fixture.Topology.VertexCount * 64);
        using var probe = gpu.GpuDevice.Factory.CreateBuffer(new(bytes, GpuBufferUsage.StructuredBufferReadWrite, 4));
        // Bring up every pass and its cold resources before the dependency pair. A cold setup drain
        // between the two submissions would hide the cross-frame hazard this fact must exercise.
        for (int warm = 0; warm < 3; warm++)
        {
            scene.Prepare(warm); a.Begin(); scene.Render(a); a.End(); scene.Submission.Submit(a);
        }
        gpu.GpuDevice.WaitForIdle();
        scene.Prepare(0); a.Begin(); scene.Render(a); a.End(); scene.Submission.Submit(a);
        var written = scene.Owners[0].Request.Surface;
        // A refused device has no covered surface, and the dependency below would otherwise fail obscurely.
        Assert.NotNull(written.Current);
        // Deliberately no wait or readback between submissions. Motion shaders and this copy read N-1 in N.
        scene.Palette[0] = Matrix4x4.CreateTranslation(0, 0, .125f);
        scene.Prepare(1); b.Begin(); scene.Render(b);
        var read = scene.Owners[0].Request.Surface;
        Assert.Same(written.Current, read.Previous); Assert.Equal(written.CurrentBase, read.PreviousBase);
        b.CopyBuffer(read.Previous, read.PreviousBase * 4, probe, 0, bytes);
        b.End(); scene.Submission.Submit(b); gpu.GpuDevice.WaitForIdle();
        AssertSurface(gpu.GpuDevice, fixture, new(probe, probe, read.Indices, 0, 0,
            read.VertexCount, read.IndexCount), 0, Vector3.UnitZ, 1, Vector3.Zero);
        AssertSurface(gpu.GpuDevice, fixture, read, 1, Vector3.UnitZ, 1, new(0, 0, .125f));
        Assert.NotEqual(read.CurrentBase, read.PreviousBase);
        output.WriteLine($"Cross-submission N reads N-1, {gpu.Backend}, {gpu.GpuDevice.Capabilities.DeviceName}, fixture {fixture.FixtureHash}");
        scene.Submission.Dispose();
    }

    internal static void AssertSurface(IGpuDevice device, CoverageRecessionBodySizeFixture fixture,
        CoverageSurfaceRange surface, int entry, Vector3 direction, float transition, Vector3 translation)
    {
        uint[] all = GpuReadback.ReadBuffer<uint>(device, surface.Current,
            checked((int)surface.CurrentBase + surface.VertexCount * 16));
        AssertWords(fixture, all, surface.CurrentBase, entry, direction, transition, translation);
    }

    internal static void AssertWords(CoverageRecessionBodySizeFixture fixture, uint[] all, uint currentBase,
        int entry, Vector3 direction, float transition, Vector3 translation)
    {
        Assert.True(fixture.Topology.VertexCount > 0);
        Assert.Equal(checked((int)currentBase + fixture.Topology.VertexCount * 16), all.Length);
        var reference = new ModelVertex[fixture.Topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, [Matrix4x4.CreateTranslation(translation)], fixture.Table, entry,
            entry < 0 ? [] : [new(0, direction, transition)], new(fixture.Topology, 1), reference, out var failure), failure.ToString());
        CoverageDirectionAssertions.EqualRecords(MemoryMarshal.Cast<ModelVertex, uint>(reference.AsSpan()), all.AsSpan((int)currentBase));
        int changed = 0, protectedCount = 0;
        int directionWitnesses = 0;
        var ramp = CoverageRecessionBodySizeFixture.LeftRampDirections(entry, direction, transition);
        for (int vertex = 0; vertex < fixture.Topology.VertexCount; vertex++)
        {
            int first = (int)currentBase + vertex * 16;
            Vector3 actual = new(F(0), F(1), F(2));
            Vector3 expected = fixture.ExpectedPosition(vertex, entry, direction, transition, translation);
            Assert.True(Vector3.Distance(actual, expected) < 2e-6f, $"Vertex {vertex}, expected {expected}, actual {actual}");
            Vector3 normal = new(F(3), F(4), F(5));
            Vector3 tangent = new(F(12), F(13), F(14));
            Assert.InRange(normal.Length(), .9999f, 1.0001f);
            Assert.InRange(tangent.Length(), .9999f, 1.0001f);
            Assert.True(normal.Z > 0);
            Assert.Equal(-1f, F(15));
            Assert.Equal(.75f, F(6)); Assert.Equal(.5f, F(7)); Assert.Equal(.25f, F(8)); Assert.Equal(1f, F(9));
            Vector3 rest = fixture.RestPosition(vertex);
            // Refinement level two or higher keeps this witness's entire one-ring on the same
            // affine tent ramp. Every incident face therefore has these analytic directions.
            if (fixture.Width >= 4 && fixture.Height >= 4 && fixture.Level >= 2 && rest == fixture.LeftRampWitness)
            {
                directionWitnesses++;
                CoverageDirectionAssertions.Equal(ramp.Normal, normal);
                CoverageDirectionAssertions.Equal(ramp.Tangent, tangent);
            }
            Assert.InRange(MathF.Abs(rest.X + fixture.Width / 32f - F(10)), 0, 2e-6f);
            Assert.InRange(MathF.Abs(rest.Y + fixture.Height / 32f - F(11)), 0, 2e-6f);
            if (fixture.Topology.Vertices[vertex].Strength == 0)
            {
                protectedCount++;
                // Thirds have different valid binary32 interpolation roundings. The analytic bound above
                // covers generated thirds, while source records and dyadic refinement retain exact positions.
                if (fixture.Topology.Vertices[vertex].SourceVertex >= 0 || fixture.Level is 1 or 2 or 4)
                    Assert.Equal(rest + translation, actual);
                CoverageDirectionAssertions.Equal(Vector3.UnitZ, normal); CoverageDirectionAssertions.Equal(Vector3.UnitX, tangent);
            }
            if (Vector3.Distance(actual, rest + translation) > 2e-6f) changed++;
            float F(int word) => BitConverter.UInt32BitsToSingle(all[first + word]);
        }
        Assert.True(protectedCount > 0);
        if (fixture.Width >= 4 && fixture.Height >= 4 && fixture.Level >= 2) Assert.True(directionWitnesses > 0);
        if (entry >= 0 && transition != 0) Assert.True(changed > 0);
        else Assert.Equal(0, changed);
        // Frozen center of this dyadic 4-by-4 fixture has a 0.03125 / 0.0625 recession.
        if (fixture.Width == 4 && fixture.Height == 4 && entry >= 0)
        {
            int center = Enumerable.Range(0, fixture.Topology.VertexCount).First(i => fixture.RestPosition(i) == Vector3.Zero);
            Assert.Equal(entry == 0 ? .03125f : .0625f, fixture.Table.Entry(entry)[fixture.Topology.Vertices[center].PositionGroup]);
        }
    }
}
