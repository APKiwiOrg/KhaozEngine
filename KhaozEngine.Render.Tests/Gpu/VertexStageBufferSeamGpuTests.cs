using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
/// <see cref="TheRigidMotionVariantsTwoSetShapeIndexesTheBufferFromTheSlotAtLocationFifteen"/> repeats the proof in the
/// exact binding shape Task D5 builds: the model layout at set 0, the motion block and the buffer at set 1, the model
/// pass's vertex and instance streams at slots 0 and 1, and the slot at location 15 on slot 2. On Direct3D 11 that
/// puts the buffer at vertex register t8, which
/// <see cref="TheRigidMotionShapePutsThePreviousTransformsAtVertexRegisterT8OnDirect3D11"/> pins on every leg.
/// </para>
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

    // ModelMotionVert's shape: ModelVert's inputs at locations 0 to 14, the motion block and the previous transforms at
    // set 1, and the motion slot at location 15. Frame.x is 1 and ColourScale doubles the half-bright entry colours,
    // so an unread uniform block collapses or blackens every cell.
    const string RigidVert = @"#version 450
layout(set=0, binding=0) uniform U { vec4 Frame; };
layout(set=1, binding=0) uniform MotionFrame { vec4 ColourScale; };
struct Entry { vec4 Place; vec4 Colour; };
layout(std430, set=1, binding=1) readonly buffer PreviousInstanceTransforms { Entry E[]; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;
layout(location=5) in vec4 IModel0;
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=12) in float IDynamic;
layout(location=13) in vec2 IDissolve;
layout(location=14) in float IDissolveComplement;
layout(location=15) in float IMotionSlot;
layout(location=0) out vec4 vColour;
void main() {
    Entry e = E[int(IMotionSlot)];
    // Every input but Position.xy is zero. Each one is read so no location drops out of the input signature.
    vec4 zero = vec4(Normal, IDynamic) + Color + vec4(TexCoord, IDissolve) + Tangent + IModel0 + IModel1 + IModel2
        + IModel3 + ITint + IEmissive + ISpecParams + vec4(Position.z, IDissolveComplement, 0.0, 0.0);
    gl_Position = vec4(e.Place.xy + Position.xy * e.Place.z * Frame.x, 0.0, 1.0) + zero;
    vColour = e.Colour * ColourScale;
}";

    // ModelFrag's set 0, binding for binding, every resource read in live code. The front end optimises at Performance
    // (SpirvFrontEndPin.Optimization) and strips a resource the program only reads at a constant zero weight, and the
    // Direct3D 11 registers are numbered over what survives. So each read here is zero only at run time: the map
    // texel, both buffers and Frame.y are uploaded as zero.
    const string RigidFrag = @"#version 450
