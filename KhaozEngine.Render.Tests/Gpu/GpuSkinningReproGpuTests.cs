using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    // GPU-SKINNING VIABILITY REPRO (the spike gate). The engine HAD a GPU skinned-mesh path and removed it: the
    // recorded reason was that the skinned vertex shader's bone-buffer array read returned garbage past element 0 in
    // the WINDOWED Veldrid/Metal swapchain context ("only bones[0] survives, a constant bones[1] or any
    // data-dependent index reads garbage - independent of buffer type / binding / dynamic offset / submit
    // structure. headless/fenced is clean"). Archaeology (git show 80b69f22^) showed the OLD binding was already a
    // dynamic-offset UNIFORM block `Bones { mat4 bones[128]; }` (GpuBufferUsage.UniformBuffer, dynamic:true),
    // indexed by a per-vertex float BoneIndex - NOT a storage/structured buffer.
    //
    // These tests isolate exactly that read on THIS machine's Metal device, OFFSCREEN. They render N small quads,
    // each carrying a per-vertex bone index i, where bones[i] is a translation that places quad i at a distinct screen
    // column. If every element past 0 reads correctly, all N columns are occupied. If "only bones[0] survives", the
    // quads collapse and the distinct-column assertion fails.
    //
    // SPIKE OUTCOME (see the report): variants 1 + 2 prove the plain uniform-block mat4 array + per-draw dynamic
    // offset read is CLEAN when bones is the ONLY resource buffer - the spike's hypothesis holds in isolation.
    // Variant 3 pins down the REAL blocker: a pipeline whose VERTEX stage reads a SECOND resource buffer (the frame/
    // material UBO at set 0 + bones at set 1) reproduces the historical corruption OFFSCREEN (only the first bones
    // survive), for uniform AND storage bones and 1 or 2 vertex buffers - the SAME Metal two-UBO mis-bind the
    // splat-params note documents. It also proves the FIX: fold the matrix into the bone buffer so the vertex reads
    // exactly ONE resource buffer at set 0. So the historical bug is NOT windowed-specific (it repros offscreen) and
    // NOT a bone-read bug. It is a multi-buffer binding bug, fixable but needing a skinned-specific binding layout.
    // Skipped unless KE_GPU_TESTS is set.
    public sealed class GpuSkinningReproGpuTests
    {
        readonly ITestOutputHelper _out;

        public GpuSkinningReproGpuTests(ITestOutputHelper output) => _out = output;

        const int W = 256, H = 64;
        const int N = 8;                 // distinct bones exercised (0..7), index 7 is a strong "past element 0" probe
        const int MaxBones = 128;        // matches SkinningMath.MaxBonesPerDraw: the uniform block is `mat4 bones[128]` = 8 KiB
        const uint SlotBytes = (uint)MaxBones * 64u; // 8192, a multiple of 256 => valid dynamic-offset alignment

        // Vertex: NDC xy + a float-encoded bone index. 12 bytes (Float2 + Float1), no padding.
        [StructLayout(LayoutKind.Sequential)]
        struct Vtx
        {
            public float X, Y, Bone;
            public Vtx(float x, float y, float bone) { X = x; Y = y; Bone = bone; }
        }

        const string Vert = @"#version 450
layout(set=0, binding=0) uniform Bones { mat4 bones[128]; };
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
        //      buffer, where draw s binds slot s via a per-draw dynamic offset, and its quad reads a FIXED non-zero bone
        //      index (5) within that slot. Tests dynamic-offset window selection AND a non-zero per-vertex index
        //      together - the historical failure mode. ----
        [GpuFact]
        public void DynamicOffsetUniformBlock_NonZeroIndex_ReadsEverySlot()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            var f = gd.Factory;
            const int FixedIdx = 5; // a non-zero index, exercised in every slot

            // N slots of MaxBones mat4. In slot s, bones[FixedIdx] = translation to column s, rest identity.
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
            // The set binds a single-slot WINDOW (offset 0, size SlotBytes), and the per-draw dynamic offset selects the slot.
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

        // ---- Variant 3: the ROOT CAUSE + the WORKING FIX for the real skinned pipeline.
        //   The real model VERTEX stage needs BOTH a matrix (ViewProj) AND the bone palette. Doing that as TWO
        //   uniform buffers read by the vertex (frame U at set 0 + bones at set 1) REPRODUCES the historical
        //   corruption OFFSCREEN: only the first bones survive (measured occupancy [1,0,0,0,0,0,0,0], or
        //   [1,1,1,1,0,0,0,0] with the sets swapped). This is the SAME Metal two-UBO mis-bind the splat-params note
        //   documents, and it holds for a STORAGE bones buffer and for 1 or 2 vertex buffers too (all confirmed via
        //   this harness during the spike). Variants 1+2 prove the bone read itself is fine when bones is the ONLY
        //   resource, so the trigger is the SECOND vertex-stage resource buffer, not the indexed read.
        //   THE FIX proven here: FOLD the matrix into the bone buffer so the vertex stage reads exactly ONE resource
        //   buffer (the combined { Mvp; bones[128] }) AT SET 0, with every other UBO/texture at set 1+ read ONLY by
        //   the fragment. Then all 8 bones read correctly. Shipping this in the engine requires the material + frame
        //   UBO moved off set 0 for the skinned pipeline (a skinned-specific fragment + material layout), because the
        //   shared ModelFrag reads the material at set 0 - see the spike report. ----
        const string TwoUboVert = @"#version 450
layout(set=0, binding=0) uniform VBlock { mat4 Mvp; mat4 bones[128]; };  // the vertex's ONLY resource buffer, at set 0
layout(location=0) in vec2 Pos;
layout(location=1) in float BoneIndex;
void main() {
    mat4 b = bones[int(BoneIndex)];
    gl_Position = Mvp * vec4((b * vec4(Pos, 0.0, 1.0)).xy, 0.0, 1.0);
}";
        const string TwoUboFrag = @"#version 450
layout(set=1, binding=0) uniform U { vec4 Tint; };   // frame UBO at set 1, read ONLY by the fragment
layout(location=0) out vec4 o;
void main() { o = vec4(1.0, 1.0, 1.0, 1.0) + Tint * 1e-30; }";

        [GpuFact]
        public void FoldMatrixIntoBoneBuffer_VertexReadsOneResource_ReadsEveryBone()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            // THIS ROW IS THE OTHER ONE THAT PROVOKES METAL'S API VALIDATION ON THE INCUMBENT, and it took
            // https://github.com/APKiwiOrg/KhaozEngine/issues/621 to attribute it. The shape above is the
            // split-stage one 2.3a of docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md measures: the vertex
            // reads VBlock at set 0 and nothing else, the fragment reads U at set 1 and nothing else. The
            // incumbent counts one slot per kind across the WHOLE declared layout, so it writes U at buffer index
            // 1, while the cross-compiler numbers each stage densely from 0 over only what that stage declares,
            // so the emitted fragment function reads buffer index 0. Nothing is bound there, and the layer's
            // default error mode is assert, so the draw kills the test HOST rather than failing this row.
            //
            // The mis-bind costs this row's ASSERTION nothing, which is why it went unnoticed for so long: U's
            // only use is `Tint * 1e-30`, present to put a second uniform buffer in the pipeline rather than to
            // be read, and the bone columns this row measures come from the vertex stage, which binds correctly.
            // So the row keeps its meaning on every unarmed run and on every other backend.
            //
            // THE GUARD IS THE BACKEND AS WELL AS THE ARMING, deliberately. The engine's own native Metal backend
            // binds at the index read out of each stage's emission, so this same draw is CORRECT there and the
            // layer says nothing: the metal-native leg runs this row armed today and it passes. Standing down on
            // the arming alone would throw that away to work around a defect that is not on that backend. The
            // defect itself is the incumbent's numbering, measured, recorded, and retiring with that leg
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/604), so there is nothing to fix here.
            if (gd.Backend == GpuBackendKind.Metal && MetalValidationDormancy.StandDown(_out,
                    "reproduces the incumbent's split-stage mis-binding on purpose, by reading its frame UBO from "
                    + "the fragment stage alone at set 1 behind a vertex-only set 0, which the layer sees as a "
                    + "draw with an unbound fragment buffer at index 0"))
                return;

            var f = gd.Factory;

            // Combined vertex block: [0] = Mvp (identity), [1+i] = bones[i]. The shader's bones[i] == combined[1+i].
            var combined = new Matrix4x4[1 + MaxBones];
            for (int i = 0; i < combined.Length; i++) combined[i] = Matrix4x4.Identity;
            for (int i = 0; i < N; i++) combined[1 + i] = Matrix4x4.CreateTranslation(ColumnX(i), 0f, 0f);
            var verts = new List<Vtx>(N * 6);
            const float hw = 0.05f, hh = 0.6f;
            for (int i = 0; i < N; i++)
            {
                float bi = i;
                verts.Add(new Vtx(-hw, -hh, bi)); verts.Add(new Vtx(hw, -hh, bi)); verts.Add(new Vtx(hw, hh, bi));
                verts.Add(new Vtx(-hw, -hh, bi)); verts.Add(new Vtx(hw, hh, bi)); verts.Add(new Vtx(-hw, hh, bi));
            }
            var vtxArr = verts.ToArray();

            using IGpuTexture color = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, color);
            using IGpuBuffer frameUbo = f.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // fragment Tint
            using IGpuBuffer vblock = f.CreateBuffer(new GpuBufferDescription((uint)combined.Length * 64u, GpuBufferUsage.UniformBuffer)); // vertex: Mvp + bones
            using IGpuBuffer vb = f.CreateBuffer(new GpuBufferDescription((uint)(vtxArr.Length * Marshal.SizeOf<Vtx>()), GpuBufferUsage.VertexBuffer));
            using IGpuResourceLayout vblockLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("VBlock", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using IGpuResourceLayout frameLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            using IGpuResourceSet vblockSet = f.CreateResourceSet(new GpuResourceSetDescription(vblockLayout, vblock));
            using IGpuResourceSet frameSet = f.CreateResourceSet(new GpuResourceSetDescription(frameLayout, frameUbo));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(TwoUboVert, TwoUboFrag);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { vblockLayout, frameLayout },   // set 0 = combined vertex block, set 1 = fragment U
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { VtxLayout() },
                Outputs = fb.Outputs,
            });
            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            Matrix4x4 idm = Matrix4x4.Identity;
            cl.UpdateBuffer(frameUbo, 0, in idm);
            cl.UpdateBuffer(vblock, 0, combined.AsSpan());
            cl.UpdateBuffer(vb, 0, vtxArr.AsSpan());
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipe);
            cl.SetGraphicsResourceSet(0, vblockSet);
            cl.SetGraphicsResourceSet(1, frameSet);
            cl.SetVertexBuffer(0, vb);
            cl.Draw((uint)vtxArr.Length);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
            byte[] px = GpuReadback.ToRgba(gd, color, W, H);
            DumpPng(px, "gpu-skinning-repro-foldedmatrix.png");
            AssertEveryColumnOccupied(px, "FoldMatrixIntoBoneBuffer");
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
            const int tol = 6; // pixels. bars are ~13px wide, columns are spread ~27px apart
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
            catch { /* PNG dump is diagnostic only, never fail the test on IO */ }
        }
    }
}
