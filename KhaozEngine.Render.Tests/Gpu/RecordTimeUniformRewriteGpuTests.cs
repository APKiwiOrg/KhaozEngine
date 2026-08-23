using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE COLLAPSE, MEASURED RATHER THAN REASONED ABOUT: two record-time writes to the SAME range of ONE uniform
    /// buffer, a draw recorded between them, and both draws' pixels read back. Row zero of
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see>'s audit, and the licence for
    /// every verdict that audit reaches.
    ///
    /// <para><b>WHY A MEASUREMENT AND NOT A CODE READ.</b> The ring's own doc comments say a record-time write is
    /// a plain memcpy into the current frame's mapped segment and is not ordered against the draws, so the last
    /// write in a frame decides every byte. That is a statement about three implementations, and #483 exists
    /// because the same statement was made about a fourth thing (the Direct3D 11 leg the engine shipped until
    /// <c>18.0.0</c>) where it was FALSE.
    /// A verdict of "safe" on 30-odd renderer sites rests on knowing exactly which backends collapse, and the
    /// only way to know is to make one collapse and photograph it.</para>
    ///
    /// <para><b>THE SHAPE AVOIDS GEOMETRY ENTIRELY.</b> Two same-sized targets, one uniform buffer, one pipeline:
    /// write A, draw into target A, write B, draw into target B, all inside ONE command list, then read one texel
    /// out of each. An ORDERED backend leaves A holding A and B holding B. A COLLAPSING backend leaves both
    /// holding B, because there is only ever one copy of those 16 bytes and the second memcpy overwrote the first
    /// before the queue ran either draw. No scissor, no viewport, no half-covered triangle: the two readings
    /// differ in the one variable under test.</para>
    ///
    /// <para><b>IT ASSERTS THE COLLAPSE, WHICH IS A CONTRACT AND NOT A DEFECT.</b> The ring is the reason the
    /// native backends do not pay a blocking Map (Direct3D 11) or a render-pass split (Vulkan) per uniform write,
    /// and <c>docs/USING-KHAOZENGINE.md</c> states the consequence to consumers in as many words: address
    /// per-draw uniforms by dynamic offset. So a red run here means the ring stopped being a ring, which is a
    /// change that has to be noticed, not a bug that got fixed.</para>
    ///
    /// <para><b>IT USED TO RECORD A SECOND, UNASSERTED MEASUREMENT beside the native one</b>, taken on the
    /// Veldrid Metal leg that shipped alongside it. That leg was deleted in <c>18.0.0</c> and the control went
    /// with it, so this row now measures exactly the one backend its name claims. The audit's other half is in
    /// #483's own record.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, the phase-3 row-19 rule: under <c>KE_GPU_TESTS=1</c>
    /// the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a failure, so the row
    /// returns early with the reason recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class RecordTimeUniformRewriteGpuTests
    {
        const uint Size = 4;

        // Only 0 and 1 appear, so an 8-bit readback compares exactly with no rounding to reason about, and the
        // two values are far enough apart that a partial write could not be mistaken for either.
        static readonly Vector4 FirstValue = new(1f, 0f, 0f, 1f);   // red
        static readonly Vector4 SecondValue = new(0f, 0f, 1f, 1f);  // blue

        readonly ITestOutputHelper _out;

        public RecordTimeUniformRewriteGpuTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// #483'S PREMISE: on the native Metal backend the FIRST draw of the pair renders the SECOND write's
        /// value, because the two writes shared a range and the ring keeps one copy of it per frame.
        /// </summary>
        [GpuFact]
        public void ARewrittenUniformRangeGivesBothDrawsTheLastValueOnTheNativeBackend()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            Assert.Equal(GpuBackendKind.MetalNative, native.Backend);

            (Color first, Color second) = Measure(native.GpuDevice);
            _out.WriteLine($"native Metal ({native.Capabilities.DeviceName}): {Describe(first, second)}");

            // The second draw reads the second value on every backend: that half is what says the probe worked at
            // all rather than binding nothing.
            Assert.Equal(Blue, second);
            // And the first draw reads it too, which is the collapse. On an ordered backend this line reads red.
            Assert.Equal(Blue, first);
        }

        /// <summary>
        /// WRITE, DRAW, REWRITE THE SAME RANGE, DRAW, in ONE list, and return what each draw left behind. Written
        /// against the seam alone so the same body runs on both backends, which is what makes the two readings
        /// comparable.
        /// </summary>
        static (Color First, Color Second) Measure(IGpuDevice gd)
        {
            IGpuResourceFactory f = gd.Factory;

            using IGpuTexture firstTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuTexture secondTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer firstFb = f.CreateFramebuffer(null, firstTex);
            using IGpuFramebuffer secondFb = f.CreateFramebuffer(null, secondTex);

            using IGpuBuffer tint = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vertices = f.CreateBuffer(
                new GpuBufferDescription(6 * sizeof(float), GpuBufferUsage.VertexBuffer));

            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tint", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, tint));

            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(TintVert, TintFrag);
            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [layout],
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Pos", GpuVertexElementFormat.Float2)),
                },
                Outputs = firstFb.Outputs,
            });

            gd.UpdateBuffer(vertices, 0, FullScreenTriangle);

            Vector4 first = FirstValue;
            Vector4 second = SecondValue;
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();

                // WRITE A, then the draw that is supposed to read A.
                cl.UpdateBuffer(tint, 0, in first);
                cl.SetFramebuffer(firstFb);
                cl.ClearColorTarget(0, Color.Black);   // black = nothing drew, distinct from either value
                cl.SetPipeline(pipeline);
                cl.SetGraphicsResourceSet(0, set);
                cl.SetVertexBuffer(0, vertices);
                cl.Draw(3);

                // WRITE B OVER THE SAME 16 BYTES, then the draw that is supposed to read B.
                cl.UpdateBuffer(tint, 0, in second);
                cl.SetFramebuffer(secondFb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(pipeline);
                cl.SetGraphicsResourceSet(0, set);
                cl.SetVertexBuffer(0, vertices);
                cl.Draw(3);

                cl.End();
                gd.Submit(cl);
            }

            gd.WaitForIdle();

            // A device that lost itself would read black and mean something else entirely, so the latch is
            // checked before either pixel is believed.
            Assert.Null(gd.Diagnostics.DeviceLossReason);

            return (Centre(gd, firstTex), Centre(gd, secondTex));
        }

        static Color Centre(IGpuDevice gd, IGpuTexture texture)
        {
            byte[] pixels = GpuReadback.ToRgba(gd, texture, (int)Size, (int)Size);
            int at = (((int)Size / 2) * (int)Size + ((int)Size / 2)) * 4;
            return new Color(pixels[at] / 255f, pixels[at + 1] / 255f, pixels[at + 2] / 255f,
                pixels[at + 3] / 255f);
        }

        static Color Red => new(1f, 0f, 0f, 1f);
        static Color Blue => new(0f, 0f, 1f, 1f);

        // WHAT THE PAIR MEANS, so a failure and the control both report a diagnosis rather than six floats.
        static string Describe(Color first, Color second)
        {
            string what = (first, second) switch
            {
                _ when first == Red && second == Blue =>
                    "ORDERED: each draw read the value written before it, so this backend sequences a record-time "
                    + "uniform write against the draws",
                _ when first == Blue && second == Blue =>
                    "COLLAPSED: both draws read the LAST write, which is the ring semantics #483 is about",
                _ when first == Red && second == Red =>
                    "the SECOND write never landed at all, which is neither outcome and means the probe is broken",
                _ => "an outcome none of the three predicted shapes covers",
            };

            return $"first draw ({first.R}, {first.G}, {first.B}), second draw ({second.R}, {second.G}, "
                + $"{second.B}) - {what}.";
        }

        // The full-screen triangle every fullscreen pass in this engine uses, in clip space, so one texel answers
        // for the whole draw.
        static ReadOnlySpan<float> FullScreenTriangle => [-1f, -1f, 3f, -1f, -1f, 3f];

        const string TintVert = @"#version 450
layout(location = 0) in vec2 Pos;
void main() { gl_Position = vec4(Pos, 0.0, 1.0); }
";

        const string TintFrag = @"#version 450
layout(set = 0, binding = 0) uniform Tint { vec4 C; };
layout(location = 0) out vec4 oColour;
void main() { oColour = C; }
";
    }
}