layout(set=0, binding=0) uniform U { vec4 Frame; };
layout(std430, set=0, binding=1) readonly buffer PointLightBuffer { uvec4 PointLights[]; };
layout(std430, set=0, binding=2) readonly buffer PointLightClusterBuffer { uvec4 PointLightClusters[]; };
layout(set=0, binding=3) uniform texture2D Albedo;
layout(set=0, binding=4) uniform texture2D NormalMap;
layout(set=0, binding=5) uniform texture2D RoughnessMap;
layout(set=0, binding=6) uniform sampler Samp;
layout(set=0, binding=7) uniform texture2D ShadowMap;
layout(set=0, binding=8) uniform sampler ShadowSamp;
layout(set=0, binding=9) uniform texture2D PointShadowMap;
layout(set=0, binding=10) uniform texture2D PointShadowTransientMap;
layout(location=0) in vec4 vColour;
layout(location=0) out vec4 oColour;
void main() {
    vec2 uv = vec2(0.5);
    vec4 maps = textureLod(sampler2D(Albedo, Samp), uv, 0.0) + textureLod(sampler2D(NormalMap, Samp), uv, 0.0)
        + textureLod(sampler2D(RoughnessMap, Samp), uv, 0.0) + textureLod(sampler2D(ShadowMap, ShadowSamp), uv, 0.0)
        + textureLod(sampler2D(PointShadowMap, ShadowSamp), uv, 0.0)
        + textureLod(sampler2D(PointShadowTransientMap, ShadowSamp), uv, 0.0);
    float lights = float(PointLights[0].x + PointLightClusters[0].x);
    oColour = vColour + maps + vec4(lights + Frame.y);
}";

    static readonly Vector4[] Colours =
    {
        new(1f, 0f, 0f, 1f), new(0f, 1f, 0f, 1f), new(0f, 0f, 1f, 1f), new(1f, 1f, 0f, 1f),
        new(1f, 0f, 1f, 1f), new(0f, 1f, 1f, 1f), new(1f, 1f, 1f, 1f), new(1f, .5f, 0f, 1f),
    };

    static readonly Vector2[] Corners = { new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f) };
    static readonly ushort[] Indices = { 0, 1, 2, 0, 2, 3, 0, 0 };        // padded to 16 bytes, six are drawn
    static readonly float[] Slots = { 7f, 6f, 5f, 4f, 3f, 2f, 1f, 0f };  // instance i reads entry 7 - i
    static readonly int[] Drawn = { 1, 2, 3, 7 };                         // instances 4 to 6 and 0

    [GpuFact]
    public void AVertexStageStructuredBufferIsIndexedByAnInstanceRateSlotFromTheFirstInstance()
    {
        using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = ctx.GpuDevice;
        IGpuResourceFactory f = gd.Factory;

        Entry[] entries = Entries(colourScale: 1f);
        uint entryBytes = (uint)Marshal.SizeOf<Entry>();
        using IGpuTexture target = CreateTarget(f);
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
        using IGpuPipeline pipeline = f.CreateGraphicsPipeline(Pipeline(shaders, fb, new[] { layout },
            new GpuVertexLayoutDescription(new GpuVertexElement("Corner", GpuVertexElementFormat.Float2)),
            new GpuVertexLayoutDescription(stride: 4, instanceStepRate: 1,
                elements: new[] { new GpuVertexElement("ISlot", GpuVertexElementFormat.Float1) })));

        using IGpuCommandList cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(VertexStageBufferSeamGpuTests)))
        {
            cl.UpdateBuffer<Entry>(entryBuffer, 0, entries);
            cl.UpdateBuffer<Vector2>(cornerBuffer, 0, Corners);
            cl.UpdateBuffer<float>(slotBuffer, 0, Slots);
            cl.UpdateBuffer<ushort>(indexBuffer, 0, Indices);
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

        AssertRow(GpuReadback.ToRgba(gd, target, Size, Size), "one set, slot 1");
    }

    [GpuFact]
    public void TheRigidMotionVariantsTwoSetShapeIndexesTheBufferFromTheSlotAtLocationFifteen()
    {
        using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = ctx.GpuDevice;
        IGpuResourceFactory f = gd.Factory;

        Entry[] entries = Entries(colourScale: .5f);
        var vertices = new float[4 * 16];     // Position, Normal, Color, TexCoord and Tangent are 16 floats
        for (int v = 0; v < 4; v++)
            (vertices[v * 16], vertices[v * 16 + 1]) = (Corners[v].X, Corners[v].Y);
        var instances = new float[Cells * 32];  // the model pass's 128-byte instance row, all zero
        uint entryBytes = (uint)Marshal.SizeOf<Entry>();

        using IGpuTexture target = CreateTarget(f);
        using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
        using IGpuBuffer frame = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
        using IGpuBuffer pointLights = f.CreateBuffer(new GpuBufferDescription(
            48, GpuBufferUsage.StructuredBufferReadOnly, 48));
        using IGpuBuffer clusters = f.CreateBuffer(new GpuBufferDescription(
            16, GpuBufferUsage.StructuredBufferReadOnly, 16));
        using IGpuTexture map = f.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        using IGpuSampler sampler = f.CreateSampler(GpuSamplerDescription.Point);
        using IGpuBuffer motionFrame = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
        using IGpuBuffer entryBuffer = f.CreateBuffer(new GpuBufferDescription(
            Cells * entryBytes, GpuBufferUsage.StructuredBufferReadOnly, entryBytes));
        using IGpuBuffer vertexBuffer = f.CreateBuffer(new GpuBufferDescription(4 * 64, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer instanceBuffer = f.CreateBuffer(new GpuBufferDescription(
            Cells * 128, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer slotBuffer = f.CreateBuffer(new GpuBufferDescription(Cells * 4, GpuBufferUsage.VertexBuffer));
        using IGpuBuffer indexBuffer = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.IndexBuffer));
        gd.UpdateTexture(map, new byte[4], 0, 0, 1, 1);

        using IGpuShaderSet shaders = f.CreateShadersFromSpirv(RigidVert, RigidFrag);
        // ModelRenderer's model layout, element for element. Its one b and eight t registers are what move the
        // motion set to b1 and t8 on Direct3D 11.
        using IGpuResourceLayout modelLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer,
                GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            FragmentOnly("PointLights", GpuResourceKind.StructuredBufferReadOnly),
            FragmentOnly("PointLightClusters", GpuResourceKind.StructuredBufferReadOnly),
            FragmentOnly("Albedo", GpuResourceKind.TextureReadOnly),
            FragmentOnly("NormalMap", GpuResourceKind.TextureReadOnly),
            FragmentOnly("RoughnessMap", GpuResourceKind.TextureReadOnly),
            FragmentOnly("Sampler", GpuResourceKind.Sampler),
            FragmentOnly("ShadowMap", GpuResourceKind.TextureReadOnly),
            FragmentOnly("ShadowSamp", GpuResourceKind.Sampler),
            FragmentOnly("PointShadowMap", GpuResourceKind.TextureReadOnly),
            FragmentOnly("PointShadowTransientMap", GpuResourceKind.TextureReadOnly)));
        // The rigid variant's set 1 as ModelMotionResources declares it, vertex stage only.
        using IGpuResourceLayout motionLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("PreviousInstanceTransforms", GpuResourceKind.StructuredBufferReadOnly,
                GpuShaderStages.Vertex)));
        using IGpuResourceSet modelSet = f.CreateResourceSet(new GpuResourceSetDescription(modelLayout,
            frame, pointLights, clusters, map, map, map, sampler, map, sampler, map, map));
        using IGpuResourceSet motionSet = f.CreateResourceSet(new GpuResourceSetDescription(
            motionLayout, motionFrame, entryBuffer));
        using IGpuPipeline pipeline = f.CreateGraphicsPipeline(Pipeline(shaders, fb, new[] { modelLayout, motionLayout },
            new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4)),
            new GpuVertexLayoutDescription(stride: 128, instanceStepRate: 1, elements: new GpuVertexElement[]
            {
                new("IModel0", GpuVertexElementFormat.Float4), new("IModel1", GpuVertexElementFormat.Float4),
                new("IModel2", GpuVertexElementFormat.Float4), new("IModel3", GpuVertexElementFormat.Float4),
                new("ITint", GpuVertexElementFormat.Float4), new("IEmissive", GpuVertexElementFormat.Float4),
                new("ISpecParams", GpuVertexElementFormat.Float4), new("IDynamic", GpuVertexElementFormat.Float1),
                new("IDissolve", GpuVertexElementFormat.Float2), new("IDissolveComplement", GpuVertexElementFormat.Float1),
            }),
            new GpuVertexLayoutDescription(stride: 4, instanceStepRate: 1,
                elements: new[] { new GpuVertexElement("IMotionSlot", GpuVertexElementFormat.Float1) })));

        using IGpuCommandList cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(VertexStageBufferSeamGpuTests)))
        {
            cl.UpdateBuffer(frame, 0, new Vector4(1f, 0f, 0f, 0f));
            cl.UpdateBuffer<byte>(pointLights, 0, new byte[48]);
            cl.UpdateBuffer<byte>(clusters, 0, new byte[16]);
            cl.UpdateBuffer(motionFrame, 0, new Vector4(2f));
            cl.UpdateBuffer<Entry>(entryBuffer, 0, entries);
            cl.UpdateBuffer<float>(vertexBuffer, 0, vertices);
            cl.UpdateBuffer<float>(instanceBuffer, 0, instances);
            cl.UpdateBuffer<float>(slotBuffer, 0, Slots);
            cl.UpdateBuffer<ushort>(indexBuffer, 0, Indices);
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, modelSet);
            cl.SetGraphicsResourceSet(1, motionSet);
            cl.SetVertexBuffer(0, vertexBuffer);
            cl.SetVertexBuffer(1, instanceBuffer);
            cl.SetVertexBuffer(2, slotBuffer);
            cl.SetIndexBuffer(indexBuffer, GpuIndexFormat.UInt16);
            cl.DrawIndexed(6, 3, 0, 0, 4);   // instances 4, 5 and 6 read slots 3, 2 and 1
            cl.DrawIndexed(6, 1, 0, 0, 0);   // instance 0 reads slot 7
        }
        gd.Submit(cl);
        gd.WaitForIdle();

        AssertRow(GpuReadback.ToRgba(gd, target, Size, Size), "two sets, slot 2 at location 15");
    }

    [Fact]
    public void TheRigidMotionShapePutsThePreviousTransformsAtVertexRegisterT8OnDirect3D11()
    {
        CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(RigidVert, RigidFrag, "rigid motion seam");

        // Set 0 takes b0, t0 to t7, s0 and s1, so set 1's block is b1 and its buffer t8, the numbering plan Task D5
        // adds to D3D11RegisterNumberingTests for the CPU side. The optimised module keeps no block names, so each
        // stage is read as the set of registers it names, plus the one ByteAddressBuffer's own register.
        Match buffer = Regex.Match(pair.VertexSource, @"ByteAddressBuffer\s+\w+\s*:\s*register\((\w+)\)");
        string vertex = Registers(pair.VertexSource), fragment = Registers(pair.FragmentSource);
        Assert.True(buffer.Success && buffer.Groups[1].Value == "t8" && vertex == "b0 b1 t8"
            && fragment == "b0 s0 s1 t0 t1 t2 t3 t4 t5 t6 t7",
            "expected the buffer at t8, the vertex stage at b0 b1 t8 and the fragment stage at set 0's b0 s0 s1 "
            + $"t0 to t7. Got vertex {vertex}, fragment {fragment}. Emitted vertex stage:\n{pair.VertexSource}");
    }

    static string Registers(string hlsl)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(hlsl, @"register\(([btsu]\d+)\)")) names.Add(m.Groups[1].Value);
        return string.Join(' ', names);
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

    static Entry[] Entries(float colourScale)
    {
        var entries = new Entry[Cells];
        for (int k = 0; k < Cells; k++)
            entries[k] = new Entry { Place = new Vector4(-1f + (2f * k + 1f) / Cells, 0f, .1f, 0f), Colour = Colours[k] * colourScale };
        return entries;
    }

    static IGpuTexture CreateTarget(IGpuResourceFactory f) => f.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
        GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    static GpuResourceLayoutElement FragmentOnly(string name, GpuResourceKind kind) =>
        new(name, kind, GpuShaderStages.Fragment);

    static GpuPipelineDescription Pipeline(IGpuShaderSet shaders, IGpuFramebuffer fb, IGpuResourceLayout[] layouts,
        params GpuVertexLayoutDescription[] vertexLayouts) => new()
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = layouts,
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(vertexLayouts),
            Outputs = fb.Outputs,
        };

    static Vector3 Expected(int k) =>
        Array.IndexOf(Drawn, k) >= 0 ? new Vector3(Colours[k].X, Colours[k].Y, Colours[k].Z) : Vector3.Zero;

    /// <summary>
    /// Read all eight cells of the middle row, then assert once. The message prints the whole row and the failure the
    /// row implies, because which cells lit says which half of the seam broke.
    /// </summary>
    static void AssertRow(byte[] rgba, string shape)
    {
        var row = new Vector3[Cells];
        var lit = new bool[Cells];
        bool right = true, litAsDrawn = true;
        for (int k = 0; k < Cells; k++)
        {
            int i = ((Size / 2) * Size + (Size / Cells) * k + Size / Cells / 2) * 4;
            row[k] = new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
            lit[k] = MathF.Max(row[k].X, MathF.Max(row[k].Y, row[k].Z)) > .25f;
            right &= Vector3.Distance(row[k], Expected(k)) < .02f;
            litAsDrawn &= lit[k] == Array.IndexOf(Drawn, k) >= 0;
        }
        if (right) return;

        string diagnosis =
            Array.TrueForAll(lit, l => !l)
                ? "Every cell is black, so the vertex stage read nothing from the structured buffer or the uniform "
                    + "block, or no draw reached the target."
            : lit[5] && lit[6] && lit[7] && !lit[1] && !lit[2] && !lit[3]
                ? "Cells 5 to 7 lit with 1 to 3 black, so the instance-rate slot ignored the draw's first instance."
            : litAsDrawn
                ? "The right cells lit in the wrong colours, so a uniform block or an entry was read from the "
                    + "wrong place."
            : "The lit cells match no single failure, so the slots reached the wrong entries.";

        var text = new StringBuilder(FormattableString.Invariant($"{shape}: {diagnosis} Row, cell 0 at the left:"));
        for (int k = 0; k < Cells; k++)
        {
            Vector3 got = row[k] * 255f, want = Expected(k) * 255f;
            text.Append(FormattableString.Invariant(
                $"\n  cell {k}: read ({got.X:0}, {got.Y:0}, {got.Z:0}), expected ({want.X:0}, {want.Y:0}, {want.Z:0})"));
        }
        Assert.Fail(text.ToString());
    }
}
