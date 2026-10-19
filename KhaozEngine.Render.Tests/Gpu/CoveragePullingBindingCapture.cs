using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoveragePullingBindingCapture(UniformBufferTrackingGpuDevice tracker)
{
    internal sealed record BoundDraw(string Pass, IGpuResourceSet Set, IGpuBuffer Current, IGpuBuffer Static,
        IGpuBuffer Uniform, uint DynamicOffset, uint RangeOffset, uint RangeBytes, uint[] Words,
        uint Corners, uint Instances, uint FirstVertex, uint FirstInstance);
    internal List<BoundDraw> Draws { get; } = new();

    internal void Observe(RecordingGpuCommandList commands, RecordingGpuCommandList.DrawBindings bindings,
        uint corners, uint instances, uint firstVertex, uint firstInstance)
    {
        if (bindings.Pipeline is not { } pipeline || tracker.ProgramOf(pipeline) is not { } program) return;
        string pass;
        if (program.Vertex == ShaderSources.CoverageModelVert) pass = "main";
        else if (program.Vertex == ShaderSources.CoverageOutlineVert)
        {
            Assert.True(program.Fragment == ShaderSources.TargetOutlineFullMaskFrag
                || program.Fragment == ShaderSources.TargetOutlineVisibleMaskFrag);
            pass = program.Fragment == ShaderSources.TargetOutlineFullMaskFrag ? "full" : "visible";
        }
        else return;
        Assert.Equal(0, program.VertexLayouts);
        Assert.Equal(3, program.Layouts.Length);
        var bound = bindings.Sets[2];
        var resources = tracker.ResourcesOf(bound.Set);
        Assert.Same(program.Layouts[2], resources.Layout);
        Assert.Equal(3, resources.Resources.Length);
        var current = Assert.IsAssignableFrom<IGpuBuffer>(resources.Resources[0]);
        var staticData = Assert.IsAssignableFrom<IGpuBuffer>(resources.Resources[1]);
        var range = Assert.IsType<GpuBufferRange>(resources.Resources[2]);
        var window = Assert.Single(tracker.WindowsOf(bound.Set));
        Assert.True(window.Dynamic);
        Assert.Same(range.Buffer, window.Buffer);
        Assert.Equal(range.Offset, window.Offset);
        Assert.Equal(16u, window.Bytes);
        uint address = window.Rebased(bound.DynamicOffset).Offset;
        var upload = commands.Uploads.Last(u => ReferenceEquals(u.Buffer, range.Buffer)
            && u.Offset <= address && (ulong)u.Offset + u.Bytes >= (ulong)address + 16);
        Assert.NotNull(upload.Data);
        uint[] words = MemoryMarshal.Cast<byte, uint>(upload.Data.AsSpan(checked((int)(address - upload.Offset)), 16)).ToArray();
        Draws.Add(new(pass, bound.Set, current, staticData, range.Buffer, bound.DynamicOffset, range.Offset,
            range.Size, words, corners, instances, firstVertex, firstInstance));
    }

    internal void AssertMatches(CoveragePullingTestScene.Actor[] actors, CoverageGpuBatch batch, bool outlineOnly)
    {
        Assert.Equal(outlineOnly ? 8 : 12, Draws.Count);
        foreach (string pass in new[] { "main", "full", "visible" })
        {
            var actual = Draws.Where(d => d.Pass == pass).ToArray();
            if (pass == "main" && outlineOnly) { Assert.Empty(actual); continue; }
            Assert.Equal(4, actual.Length);
            for (int i = 0; i < actual.Length; i++)
            {
                var reservation = actors[i / 2].Frame.Preparation!.GpuReservation;
                var range = actors[i / 2].Topology.MaterialRanges[i % 2];
                var draw = actual[i];
                Assert.Same(batch.Current, draw.Current);
                Assert.Same(batch.StaticData, draw.Static);
                Assert.Equal((uint)i * 256, draw.DynamicOffset);
                Assert.Equal(0u, draw.RangeOffset);
                Assert.Equal(16u, draw.RangeBytes);
                Assert.Equal(new[] { reservation.CurrentBase, reservation.StaticBase, (uint)range.FirstIndex,
                    reservation.PreviousBase }, draw.Words);
                Assert.Equal((uint)range.IndexCount, draw.Corners);
                Assert.Equal(1u, draw.Instances);
                Assert.Equal(0u, draw.FirstVertex);
                Assert.Equal(0u, draw.FirstInstance);
            }
        }
    }

    internal string Json() => JsonSerializer.Serialize(Draws.Select(d => new
    {
        d.Pass,
        Set = RuntimeHelpers.GetHashCode(d.Set),
        Current = RuntimeHelpers.GetHashCode(d.Current),
        Static = RuntimeHelpers.GetHashCode(d.Static),
        Uniform = RuntimeHelpers.GetHashCode(d.Uniform),
        d.DynamicOffset,
        d.RangeOffset,
        d.RangeBytes,
        d.Words,
        d.Corners,
        d.Instances,
        d.FirstVertex,
        d.FirstInstance,
    }), new JsonSerializerOptions { WriteIndented = true });
}
