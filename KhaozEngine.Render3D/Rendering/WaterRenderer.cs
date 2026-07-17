using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="WaterPlane"/> as an animated, flat, alpha-blended surface into the lit color
    /// attachment + read-only scene depth (ColorDepthFB), sampling the resolved scene depth to soften the alpha
    /// near the shore. Runs AFTER the sky and the ground-decal passes and BEFORE <see cref="RenderResources.ResolveColor"/>,
    /// so it is occluded by geometry above it (depth test ON) but never corrupts the normal/linear-depth MRT the
    /// outline pass reads (depth WRITE off - see the in-source note on <see cref="ShaderSources.WaterVert"/>). One
    /// draw per queued plane (its own dynamic-offset UBO slot, mirroring <see cref="GroundDecalRenderer"/>'s
    /// per-decal slot pattern so multiple planes never share/overwrite one slot regardless of backend buffer-write
    /// ordering).
    /// </summary>
    internal sealed class WaterRenderer : IDisposable
    {
        /// <summary>Packed water-plane UBO matching the <c>Water</c> block in <see cref="ShaderSources.WaterFrag"/>
        /// (2 mat4 + 8 vec4; every member 16-byte aligned, so std140 needs no extra padding).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WaterUbo
        {
            public Matrix4x4 ViewProj;
            public Matrix4x4 InvViewProj;
            public Vector4 LightDir;
            public Vector4 LightColor;
            public Vector4 CameraPos;
            public Vector4 DeepColor;
            public Vector4 HorizonColor;
            public Vector4 WaveParams;    // x=waveScale, y=waveSpeed, z=normalStrength, w=time
            public Vector4 ShoreGlint;    // x=shoreFadeDistance, y=glintStrength, z=glintExponent, w=opacity
            public Vector4 Res;           // xy = 1/renderWidth, 1/renderHeight
        }

        /// <summary>Byte size of <see cref="WaterUbo"/>. 2*64 (mat4) + 8*16 (vec4) = 256.</summary>
        internal const uint PayloadBytes = 256;
        // Per-plane stride in the shared UBO: each plane's params occupy their OWN slot, selected at draw time by a
        // dynamic offset (i * SlotBytes), matching the GroundDecalRenderer precedent so a multi-plane frame never
        // shares/overwrites a slot no matter how a backend orders buffer writes vs draws. PayloadBytes (256) is
        // already a dynamic-offset-safe alignment, so the slot stride equals the payload exactly.
        const int SlotBytes = 256;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipe;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        IGpuBuffer? _ubo;     // grown geometrically to hold _capacity slots
        int _capacity;
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundGen;
        // Fixed-size grid buffers: every WaterPlane draws through the SAME GridResolution grid (only the CPU-side
        // vertex positions differ per plane, re-uploaded per draw), so these are allocated once and never regrown.
        IGpuBuffer? _vb;
        IGpuBuffer? _ib;

        public WaterRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.WaterVert, ShaderSources.WaterFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                // Dynamic-offset UBO read by BOTH stages (the vertex shader only needs ViewProj, folded into the
                // same buffer per the one-UBO-per-set rule).
                new GpuResourceLayoutElement("Water", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
            _pipe = Pipe(f, colorOutput);
        }

        /// <summary>Rebuild the pipeline for a new colour-target output description (e.g. the MRT became
        /// multisampled for MSAA). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _pipe.Dispose();
            _pipe = Pipe(_gd.Factory, colorOutput);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3));
            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },
                // Depth test ON (Less) so terrain/props above the surface occlude the water; depth WRITE off so the
                // resolved normal/linear-depth MRT the outline pass reads stays exactly what the opaque geometry
                // wrote (see the WaterVert in-source note for the full reasoning).
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Less),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = outputs,
            });
        }

        void BindTargets(RenderResources res)
        {
            if (_set != null && ReferenceEquals(_bound, res) && res.Generation == _boundGen) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex, _gd.PointSampler,
                new GpuBufferRange(_ubo!, 0, PayloadBytes)));
            _bound = res; _boundGen = res.Generation;
        }

        /// <summary>Ensure the UBO holds at least <paramref name="planeCount"/> slots, growing geometrically. A
        /// regrown buffer drops the set so <see cref="BindTargets"/> rebuilds its range against the new buffer.</summary>
        void EnsureUboCapacity(int planeCount)
        {
            if (_ubo != null && _capacity >= planeCount) return;
            _capacity = Math.Max(planeCount, _capacity == 0 ? 4 : _capacity * 2);
            _ubo?.Dispose();
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * SlotBytes), GpuBufferUsage.UniformBuffer));
            _set?.Dispose(); _set = null;
        }

        void EnsureGridBuffers()
        {
            if (_vb != null && _ib != null) return;
            const uint vcount = WaterMath.GridResolution * WaterMath.GridResolution;
            const uint icount = WaterMath.GridIndexCount;
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(vcount * 12u, GpuBufferUsage.VertexBuffer));   // Vector3 = 12 bytes
            _ib = _gd.Factory.CreateBuffer(new GpuBufferDescription(icount * sizeof(uint), GpuBufferUsage.IndexBuffer));
            Span<uint> indices = stackalloc uint[(int)icount];
            WaterMath.BuildGridIndices(indices);
            _gd.UpdateBuffer(_ib, 0, indices.ToArray());
        }

        /// <summary>Pure: pack one plane + the frame's light/camera/settings into the UBO. <paramref name="rawViewProj"/>
        /// is the RAW (not clip-corrected) view-projection so the fragment's depth reconstruction matches the
        /// ground-decal convention; <paramref name="clipViewProj"/> is the SEPARATE clip-corrected copy for
        /// <c>gl_Position</c> (mirrors every other pass in this file, e.g. <see cref="GpuClip.Correct"/> at the
        /// <see cref="Draw"/> call site).</summary>
        public static WaterUbo PackUbo(Matrix4x4 clipViewProj, Matrix4x4 rawViewProj, Vector3 lightDirection,
            Color lightColor, Vector3 cameraPos, WaterSettings settings, float timeSeconds, int renderWidth, int renderHeight)
        {
            Matrix4x4.Invert(rawViewProj, out var inv);
            Vector4 deep = settings.DeepColor;
            Vector4 horizon = settings.HorizonColor;
            Vector4 lightCol = lightColor;
            float invW = renderWidth > 0 ? 1f / renderWidth : 0f;
            float invH = renderHeight > 0 ? 1f / renderHeight : 0f;
            return new WaterUbo
            {
                ViewProj = clipViewProj,
                InvViewProj = inv,
                LightDir = new Vector4(lightDirection, 0f),
                LightColor = lightCol,
                CameraPos = new Vector4(cameraPos, 0f),
                DeepColor = deep,
                HorizonColor = horizon,
                WaveParams = new Vector4(settings.WaveScale, settings.WaveSpeed, settings.NormalStrength, timeSeconds),
                ShoreGlint = new Vector4(settings.ShoreFadeDistance, settings.GlintStrength, settings.GlintExponent, settings.Opacity),
                Res = new Vector4(invW, invH, 0f, 0f),
            };
        }

        /// <summary>Draw all queued water planes into ColorDepthFB (lit colour + read-only scene depth). Caller
        /// guarantees the model + sky + decal passes are complete and the framebuffer is free to rebind. No-op when
        /// <paramref name="planes"/> is empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, ReadOnlySpan<WaterPlane> planes,
            Matrix4x4 viewProj, Vector3 lightDirection, Color lightColor, Vector3 cameraPos, WaterSettings settings, float timeSeconds)
        {
            if (planes.Length == 0) return;
            EnsureUboCapacity(planes.Length);
            EnsureGridBuffers();
            BindTargets(res);

            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            Span<Vector3> gridPos = stackalloc Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
            for (int i = 0; i < planes.Length; i++)
            {
                var u = PackUbo(clipVp, viewProj, lightDirection, lightColor, cameraPos, settings, timeSeconds, res.Width, res.Height);
                cl.UpdateBuffer(_ubo!, (uint)(i * SlotBytes), in u);
            }

            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(_pipe);
            cl.SetIndexBuffer(_ib!, GpuIndexFormat.UInt32);
            for (int i = 0; i < planes.Length; i++)
            {
                int n = WaterMath.BuildGridPositions(planes[i], gridPos);
                cl.UpdateBuffer<Vector3>(_vb!, 0, gridPos.Slice(0, n));
                cl.SetGraphicsResourceSet(0, _set!, (uint)(i * SlotBytes));
                cl.SetVertexBuffer(0, _vb!);
                cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, 0, 0);
            }
        }

        public void Dispose()
        {
            _set?.Dispose();
            _pipe.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo?.Dispose();
            _vb?.Dispose();
            _ib?.Dispose();
        }
    }
}
