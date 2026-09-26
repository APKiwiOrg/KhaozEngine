using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// THE SEAM THE RIGID MOTION VARIANT STANDS ON (temporal foundations plan, Task D1). The variant reads each keyed
/// instance's previous transform from a read-only structured buffer in the VERTEX stage, at an index that arrives as an
/// instance-rate attribute. No shipped program reads a structured buffer in the vertex stage, so this proves on each
/// backend's own leg that the buffer binds to that stage, that an instance-rate attribute is offset by the draw's first
/// instance, and that the fetched value indexes the buffer.
/// <para>
/// The index is an attribute rather than <c>gl_InstanceIndex</c> because Direct3D 11's <c>SV_InstanceID</c> leaves out
/// the draw's first instance under the pinned SPIRV-Cross options, while Vulkan's <c>gl_InstanceIndex</c> includes it.
/// <see cref="InstanceIndexOnDirect3D11LeavesOutTheFirstInstance"/> pins that reason on every leg.
/// </para>
/// </summary>
public sealed class VertexStageBufferSeamGpuTests
{
    const int Size = 64;
    const int Cells = 8;

    [StructLayout(LayoutKind.Sequential)]
    struct Entry
    {
        public Vector4 Place;    // xy = the cell centre in NDC, z = the half size in NDC
        public Vector4 Colour;
    }

    const string Vert = @"#version 450
struct Entry { vec4 Place; vec4 Colour; };
layout(std430, set=0, binding=0) readonly buffer Entries { Entry E[]; };
layout(location=0) in vec2 Corner;
layout(location=1) in float ISlot;
layout(location=0) out vec4 vColour;
void main() {
    Entry e = E[int(ISlot)];
    gl_Position = vec4(e.Place.xy + Corner * e.Place.z, 0.0, 1.0);
    vColour = e.Colour;
}";

    const string Frag = @"#version 450
layout(location=0) in vec4 vColour;
layout(location=0) out vec4 oColour;
void main() { oColour = vColour; }";

    static readonly Vector4[] Colours =
    {
        new(1f, 0f, 0f, 1f), new(0f, 1f, 0f, 1f), new(0f, 0f, 1f, 1f), new(1f, 1f, 0f, 1f),
        new(1f, 0f, 1f, 1f), new(0f, 1f, 1f, 1f), new(1f, 1f, 1f, 1f), new(1f, .5f, 0f, 1f),
    };

    [GpuFact]
    public void AVertexStageStructuredBufferIsIndexedByAnInstanceRateSlotFromTheFirstInstance()
    {
        using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = ctx.GpuDevice;
        IGpuResourceFactory f = gd.Factory;

        var entries = new Entry[Cells];
        for (int k = 0; k < Cells; k++)
            entries[k] = new Entry { Place = new Vector4(-1f + (2f * k + 1f) / Cells, 0f, .1f, 0f), Colour = Colours[k] };
        Vector2[] corners = { new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f) };
        ushort[] indices = { 0, 1, 2, 0, 2, 3, 0, 0 };        // padded to 16 bytes, six are drawn
        float[] slots = { 7f, 6f, 5f, 4f, 3f, 2f, 1f, 0f };  // instance i reads entry 7 - i

        uint entryBytes = (uint)Marshal.SizeOf<Entry>();
        using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
        using IGpuBuffer entryBuffer = f.CreateBuffer(new GpuBufferDescription(
            Cells * entryBytes, GpuBufferUsage.StructuredBufferReadOnly, entryBytes));
        using IGpuBuffer cornerBuffer = f.CreateBuffer(new GpuBufferDescription(4 * 8, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer slotBuffer = f.CreateBuffer(new GpuBufferDescription(Cells * 4, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer indexBuffer = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.IndexBuffer));
        using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Entries", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, entryBuffer));
        using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { layout },
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new(new GpuVertexElement("Corner", GpuVertexElementFormat.Float2)),
                new(stride: 4, instanceStepRate: 1,
                    elements: new[] { new GpuVertexElement("ISlot", GpuVertexElementFormat.Float1) }),
            },
            Outputs = fb.Outputs,
        });

        using IGpuCommandList cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(VertexStageBufferSeamGpuTests)))
        {
            cl.UpdateBuffer<Entry>(entryBuffer, 0, entries);
            cl.UpdateBuffer<Vector2>(cornerBuffer, 0, corners);
            cl.UpdateBuffer<float>(slotBuffer, 0, slots);
            cl.UpdateBuffer<ushort>(indexBuffer, 0, indices);
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.SetVertexBuffer(0, cornerBuffer);
            cl.SetVertexBuffer(1, slotBuffer);
            cl.SetIndexBuffer(indexBuffer, GpuIndexFormat.UInt16);
            cl.DrawIndexed(6, 3, 0, 0, 4);   // instances 4, 5 and 6 read slots 3, 2 and 1
            cl.DrawIndexed(6, 1, 0, 0, 0);   // instance 0 reads slot 7
        }
        gd.Submit(cl);
        gd.WaitForIdle();

        byte[] rgba = GpuReadback.ToRgba(gd, target, Size, Size);
        int[] drawn = { 1, 2, 3, 7 };
        for (int k = 0; k < Cells; k++)
        {
            int i = ((Size / 2) * Size + (Size / Cells) * k + Size / Cells / 2) * 4;
            var got = new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
            Vector3 want = Array.IndexOf(drawn, k) >= 0
                ? new Vector3(Colours[k].X, Colours[k].Y, Colours[k].Z)
                : Vector3.Zero;
            Assert.True(Vector3.Distance(got, want) < .02f,
                $"cell {k}: read {got}, expected {want}. Cells 5 and 6 lit instead of 1 and 2 means the instance-rate "
                + "slot ignored the draw's first instance. Cells 1 to 3 black means the vertex stage never saw the "
                + "structured buffer.");
        }
    }

    [Fact]
    public void InstanceIndexOnDirect3D11LeavesOutTheFirstInstance()
    {
        const string vert = "#version 450\nlayout(location=0) out float vSlot;\n"
            + "void main() { vSlot = float(gl_InstanceIndex); gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }";
        const string frag = "#version 450\nlayout(location=0) in float vSlot;\nlayout(location=0) out vec4 o;\n"
            + "void main() { o = vec4(vSlot); }";

        CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(vert, frag, "instance index probe");

        // SV_InstanceID with no base term: a draw at first instance 4 would read 0 here and 4 on Vulkan.
        Assert.Contains("SV_InstanceID", pair.VertexSource);
        Assert.DoesNotContain("BaseInstance", pair.VertexSource);
    }
}
