using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A record-time uniform write applies in list order across submissions: each of several command lists, submitted
    /// one after another with no present and no wait between them, writes its own value into the SAME uniform range and
    /// draws it into its own target, and every target reads back its own list's value. That is the seam's promise for
    /// <see cref="IGpuCommandList.UpdateBuffer{T}(IGpuBuffer, uint, in T)"/>, and it is what a headless render loop, such
    /// as <see cref="KhaozEngine.Render3D.Render3DSnapshot"/>, and a frame that opens more than one recording both rely
    /// on. Within ONE list the last write still decides every draw (see <see cref="RecordTimeUniformRewriteGpuTests"/>),
    /// which is a different contract.
    /// <para>
    /// A ring that rotated its uniform segment only at a present gave every list of such a loop the same segment, so a
    /// later list's write landed in memory an earlier, still queued list had yet to read, and every target read the last
    /// list's value. A temporal frame then rasterised and resolved with the newest frame's jitter, and the history
    /// accumulated one jittered frame many times over.
    /// </para>
    /// </summary>
    public sealed class ConsecutiveSubmissionUniformsGpuTests
    {
        const uint Size = 64;
        const int Lists = 8;

        [GpuFact]
        public void Consecutive_submissions_without_a_present_each_draw_their_own_uniform_value()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            IGpuResourceFactory f = gd.Factory;

            var textures = new IGpuTexture[Lists];
            var framebuffers = new IGpuFramebuffer[Lists];
            for (int i = 0; i < Lists; i++)
            {
                textures[i] = f.CreateTexture(GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                framebuffers[i] = f.CreateFramebuffer(null, textures[i]);
            }

            using IGpuBuffer tint = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vertices = f.CreateBuffer(new GpuBufferDescription(6 * sizeof(float), GpuBufferUsage.VertexBuffer));
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tint", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, tint));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(TintVert, TintFrag);
            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [layout],
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Pos", GpuVertexElementFormat.Float2)),
                },
                Outputs = framebuffers[0].Outputs,
            });
            gd.UpdateBuffer(vertices, 0, FullScreenTriangle);

            try
            {
                using IGpuCommandList cl = f.CreateCommandList();
                for (int i = 0; i < Lists; i++)
                {
                    Vector4 value = Value(i);
                    using (GpuRecording.Open(gd, cl, nameof(ConsecutiveSubmissionUniformsGpuTests)))
                    {
                        cl.UpdateBuffer(tint, 0, in value);
                        cl.SetFramebuffer(framebuffers[i]);
                        cl.ClearColorTarget(0, Color.Black);
                        cl.SetPipeline(pipeline);
                        cl.SetGraphicsResourceSet(0, set);
                        cl.SetVertexBuffer(0, vertices);
                        cl.Draw(3);
                    }
                    gd.Submit(cl);   // no present and no wait: the next list records while this one may still be queued
                }
                gd.WaitForIdle();
                Assert.Null(gd.Diagnostics.DeviceLossReason);

                var read = new string[Lists];
                int wrong = 0;
                for (int i = 0; i < Lists; i++)
                {
                    byte[] rgba = GpuReadback.ToRgba(gd, textures[i], (int)Size, (int)Size);
                    int at = ((int)(Size / 2) * (int)Size + (int)(Size / 2)) * 4;
                    int expected = Level(i);
                    read[i] = $"{rgba[at]}";
                    if (Math.Abs(rgba[at] - expected) > 1 || rgba[at + 2] != 255) wrong++;
                }
                Assert.True(wrong == 0, $"{wrong} of {Lists} lists drew another list's uniform value on {gd.Backend}: "
                    + $"red read {string.Join(", ", read)}, each list wrote {string.Join(", ", Levels())}");
            }
            finally
            {
                for (int i = 0; i < Lists; i++)
                {
                    framebuffers[i].Dispose();
                    textures[i].Dispose();
                }
            }
        }

        // Distinct red levels a multiple of 17 apart, exact in an 8-bit target, with blue at 1 to show the draw landed.
        static int Level(int list) => 17 * (list + 3);
        static Vector4 Value(int list) => new(Level(list) / 255f, 0f, 1f, 1f);

        static IEnumerable<int> Levels()
        {
            for (int i = 0; i < Lists; i++) yield return Level(i);
        }

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
