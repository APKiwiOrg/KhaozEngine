using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Reads actual command-bound resource sets and the upload image visible at each draw ordinal.
/// The fixture owns these inert resources. No preparation metadata or logical-slot callback is consulted.</summary>
internal sealed class CoverageRecordedBindings(CoverageMultiActorFixture fixture)
{
    internal readonly record struct Output(IGpuBuffer Buffer, uint Base, int Vertices);
    internal sealed record Draw(string Pass, Matrix4x4 Model, IGpuBuffer Current, IGpuBuffer Previous,
        IGpuBuffer Indices, uint CurrentBase, uint PreviousBase, int FirstIndex, uint Count,
        IGpuResourceSet? Material, Vector4 Tint, Vector4 Emissive, Vector4 Specular,
        Matrix4x4 PreviousModel, GpuPipelineDescription Pipeline);

    internal Dictionary<MotionKey, Output> Outputs()
    {
        var actorsByFrames = new Dictionary<IGpuBuffer, MotionKey>();
        int groupCount = fixture.Root.Host.Table.PositionGroupCount;
        int marker = fixture.Root.Host.Table.Representatives.ToArray().Select((r, i) => (r, i)).First(p => p.r >= 0).i;
        foreach (var dispatch in fixture.Commands.ComputeBindings)
        {
            if (fixture.Root.Device.ComputeSource(dispatch.Pipeline) != CoverageCarryShaders.Kernels[1].Source) continue;
            var resources = Set(dispatch.Sets[0].Set).Resources;
            var table = MemoryMarshal.Cast<byte, uint>(fixture.Root.Device.InitialBytes((IGpuBuffer)resources[4]));
            uint value = table[groupCount + marker];
            var actor = value == BitConverter.SingleToUInt32Bits(fixture.First.Table.Entry(0)[marker])
                ? fixture.First : fixture.Second;
            Assert.Equal(BitConverter.SingleToUInt32Bits(actor.Table.Entry(0)[marker]), value);
            actorsByFrames.Add((IGpuBuffer)resources[1], actor.Key);
        }
        var outputs = new Dictionary<MotionKey, Output>();
        foreach (var dispatch in fixture.Commands.ComputeBindings)
        {
            if (fixture.Root.Device.ComputeSource(dispatch.Pipeline) != CoverageCarryShaders.Kernels[4].Source) continue;
            var resources = Set(dispatch.Sets[0].Set).Resources;
            var frames = (IGpuBuffer)resources[1];
            uint[] words = MemoryMarshal.Cast<byte, uint>(Read(frames, 0, frames.SizeInBytes, dispatch.DrawOrdinal)).ToArray();
            Assert.Equal(1u, words[0]); // This fixture has one actor per independently marked table group.
            int actorFrame = checked((int)words[5]);
            outputs.Add(actorsByFrames[frames], new((IGpuBuffer)resources[4], words[actorFrame + 2], (int)words[actorFrame + 6]));
        }
        return outputs;
    }

    internal Draw[] Draws()
    {
        var result = new List<Draw>();
        foreach (var draw in fixture.Commands.Bindings)
        {
            if (draw.Pipeline is not FakePipeline { Request: { } request }) continue;
            var kind = Classify(request.VertexGlsl, request.FragmentGlsl);
            if (kind is null) continue;
            var (pass, pullingSet, modelSet, modelElement, modelOffset) = kind.Value;
            var pulling = draw.Sets[pullingSet];
            var resources = Set(pulling.Set).Resources;
            byte[] header = Uniform(draw, pullingSet, 2);
            uint[] words = MemoryMarshal.Cast<byte, uint>(header).ToArray();
            byte[] model = Uniform(draw, modelSet, modelElement);
            bool main = pass is "motion" or "colour-depth";
            IGpuResourceSet? material = main || pass is "full" or "visible" ? draw.Sets[1].Set : null;
            result.Add(new(pass, MemoryMarshal.Read<Matrix4x4>(model.AsSpan(modelOffset)),
                (IGpuBuffer)resources[0], (IGpuBuffer)resources[3], (IGpuBuffer)resources[1],
                words[0], words[3], (int)words[2], draw.ElementCount, material,
                main ? MemoryMarshal.Read<Vector4>(model.AsSpan(64)) : default,
                main ? MemoryMarshal.Read<Vector4>(model.AsSpan(80)) : default,
                main ? MemoryMarshal.Read<Vector4>(model.AsSpan(96)) : default,
                pass == "motion" ? MemoryMarshal.Read<Matrix4x4>(header.AsSpan(16)) : default, request.Description));
        }
        return result.ToArray();
    }

