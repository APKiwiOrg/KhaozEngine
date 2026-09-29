using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The coarse-grid temporal counts behind <see cref="Scene3D.RequestTemporalCounts"/>: a 32 by 18 RGBA8 target
    /// the probe program (<see cref="ShaderSources.TemporalProbeFrag"/>) writes once per request, copied into a
    /// staging texture of the same size, 2.3 KB in all.
    /// <para>
    /// ON REQUEST, BECAUSE THE SEAM HAS NO READBACK THAT NEVER STALLS. A read map drains the whole device on Metal
    /// (<c>MetalGpuDevice.Resources.cs</c> <c>DrainForRead</c>) and on Vulkan (<c>VulkanGpuDevice.Resources.cs</c>
    /// <c>DrainBeforeRead</c>), so a per-frame readback would serialise the CPU and GPU on two of three backends.
    /// A request records the probe on the next frame the resolve runs and <see cref="TryHarvest"/> pays one drain at
    /// the following <c>PrepareFrame</c>. A steady frame records and reads nothing.
    /// </para>
    /// <para>
    /// The probe writes no uniform. It reads the resolve's own uniform buffer through the resolve's set, which the
    /// resolve writes earlier in the same recording, and the grid copy rides the frame's own list after the draw.
    /// </para>
    /// </summary>
    internal sealed class TemporalCountProbe : IDisposable
    {
        internal const int GridWidth = 32, GridHeight = 18, SamplesPerCell = 16;
        internal const int TotalSamples = GridWidth * GridHeight * SamplesPerCell;

        readonly IGpuDevice _gd;
        readonly IGpuTexture _target, _staging;
        readonly IGpuFramebuffer _framebuffer;
        readonly IGpuShaderSet _shaders;
        readonly IGpuPipeline _pipeline;
        readonly byte[] _row = new byte[GridWidth * 4];
        bool _recorded;

        public TemporalCountProbe(IGpuDevice gd, IGpuResourceLayout resolveLayout, TemporalResolvePrecision precision)
        {
            _gd = gd;
            IGpuResourceFactory f = gd.Factory;
            _target = f.CreateTexture(GpuTextureDescription.Texture2D(GridWidth, GridHeight,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _framebuffer = f.CreateFramebuffer(null, _target);
            _staging = f.CreateTexture(GpuTextureDescription.Texture2D(GridWidth, GridHeight,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                ShaderSources.TemporalProbeFragment(precision));
            _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { resolveLayout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = _framebuffer.Outputs,
            });
        }

        /// <summary>How many times the grid has been mapped for reading. For tests.</summary>
        internal int Readbacks { get; private set; }

        /// <summary>Record the probe over this frame's resolve set and copy the grid into the staging
        /// texture.</summary>
        public void Record(IGpuCommandList cl, IGpuResourceSet resolveSet)
        {
            cl.SetFramebuffer(_framebuffer);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, resolveSet);
            cl.Draw(3);
            cl.CopyTexture(_target, _staging);
            _recorded = true;
        }

        /// <summary>Sum the last recorded grid, in samples out of <see cref="TotalSamples"/>. Maps the staging copy,
        /// which drains the device on Metal and Vulkan, so call it outside any recording and only on request. False
        /// when nothing was recorded since the last harvest.</summary>
        public bool TryHarvest(out int disoccluded, out int reactive, out int clipped)
        {
            disoccluded = reactive = clipped = 0;
            if (!_recorded) return false;
            _recorded = false;
            Readbacks++;
            MappedData map = _gd.Map(_staging, GpuMapMode.Read);
            try
            {
                for (int y = 0; y < GridHeight; y++)
                {
                    Marshal.Copy(IntPtr.Add(map.Data, y * (int)map.RowPitch), _row, 0, _row.Length);
                    for (int x = 0; x < GridWidth; x++)
                    {
                        disoccluded += Samples(_row[x * 4]);
                        reactive += Samples(_row[x * 4 + 1]);
                        clipped += Samples(_row[x * 4 + 2]);
                    }
                }
            }
            finally
            {
                _gd.Unmap(_staging);
            }
            return true;
        }

        /// <summary>Invert the probe's k / 16 in an 8-bit channel, exactly for every k from 0 to 16.</summary>
        internal static int Samples(byte stored) => (stored * SamplesPerCell + 127) / 255;

        public void Dispose()
        {
            _pipeline.Dispose();
            _shaders.Dispose();
            _framebuffer.Dispose();
            _staging.Dispose();
            _target.Dispose();
        }
    }
}
