using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT <see cref="GpuRasterizerState.DepthClipEnabled"/> DOES TO PIXELS, asserted on whichever real device
    /// the leg is running (https://github.com/APKiwiOrg/KhaozEngine/issues/598).
    ///
    /// <para><b>THIS IS A PARITY ROW, SO IT IS BACKEND-AGNOSTIC ON PURPOSE.</b> It creates its device through
    /// <see cref="GpuDeviceContext.CreateHeadless()"/>, which honours <c>KE_GRAPHICS_BACKEND</c>, so the ONE class
    /// runs on all five golden legs plus the incumbent Metal one and asserts the same two claims on each. That is
    /// the whole point: the flag was read by Direct3D 11 and Vulkan and by neither Metal path, and nothing in the
    /// suite could see the disagreement because the goldens are baked per backend family and never compared
    /// across one.</para>
    ///
    /// <para><b>THE GEOMETRY IS A QUAD THAT CROSSES THE NEAR PLANE, WITH ITS OWN CONTROL IN THE SAME DRAW.</b>
    /// Clip-space z runs linearly with x, from -0.5 at the left edge to +0.5 at the right, so it passes through
    /// zero at the horizontal centre: the LEFT half sits in front of the near plane and the RIGHT half sits
    /// inside the frustum. Clipping cuts the left half and keeps the right. Clamping keeps both and pins the left
    /// half's depth at 0. Reading one pixel from each half therefore separates "the flag was honoured" from "the
    /// draw did not happen at all", which a single-sample test cannot do.</para>
    ///
    /// <para><b>THE VARYING AXIS IS x RATHER THAN y BECAUSE ONE OF THEM IS BACKEND-DEPENDENT.</b> Clip-space y
    /// points opposite ways on Vulkan and the rest, and this suite pins
    /// <c>InvertVertexOutputY = false</c> (<see cref="KhaozEngine.Gpu.Internal.HlslCrossCompilePin"/>), so a
    /// vertically-varying quad would sample the two halves the other way round on one leg. x is untouched
    /// everywhere. Depth is read the same way: the same pin sets <c>FixClipSpaceZ = false</c>, so the z written
    /// here IS the [0,1] clip-space value every backend rasterizes, with no injected correction, which is the
    /// convention the engine's own shaders already use (<c>SkyVert</c>'s far-plane z=1).</para>
    /// </summary>
    public sealed class DepthClipModeGpuTests
    {
        const uint W = 32, H = 8;

        // Sampled columns: a quarter in from each edge, eight pixels either side of the clip boundary at the
        // horizontal centre.
        const int NearHalfColumn = 8;
        const int FarHalfColumn = 24;

        const string Vert = @"#version 450
layout(location=0) in vec3 Pos;
void main() { gl_Position = vec4(Pos, 1.0); }";

        const string Frag = @"#version 450
layout(location=0) out vec4 o;
void main() { o = vec4(0.0, 1.0, 0.0, 1.0); }";

        /// <summary>
        /// <c>DepthClipEnabled = false</c> MEANS CLAMP, so the half of the quad in front of the near plane still
        /// rasterizes, with its depth pinned to the near value rather than discarded.
        /// <para>
        /// THIS IS THE ROW THAT WAS RED ON BOTH METAL PATHS. Direct3D 11 set <c>DepthClipEnable</c> from the flag
        /// and Vulkan set <c>depthClampEnable</c> from its inverse, while both Metal backends derived
        /// <c>MTLDepthClipMode</c> from the DEPTH TEST instead and so clipped this quad, cutting the left half.
        /// </para>
        /// <para>
        /// <b>IT IS SKIPPED ON ONE PAIRING, AND ONLY THAT ONE:</b> a virtualised adapter with the Metal API
        /// validation layer holding the device
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/682">#682</see>). The hosted
        /// <c>macos-26</c> runner's Apple Paravirtual device drops <c>-setDepthClipMode:</c> with
        /// <c>MTLDepthClipModeClamp</c> under <c>MTLDebugDevice</c> and rasterizes this draw as a clip, while the
        /// same device with the layer off passes the row and real Apple silicon passes it WITH the layer armed.
        /// The clamp is derived and sent correctly on the failing leg itself, pinned there by
        /// <c>MetalPipelinePlanTests.TheDepthClipModeFollowsTheSeamsFlagAndNotTheDepthTest</c>, so the artefact
        /// is the device and not this engine. The clip row below and that derivation row stay unconditional, so
        /// a real regression in either direction still goes red on every leg.
        /// </para>
        /// </summary>
        [GpuFact(RequiresRealGpuUnderMetalApiValidation = true)]
        public void DepthClipDisabled_KeepsTheHalfInFrontOfTheNearPlane()
        {
            (byte nearHalf, byte farHalf) = RenderCrossingQuad(depthClipEnabled: false);

            Assert.True(farHalf > 200,
                "the half of the quad INSIDE the frustum must rasterize whatever the clip mode is, and it did "
                + $"not, so this run proves nothing about clamping. Got G={farHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

            Assert.True(nearHalf > 200,
                "DepthClipEnabled = false asks the rasterizer to CLAMP instead of clip, so the half of the quad "
                + "in front of the near plane must still rasterize with its depth clamped to 0. It did not, so "
                + "this backend clipped instead: on Metal that is MTLDepthClipMode derived from the depth test "
                + "rather than from the seam's own flag (issue 598). Got "
                + $"G={nearHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        /// <summary>
        /// AND THE OTHER DIRECTION, which is what stops the row above from passing on a backend that simply never
        /// clips. <c>DepthClipEnabled = true</c> must cut the same half.
        /// </summary>
        [GpuFact]
        public void DepthClipEnabled_CutsTheHalfInFrontOfTheNearPlane()
        {
            (byte nearHalf, byte farHalf) = RenderCrossingQuad(depthClipEnabled: true);

            Assert.True(farHalf > 200,
                "the half of the quad INSIDE the frustum must rasterize under clipping too, and it did not, so "
                + $"this run proves nothing about clipping. Got G={farHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

            Assert.True(nearHalf < 50,
                "DepthClipEnabled = true asks for real near-plane clipping, so the half of the quad in front of "
                + "it must be discarded. It was not, which means the flag reaches nothing on this backend in "
                + "EITHER direction. Got "
                + $"G={nearHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        /// <summary>
        /// AND THE SAME TWO CLAIMS ON A PASS WITH NO DEPTH ATTACHMENT AT ALL, which is the hole 17.39.0 left
        /// behind (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/674">#674</see>).
        ///
        /// <para><b>THE CLAMP DIRECTION WAS UNREACHABLE ON METAL HERE AND ONLY HERE.</b> Metal expresses the
        /// flag as <c>-setDepthClipMode:</c> on the render encoder, and that call used to sit inside the guard
        /// that skips the depth state when the BOUND FRAMEBUFFER has no depth attachment. So a colour-only pass
        /// rasterized at the encoder default, <c>MTLDepthClipModeClip</c>, whatever the pipeline asked for, and
        /// <c>false</c> could not be expressed at all. Direct3D 11 (<c>DepthClipEnable</c>) and Vulkan
        /// (<c>depthClampEnable</c>) both hold the flag in rasterizer state that exists with or without a depth
        /// attachment, so both already honoured it here and the disagreement was invisible.</para>
        ///
        /// <para><b>NOTHING SHIPPED COULD SEE IT, WHICH IS WHY THE ROW HAD TO BE WRITTEN RATHER THAN FOUND.</b>
        /// The engine's colour-only passes are the fullscreen post ones, whose vertex stage writes z = 0
        /// exactly, inside the depth range, where clipping and clamping do the same thing. No committed golden
        /// moves either way. This quad crosses the near plane on purpose so the two modes separate.</para>
        ///
        /// <para>Skipped on the one pairing the depth row above is skipped on, and for the same measured reason
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/682">#682</see>): a virtualised adapter
        /// with the Metal API validation layer holding the device drops the clamp.</para>
        /// </summary>
        [GpuFact(RequiresRealGpuUnderMetalApiValidation = true)]
        public void DepthClipDisabled_KeepsTheHalfInFrontOfTheNearPlane_OnAColourOnlyPass()
        {
            (byte nearHalf, byte farHalf) = RenderCrossingQuad(depthClipEnabled: false, depthAttachment: false);

            Assert.True(farHalf > 200,
                "the half of the quad INSIDE the frustum must rasterize on a colour-only pass whatever the clip "
                + "mode is, and it did not, so this run proves nothing about clamping. Got "
                + $"G={farHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

            Assert.True(nearHalf > 200,
                "DepthClipEnabled = false must CLAMP on a pass with no depth attachment too. It did not, so the "
                + "backend clipped instead: on Metal that is -setDepthClipMode: still sitting behind the "
                + "framebuffer-has-depth guard, leaving the encoder default MTLDepthClipModeClip in force "
                + "(issue 674). Got "
                + $"G={nearHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        /// <summary>
        /// The colour-only control. <c>DepthClipEnabled = true</c> must still cut the half in front of the near
        /// plane, so the row above cannot pass on a backend that has simply stopped clipping.
        /// </summary>
        [GpuFact]
        public void DepthClipEnabled_CutsTheHalfInFrontOfTheNearPlane_OnAColourOnlyPass()
        {
            (byte nearHalf, byte farHalf) = RenderCrossingQuad(depthClipEnabled: true, depthAttachment: false);

            Assert.True(farHalf > 200,
                "the half of the quad INSIDE the frustum must rasterize under clipping on a colour-only pass "
                + "too, and it did not, so this run proves nothing about clipping. Got "
                + $"G={farHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

            Assert.True(nearHalf < 50,
                "DepthClipEnabled = true asks for real near-plane clipping on a colour-only pass as well, so "
                + "the half in front of it must be discarded. It was not, which means the flag reaches nothing "
                + "there in EITHER direction. Got "
                + $"G={nearHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        /// <summary>
        /// Draw the crossing quad once under <paramref name="depthClipEnabled"/> and return the green channel of
        /// one pixel from each half: the one in front of the near plane, then the one inside the frustum.
        /// </summary>
        /// <param name="depthClipEnabled">The seam flag under test.</param>
        /// <param name="depthAttachment">Whether the bound framebuffer carries a depth texture. False is the
        /// colour-only pass of issue 674, where the depth test is off because there is nothing to test
        /// against and the clip mode is the only depth-shaped state left in play.</param>
        static (byte NearHalf, byte FarHalf) RenderCrossingQuad(bool depthClipEnabled,
            bool depthAttachment = true)
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = ctx.GpuDevice;
            IGpuResourceFactory f = device.Factory;

            // Two triangles. z is a pure function of x (z = x * 0.5), so it is -0.5 at the left edge, 0 at the
            // horizontal centre and +0.5 at the right: the left half is in front of the near plane.
            var quad = new[]
            {
                new Vector3(-1, -1, -0.5f), new Vector3(1, -1, 0.5f), new Vector3(-1, 1, -0.5f),
                new Vector3(-1, 1, -0.5f), new Vector3(1, -1, 0.5f), new Vector3(1, 1, 0.5f),
            };

            using IGpuBuffer vb = f.CreateBuffer(new GpuBufferDescription(6 * 12, GpuBufferUsage.VertexBuffer));
            device.UpdateBuffer(vb, 0, quad);

            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuTexture? depth = depthAttachment
                ? f.CreateTexture(GpuTextureDescription.Texture2D(
                    W, H, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil))
                : null;
            using IGpuFramebuffer fb = f.CreateFramebuffer(depth, target);

            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription());

            var pd = new GpuPipelineDescription
            {
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },

                // THE DEPTH TEST IS ON, and that is the whole point of the row rather than incidental. Both Metal
                // backends read this flag to pick their clip mode, so a depth-test-off pipeline would have agreed
                // with the fixed behaviour by accident and asserted nothing.
                // A colour-only pass gets the test OFF, because a depth-testing pipeline against an output with
                // no depth format is the debug-layer failure the guard below exists for. That is exactly what
                // makes the row interesting: with the test off, the clip mode cannot be derived from it.
                DepthStencil = depthAttachment
                    ? GpuDepthStencilState.DepthOnlyLessEqual : GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(
                    GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new GpuVertexLayoutDescription(new GpuVertexElement("Pos", GpuVertexElementFormat.Float3)),
                },
                ShaderSet = shaders,

                // Declared empty rather than dropped, and bound below: the native Metal backend requires the
                // declared layout array to be the same shape as the reflection, and Veldrid's Metal backend
                // dereferenced a null at the draw when a declared layout had no set bound. Both halves are the
                // same requirement GpuDeviceSmokeTests documents at length.
                ResourceLayouts = new[] { layout },
                Outputs = fb.Outputs,
            };

            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(pd);
            using IGpuResourceSet emptySet = f.CreateResourceSet(new GpuResourceSetDescription(layout));

            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);

            // Cleared to the far plane, so every fragment the rasterizer emits passes LessEqual and the only
            // thing deciding a pixel is whether the rasterizer emitted it at all.
            if (depthAttachment) cl.ClearDepthStencil(1f);
            cl.SetFullScissorRects();
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, emptySet);
            cl.SetVertexBuffer(0, vb);
            cl.Draw(6, 1, 0, 0);
            cl.CopyTexture(target, staging);
            cl.End();
            device.Submit(cl);
            device.WaitForIdle();

            MappedData map = device.Map(staging, GpuMapMode.Read);
            try
            {
                int row = (int)H / 2 * (int)map.RowPitch;
                byte near = Marshal.ReadByte(map.Data, row + NearHalfColumn * 4 + 1);
                byte far = Marshal.ReadByte(map.Data, row + FarHalfColumn * 4 + 1);
                return (near, far);
            }
            finally
            {
                device.Unmap(staging);
            }
        }
    }
}
