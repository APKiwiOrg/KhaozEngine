using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="GroundDecal"/>s as a far-plane fullscreen pass per decal into the lit color
    /// attachment + read-only scene depth (ColorDepthFB), sampling the linear depth to reconstruct each pixel's
    /// surface world position and painting the decal's analytic shape onto the ground/terrain. Runs after the
    /// model+beam passes and before the post chain, so decals are occluded by geometry (the read-only depth test
    /// rejects no-geometry background; the Y-band gate keeps shapes off vertical faces) and flow through
    /// quantize/blit. One draw per decal; each reads its own params from its OWN slot of a shared dynamic-offset UBO
    /// (the slot is selected per draw by a byte offset), so no two decals share or overwrite a slot. Two pipelines:
    /// alpha and additive.
    /// </summary>
    internal sealed class GroundDecalRenderer : IDisposable
    {
        /// <summary>160-byte UBO matching the Decal block in <see cref="ShaderSources.DecalFrag"/>
        /// (mat4 + 6 vec4; every member 16-byte aligned, so std140 needs no extra padding).</summary>
        public struct DecalUbo
        {
            public Matrix4x4 InvViewProj; // 64
            public Vector4 Center;        // xyz center, w=rotation
            public Vector4 Size;
            public Vector4 Fill;
            public Vector4 Outline;
            public Vector4 Params;        // x=edge, y=fillFraction, z=flashAdd, w=shapeIndex
            public Vector4 Gate;          // x=groundY, y=yTol, z=maxStep, w=0
        }

        // Per-decal stride in the shared UBO: each decal's params occupy their OWN 256-byte slot, selected at draw
        // time by a dynamic offset (i * SlotBytes). Distinct, never-overwritten slots mean every draw reads its own
        // data regardless of how a backend orders mid-command-list buffer updates against the draws - the robust
        // per-draw UBO pattern the engine prefers over re-uploading one shared slot per draw (which leans on an
        // implicit write-after-read barrier). The payload is the 160-byte DecalUbo, padded to the 256-byte
        // dynamic-offset alignment that is safe across Metal/D3D11/Vulkan.
        const int PayloadBytes = 160;
        const int SlotBytes = 256;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuPipeline _alphaPipe, _additivePipe;
        readonly List<IDisposable> _retired = new();
        IGpuBuffer? _ubo;       // grown geometrically to hold _capacity slots; a regrown buffer is retired (a prior
        int _capacity;          // frame's command list may still read it) and freed in Dispose.
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundW, _boundH;

        public GroundDecalRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.DecalVert, ShaderSources.DecalFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                // Dynamic-offset UBO: the set binds a 160-byte window; each draw supplies its slot's byte offset.
                new GpuResourceLayoutElement("Decal", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment, dynamic: true)));
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, GpuBlendAttachment blend) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                // Read-only depth test: the far-plane quad (DecalVert) passes Greater only where stored depth is
                // nearer than the far plane, i.e. only on scene geometry; background (cleared far) is rejected. No
                // depth write, so the scene depth is untouched for any later pass.
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Greater),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            // Rebuild when the targets change OR the set is stale (the UBO was regrown - the set holds a range into
            // it). The dynamic-offset binding is offset 0 + the 160-byte window; the per-draw offset is supplied to
            // SetGraphicsResourceSet at draw time.
            if (_set != null && ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex, _gd.PointSampler,
                new GpuBufferRange(_ubo!, 0, PayloadBytes)));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }

        /// <summary>Ensure the UBO holds at least <paramref name="decalCount"/> 256-byte slots, growing geometrically.
        /// A regrown buffer retires the old one (a prior frame's command list may still read it) and drops the set so
        /// <see cref="BindTargets"/> rebuilds its range against the new buffer.</summary>
        void EnsureCapacity(int decalCount)
        {
            if (_ubo != null && _capacity >= decalCount) return;
            if (_ubo != null) _retired.Add(_ubo);
            _capacity = Math.Max(decalCount, _capacity == 0 ? 8 : _capacity * 2);
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * SlotBytes), GpuBufferUsage.UniformBuffer));
            _set?.Dispose(); _set = null;
        }

        /// <summary>Pure: pack a decal + the (raw, inverted) view-projection into the UBO.</summary>
        public static DecalUbo PackUbo(in GroundDecal d, Matrix4x4 invViewProj)
        {
            Vector4 fill = d.FillColor; Vector4 outline = d.OutlineColor;
            return new DecalUbo
            {
                InvViewProj = invViewProj,
                Center = new Vector4(d.Center, d.Rotation),
                Size = d.Size,
                Fill = fill,
                Outline = outline,
                Params = new Vector4(d.EdgeThickness, d.FillFraction, d.FlashAdd, (int)d.Shape),
                Gate = new Vector4(d.Center.Y, d.YTolerance, d.MaxStep, 0f),
            };
        }

        /// <summary>Draw all queued decals into ColorDepthFB (lit color + read-only scene depth). Caller guarantees
        /// the model pass is complete (depth written) and the framebuffer is free to rebind. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, ReadOnlySpan<GroundDecal> decals)
        {
            if (decals.Length == 0) return;
            EnsureCapacity(decals.Length);
            BindTargets(res);
            // Reconstruct with the RAW view-projection inverse (NOT GpuClip-corrected): the decal frag does a
            // screen->world unprojection like Camera.ScreenToRay picking, which is CPU/backend-independent. Using
            // the clip-corrected matrix here desynced Vulkan; combined with the frag's texelFetch(gl_FragCoord)
            // sampling (no UV Y-origin dependence), the reconstruction is now uniform across all backends.
            Matrix4x4.Invert(viewProj, out var inv);
            // Pack each decal into its OWN slot first (distinct offsets, never overwritten). Doing all the uploads
            // before any draw - and binding the framebuffer once - means each draw reads exactly its decal's params
            // no matter how the backend orders the buffer copies relative to the draws.
            for (int i = 0; i < decals.Length; i++)
            {
                var u = PackUbo(decals[i], inv);
                cl.UpdateBuffer(_ubo!, (uint)(i * SlotBytes), in u);
            }
            cl.SetFramebuffer(res.ColorDepthFB);
            for (int i = 0; i < decals.Length; i++)
            {
                cl.SetPipeline(decals[i].Blend == DecalBlend.Additive ? _additivePipe : _alphaPipe);
                cl.SetGraphicsResourceSet(0, _set!, (uint)(i * SlotBytes));   // dynamic offset selects decal i's slot
                cl.Draw(3);
            }
        }

        public void Dispose()
        {
            _set?.Dispose();
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _layout.Dispose(); _shaders.Dispose();
            _ubo?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