    internal void AssertActor(Draw draw, CoverageMultiActorFixture.Actor actor, Output current, Output previous,
        Matrix4x4 previousModel)
    {
        Assert.Equal(actor.Model, draw.Model);
        Assert.Same(current.Buffer, draw.Current); Assert.Equal(current.Base, draw.CurrentBase);
        Assert.Same(previous.Buffer, draw.Previous); Assert.Equal(previous.Base, draw.PreviousBase);
        int slice = Array.FindIndex(fixture.Root.Host.Topology.MaterialRanges.ToArray(), r => r.FirstIndex == draw.FirstIndex);
        Assert.True(slice >= 0);
        Assert.Equal((uint)fixture.Root.Host.Topology.MaterialRanges[slice].IndexCount, draw.Count);
        Assert.Equal(fixture.Root.Host.Topology.Indices.ToArray(),
            MemoryMarshal.Cast<byte, uint>(fixture.Root.Device.InitialBytes(draw.Indices)).ToArray());
        if (draw.Pass is "motion" or "colour-depth")
        {
            Assert.Same(fixture.Scene.SkinnedGpuMaterialSetAt(actor.Meshes[slice]), draw.Material);
            Assert.Equal((Vector4)actor.Tint(slice), draw.Tint);
            var material = actor.Material(slice);
            Assert.Equal((Vector4)material.Emissive, draw.Emissive);
            Assert.Equal(new Vector4(material.Specular, material.Shininess, 0, 0), draw.Specular);
            Assert.True(draw.Pipeline.DepthStencil.DepthTestEnabled && draw.Pipeline.DepthStencil.DepthWriteEnabled);
            Assert.True(draw.Pipeline.Outputs.Colour.Length >= 3); // The same draw writes colour and depth colour.
            if (draw.Pass == "motion")
            {
                Assert.Contains(MotionMath.Format, draw.Pipeline.Outputs.Colour);
                Assert.Equal(previousModel, draw.PreviousModel);
            }
        }
        else if (draw.Pass is "full" or "visible")
            Assert.Same(fixture.Scene.SkinnedOutlineMaterialSetAt(actor.Meshes[slice]), draw.Material);
        else
        {
            Assert.Null(draw.Material); // Depth caster programs have no material texture layout.
            Assert.DoesNotContain(draw.Pipeline.ResourceLayouts.SelectMany(l => ((FakeResourceLayout)l).Description.Elements),
                e => e.Kind == GpuResourceKind.TextureReadOnly);
        }
        if (draw.Material is { } set) Assert.Same(actor.Textures[slice], Set(set).Resources[0]);
    }

    byte[] Uniform(RecordingGpuCommandList.DrawBindings draw, uint setIndex, int element)
    {
        var binding = draw.Sets[setIndex];
        var range = Assert.IsType<GpuBufferRange>(Set(binding.Set).Resources[element]);
        return Read(range.Buffer, checked(range.Offset + binding.DynamicOffset), range.Size, draw.DrawOrdinal);
    }

    byte[] Read(IGpuBuffer buffer, uint offset, uint size, int ordinal)
    {
        var bytes = fixture.Root.Device.InitialBytes(buffer).Slice((int)offset, (int)size).ToArray();
        foreach (var upload in fixture.Commands.Uploads)
        {
            if (!ReferenceEquals(upload.Buffer, buffer) || upload.DrawsBefore > ordinal) continue;
            long first = Math.Max(offset, upload.Offset), last = Math.Min((long)offset + size, (long)upload.Offset + upload.Bytes);
            if (last <= first) continue;
            Assert.NotNull(upload.Data);
            upload.Data.AsSpan((int)(first - upload.Offset), (int)(last - first)).CopyTo(bytes.AsSpan((int)(first - offset)));
        }
        return bytes;
    }

    static FakeResourceSet Set(IGpuResourceSet set) => Assert.IsType<FakeResourceSet>(set);
    static (string Pass, uint Pulling, uint ModelSet, int ModelElement, int ModelOffset)? Classify(string vertex, string fragment)
    {
        if (vertex == ShaderSources.CoverageModelMotionVert) return ("motion", 2, 0, 3, 0);
        if (vertex == ShaderSources.CoverageModelVert) return ("colour-depth", 2, 0, 3, 0);
        if (vertex == ShaderSources.CoverageCascadeVert || vertex == ShaderSources.CoverageCascadeDissolveVert) return ("cascade", 1, 0, 0, 64);
        if (vertex == ShaderSources.CoveragePointVert || vertex == ShaderSources.CoveragePointDissolveVert) return ("point", 2, 1, 0, 0);
        if (vertex == ShaderSources.CoverageOutlineVert)
            return (fragment == ShaderSources.TargetOutlineFullMaskFrag ? "full" : "visible", 2, 0, 0, 64);
        return null;
    }
}
