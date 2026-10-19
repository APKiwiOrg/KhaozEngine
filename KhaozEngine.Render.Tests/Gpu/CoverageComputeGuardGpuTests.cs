using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeGuardGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Parallel_notes_reduce_by_reference_key_not_writer_order()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var original = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        original[0] = original[0] with { Id = 20, EquipmentInstanceId = 0x1234567800000020 };
        original[1] = original[1] with { Id = 10, EquipmentInstanceId = 0x8765432100000010 };
        var cases = new[] { original, original.Reverse().ToArray(), original.ToArray() }
            .Select(c => new CoverageReferenceCase("ordered", topology, [], [], fixture.Posed, Matrix4x4.Identity, c)).ToArray();
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        // Actual membership fails independently at all three corners. Both slots have faces in multiple workgroups.
        for (int actor = 0; actor < 2; actor++)
            for (int slot = 0; slot < 2; slot++) scratch[actor].Frames[slot] = scratch[actor].Frames[slot] with { HostToActualSheet = Matrix4x4.CreateTranslation(10, 0, 0) };
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var s = scratch[actor];
            Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, c.Commands, s, out _));
            bool accepted = CoverageReferenceCorrection.TryApply(topology, c.Commands, s, out var failure);
            Failure(failure, session, actor);
            Assert.Equal(actor == 2, accepted);
            Assert.Equal(actor == 2 ? 0u : 1u, session.ReadHeader(actor)[30]);
            if (actor == 2) continue;
            Assert.Equal(10u, failure.CommandId);
            Assert.Equal(0u, session.ReadHeader(actor)[27]);
            Assert.Equal(7u, session.ReadHeader(actor)[29]);
            int[] faces = Enumerable.Range(0, topology.TriangleCount).Where(t => topology.Triangles[t].Role == CoverageTriangleRole.Core
                && topology.Triangles[t].RegionIndex == 1).ToArray();
            Assert.True(faces.Select(t => t / 64).Distinct().Count() > 1);
            Assert.Equal(faces.Length * 3, failure.Count);
            uint[] events = session.ReadWords(actor, 24);
            int notes = Enumerable.Range(0, topology.TriangleCount * 3).Count(i => events[i * 6] != 0);
            int notesLostWhenPeerFlagsRefusal = topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core) * 3 - notes;
            Assert.Equal(0, notesLostWhenPeerFlagsRefusal);
        }
        // A fresh frame on the same actor storage clears both atomic and completed refusal state.
        for (int actor = 0; actor < 2; actor++)
        {
            scratch[actor] = Prepare(cases[actor]);
            session.UploadCommands(actor, cases[actor].Commands, scratch[actor]);
        }
        Execute(gpu.GpuDevice, pipeline, session);
        Assert.Equal(0u, session.ReadHeader(0)[30]);
        Assert.Equal(0u, session.ReadHeader(1)[30]);
    }

    [GpuFact]
    public void Conformation_uses_representative_order_and_preserves_completed_failure()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        commands[0] = commands[0] with { Id = 20, MaxWorldRecession = 0 };
        commands[1] = commands[1] with { Id = 10, MaxWorldRecession = 0 };
        var cases = new[] { commands, commands.Reverse().ToArray() }.Select(c =>
            new CoverageReferenceCase("point-order", topology, [], [], fixture.Posed, Matrix4x4.Identity, c)).ToArray();
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, cases[actor].Commands, scratch[actor], out var failure));
            Assert.Equal(20u, failure.CommandId);
            Failure(failure, session, actor);
            Assert.Equal(6u, session.ReadHeader(actor)[29]);
            Assert.Equal(1u, session.ReadHeader(actor)[27]);
            Assert.Equal(1u, session.ReadHeader(actor)[30]);
            Assert.All(session.ReadWords(actor, 20), word => Assert.Equal(0u, word));
            Assert.Equal(Words(scratch[actor].FullRecessions), session.ReadWords(actor, 14));
            Assert.Equal(Words(scratch[actor].GroupPositions), session.ReadWords(actor, 12));
            uint[] events = session.ReadWords(actor, 24);
            int[] writers = Enumerable.Range(0, topology.PositionGroupCount).Where(g => events[g * 6] != 0).ToArray();
            Assert.True(writers.Select(g => g / 64).Distinct().Count() > 1);
        }
    }

    [GpuFact]
    public void Full_target_budget_is_checked_at_zero_transition()
    {
        var original = CoverageReferenceCases.Get("curved-ordinary-seams-l3-w0");
        var s = Prepare(original, conform: true);
        float point = s.FullRecessions.Max();
        Assert.True(CoverageReferenceCorrection.TryApply(original.Topology, original.Commands, s, out _));
        float full = s.FullRecessions.Max();
        Assert.True(full > point);
        var c = original with { Commands = original.Commands.Select(x => x with { MaxWorldRecession = (point + full) * .5f }).ToArray() };
        s = Prepare(c);
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, [c], [s]);
        session.UploadPosed(c.Posed, 0);
        Execute(gpu.GpuDevice, pipeline, session);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, s, out _));
        Assert.False(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, s, out var failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);
        Failure(failure, session, 0);
        Assert.Equal(8u, session.ReadHeader(0)[29]);
        Assert.Equal(0u, session.ReadHeader(0)[27]);
    }
}
