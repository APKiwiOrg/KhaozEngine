using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // GPU-SKINNING VIABILITY REPRO (the spike gate). The engine HAD a GPU skinned-mesh path and removed it: the
    // recorded reason was that the skinned vertex shader's bone-buffer array read returned garbage past element 0 in
    // the WINDOWED Veldrid/Metal swapchain context ("only bones[0] survives; a constant bones[1] or any
    // data-dependent index reads garbage - independent of buffer type / binding / dynamic offset / submit
    // structure; headless/fenced is clean"). Archaeology (git show 80b69f22^) showed the OLD binding was already a
    // dynamic-offset UNIFORM block `Bones { mat4 bones[128]; }` (GpuBufferUsage.UniformBuffer, dynamic:true),
    // indexed by a per-vertex float BoneIndex - NOT a storage/structured buffer.
    //
    // These tests isolate exactly that read on THIS machine's Metal device, OFFSCREEN. They render N small quads,
    // each carrying a per-vertex bone index i; bones[i] is a translation that places quad i at a distinct screen
    // column. If every element past 0 reads correctly, all N columns are occupied. If "only bones[0] survives", the
    // quads collapse and the distinct-column assertion fails.
    //
    // IMPORTANT SCOPE: passing here is NECESSARY BUT NOT SUFFICIENT proof - the historical corruption manifested in
    // the WINDOWED swapchain-present context, not offscreen. A clean offscreen pass means the untested hypothesis
    // (that the read is sound with a plain uniform-block mat4 array) survives offscreen; it does NOT resurrect GPU
    // skinning windowed. A human windowed A/B is still required. Skipped unless KE_GPU_TESTS is set.
    public sealed class GpuSkinningReproGpuTests
    {
        const int W = 256, H = 64;
        const int N = 8;                 // distinct bones exercised (0..7); index 7 is a strong "past element 0" probe
        const int MaxBones = 64;         // the uniform block is `mat4 bones[64]` = 4 KiB (a 256-aligned slot)
        const uint SlotBytes = (uint)MaxBones * 64u; // 4096, a multiple of 256 => valid dynamic-offset alignment

        // Vertex: NDC xy + a float-encoded bone index. 12 bytes (Float2 + Float1), no padding.
        [StructLayout(LayoutKind.Sequential)]
        struct Vtx
        {
            public float X, Y, Bone;
            public Vtx(float x, float y, float bone) { X = x; Y = y; Bone = bone; }
        }

        const string Vert = @"#version 450
layout(set=0, binding=0) uniform Bones { mat4 bones[64]; };
layout(location=0) in vec2 Pos;
layout(location=1) in float BoneIndex;
void main() {
    mat4 b = bones[int(BoneIndex)];         // per-vertex, data-dependent index - the exact read that historically corrupted
    vec4 p = b * vec4(Pos, 0.0, 1.0);
    gl_Position = vec4(p.xy, 0.0, 1.0);
}";
        const string Frag = @"#version 450
layout(location=0) out vec4 o;
void main() { o = vec4(1.0, 1.0, 1.0, 1.0); }";

        // Column centre (NDC x) that bone i translates its quad to: evenly spread -0.75..+0.75 across i=0..N-1.
        static float ColumnX(int i) => -0.75f + i * (1.5f / (N - 1));
        // Expected pixel column for bone i.
        static int ExpectedPx(int i) => (int)((ColumnX(i) * 0.5f + 0.5f) * W);

        // ---- Variant 1: a plain (non-dynamic) uniform block, one draw, per-vertex indices 0..N-1. ----
        [GpuFact]
        public void UniformBlock_PerVertexIndex_ReadsEveryBone_NotJustElement0()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            var f = gd.Factory;

            // bones[i] = translation to column i (only bones 0..N-1 matter).
            var bones = new Matrix4x4[MaxBones];
            for (int i = 0; i < MaxBones; i++) bones[i] = Matrix4x4.Identity;
            for (int i = 0; i < N; i++) bones[i] = Matrix4x4.CreateTranslation(ColumnX(i), 0f, 0f);

            // One quad per bone, all in one vertex buffer, each quad's 6 verts carry BoneIndex = i.
            var verts = new List<Vtx>(N * 6);
            const float hw = 0.05f, hh = 0.6f; // half width/height in NDC (tall thin bars, easy to detect per column)
            for (int i = 0; i < N; i++)
            {
                float bi = i;
                verts.Add(new Vtx(-hw, -hh, bi)); verts.Add(new Vtx(hw, -hh, bi)); verts.Add(new Vtx(hw, hh, bi));
                verts.Add(new Vtx(-hw, -hh, bi)); verts.Add(new Vtx(hw, hh, bi)); verts.Add(new Vtx(-hw, hh, bi));
            }

            byte[] px = RenderBars(gd, f, bones.AsSpan(), verts.ToArray(), dynamic: false);
            AssertEveryColumnOccupied(px, "UniformBlock_PerVertexIndex");
            DumpPng(px, "gpu-skinning-repro-uniformblock.png");
        }

        // ---- Variant 2: a DYNAMIC-OFFSET uniform block (the exact old SkinnedModelRenderer binding). N slots in one
        //      buffer; draw s binds slot s via a per-draw dynamic offset, and its quad reads a FIXED non-zero bone
        //      index (5) within that slot. Tests dynamic-offset window selection AND a non-zero per-vertex index
        //      together - the historical failure mode. ----
        [GpuFact]
        public void DynamicOffsetUniformBlock_NonZeroIndex_ReadsEverySlot()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            var f = gd.Factory;
            const int FixedIdx = 5; // a non-zero index, exercised in every slot

            // N slots of MaxBones mat4. In slot s, bones[FixedIdx] = translation to column s; rest identity.
            var slots = new Matrix4x4[N * MaxBones];
            for (int i = 0; i < slots.Length; i++) slots[i] = Matrix4x4.Identity;
            for (int s = 0; s < N; s++) slots[s * MaxBones + FixedIdx] = Matrix4x4.CreateTranslation(ColumnX(s), 0f, 0f);

            // One shared quad (6 verts, BoneIndex = FixedIdx), drawn N times with different dynamic offsets.
            const float hw = 0.05f, hh = 0.6f;
            var quad = new[]
            {
                new Vtx(-hw, -hh, FixedIdx), new Vtx(hw, -hh, FixedIdx), new Vtx(hw, hh, FixedIdx),
                new Vtx(-hw, -hh, FixedIdx), new Vtx(hw, hh, FixedIdx), new Vtx(-hw, hh, FixedIdx),
            };

            using IGpuTexture color = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, color);

            using IGpuBuffer boneBuf = f.CreateBuffer(new GpuBufferDescription((uint)slots.Length * 64u, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vb = f.CreateBuffer(new GpuBufferDescription((uint)(quad.Length * Marshal.SizeOf<Vtx>()), GpuBufferUsage.VertexBuffer));
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Bones", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
            // The set binds a single-slot WINDOW (offset 0, size SlotBytes); the per-draw dynamic offset selects the slot.
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, new GpuBufferRange(boneBuf, 0, SlotBytes)));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { VtxLayout() },
                Outputs = fb.Outputs,
            });

            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.UpdateBuffer(boneBuf, 0, slots.AsSpan());
            cl.UpdateBuffer(vb, 0, quad.AsSpan());
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipe);
            cl.SetVertexBuffer(0, vb);
            for (int s = 0; s < N; s++)
            {
                cl.SetGraphicsResourceSet(0, set, (uint)s * SlotBytes);
                cl.Draw(6);
            }
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            byte[] px = GpuReadback.ToRgba(gd, color, W, H);
            AssertEveryColumnOccupied(px, "DynamicOffsetUniformBlock_NonZeroIndex");
            DumpPng(px, "gpu-skinning-repro-dynamicoffset.png");
        }

        static GpuVertexLayoutDescription VtxLayout() => new(
            new GpuVertexElement("Pos", GpuVertexElementFormat.Float2),
            new GpuVertexElement("BoneIndex", GpuVertexElementFormat.Float1));

        static byte[] RenderBars(IGpuDevice gd, IGpuResourceFactory f, ReadOnlySpan<Matrix4x4> bones, Vtx[] verts, bool dynamic)
        {
            using IGpuTexture color = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, color);
            using IGpuBuffer boneBuf = f.CreateBuffer(new GpuBufferDescription((uint)bones.Length * 64u, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vb = f.CreateBuffer(new GpuBufferDescription((uint)(verts.Length * Marshal.SizeOf<Vtx>()), GpuBufferUsage.VertexBuffer));
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Bones", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, new GpuBufferRange(boneBuf, 0, (uint)bones.Length * 64u)));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { VtxLayout() },
                Outputs = fb.Outputs,
            });
            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.UpdateBuffer(boneBuf, 0, bones);
            cl.UpdateBuffer(vb, 0, verts.AsSpan());
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipe);
            cl.SetGraphicsResourceSet(0, set);
            cl.SetVertexBuffer(0, vb);
            cl.Draw((uint)verts.Length);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
            return GpuReadback.ToRgba(gd, color, W, H);
        }

        // Assert every bone's expected column band has white pixels (all N bones read correctly), and count distinct
        // occupied bands - the decisive "not just element 0" check.
        static void AssertEveryColumnOccupied(byte[] px, string label)
        {
            int midRow = H / 2;
            const int tol = 6; // pixels; bars are ~13px wide, columns are spread ~27px apart
            var occupied = new bool[N];
            for (int i = 0; i < N; i++)
            {
                int cx = ExpectedPx(i);
                bool found = false;
                for (int x = Math.Max(0, cx - tol); x <= Math.Min(W - 1, cx + tol) && !found; x++)
                    if (px[(midRow * W + x) * 4] > 200) found = true; // white bar present at this column
                occupied[i] = found;
            }
            int count = 0; foreach (var b in occupied) if (b) count++;
            // If the read collapsed to bones[0], only column 0 lights up (count == 1). All N present => every element read.
            Assert.True(count == N,
                $"{label}: expected all {N} bone columns occupied (each a distinct bones[i] read), got {count}. " +
                $"occupancy=[{string.Join(",", Array.ConvertAll(occupied, b => b ? "1" : "0"))}] " +
                $"(1 => historical 'only bones[0] survives' corruption reproduced offscreen).");
        }

        static void DumpPng(byte[] rgba, string name)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ke-gpu-skinning-spike");
                Directory.CreateDirectory(dir);
                PngWriter.Save(Path.Combine(dir, name), rgba, W, H);
            }
            catch { /* PNG dump is diagnostic only; never fail the test on IO */ }
        }
    }
}
