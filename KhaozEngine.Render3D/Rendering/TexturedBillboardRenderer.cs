using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws textured, sprite-sheet-frame billboards INTO the model MRT framebuffer (alongside the lit meshes),
    /// so they interleave correctly in depth: the depth test (less-or-equal, no write) reads the meshes' depth, so
    /// a nearer mesh occludes a quad behind it and a quad in front draws over a mesh behind it. Depth write is off
    /// and the host submits back-to-front, so overlapping quads blend in order. Reuses
    /// <see cref="BillboardRenderer.BillboardVertex"/> (pos/uv/colour). Two pipelines share the layout/UBO/shaders:
    /// [0] alpha, [1] additive. Each draw binds one texture's resource set (UBO + albedo + sampler).
    /// </summary>
    internal sealed class TexturedBillboardRenderer : IDisposable
    {
        const int AlphaPipeline = 0;
        const int AdditivePipeline = 1;

        readonly AtmosphereBindings _atmosphere;
        readonly bool _ownsAtmosphere;
        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // two aligned ViewProj + FogBlend windows
        internal const uint FramePayloadBytes = 80;
        internal const uint FrameSlotBytes = 256;
        readonly IGpuResourceLayout _layout;   // UBO(vert) + texture(frag) + sampler(frag)
        readonly IGpuSampler _sampler;         // device built-in linear sampler (non-owning)
        readonly IGpuShaderSet _shaders;
        readonly ModelVisibilityShaders _visibilityShaders = new();
        IGpuShaderSet? _motionShaders;   // TexturedBillboardMotionFrag, built only for a temporal model target
        IGpuPipeline[] _pipelines;             // [0] alpha, [1] additive; rebuilt by SetOutputs on MSAA change
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public TexturedBillboardRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs, AtmosphereBindings? atmosphere = null)
        {
            _gd = gd;
            _ownsAtmosphere = atmosphere is null;
            _atmosphere = atmosphere ?? new AtmosphereBindings(gd);
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(2 * FrameSlotBytes, GpuBufferUsage.UniformBuffer));
            var uniforms = new AtmosphereBindings.FogBlendUniforms { ViewProj = Matrix4x4.Identity, FogBlend = Vector4.Zero };
            gd.UpdateBuffer(_ubo, 0, in uniforms);
            uniforms.FogBlend = Vector4.UnitX;
            gd.UpdateBuffer(_ubo, FrameSlotBytes, in uniforms);

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(AtmosphereBindings.AppendLayout(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true),
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment))));

            // The same built-in linear sampler the model pass / Render2D use (verified on D3D11/WARP). Non-owning.
            _sampler = gd.LinearSampler;

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag);

            _pipelines = BuildPipelines(factory, modelOutputs);
        }

        /// <summary>Rebuild the pipelines for a new model-MRT output description (e.g. it became multisampled for
        /// MSAA - a pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs, GpuRetireQueue? retired = null)
        {
            if (!ShadowVisibilityTargets.HasVisibility(modelOutputs)) _visibilityShaders.Release(retired);
            foreach (var p in _pipelines) ResourceRetirement.Free(p, retired);
            _pipelines = BuildPipelines(_gd.Factory, modelOutputs);
            if (MotionMath.IsTemporal(modelOutputs)) return;
            // The old pipelines and their temporal program share the completion boundary.
            ResourceRetirement.Free(_motionShaders, retired);
            _motionShaders = null;
        }

        /// <summary>Whether the temporal program is held. For tests.</summary>
        internal bool HoldsMotionShadersForTests => _motionShaders is not null;

        IGpuPipeline[] BuildPipelines(IGpuResourceFactory factory, GpuOutputDescription modelOutputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // The model FB has 3 colour attachments, and a 4th (motion) while temporal rendering is active. Colour
            // blends, and every other attachment keeps its destination, so the edge pass reads the meshes' normal and
            // depth and the resolve reads their motion.
            var alphaBlends = ModelTargetBlends.Transparent(GpuBlendAttachment.AlphaBlend, modelOutputs);
            var addBlends = ModelTargetBlends.Transparent(GpuBlendAttachment.Additive, modelOutputs);

            return new[]
            {
                CreatePipeline(factory, modelOutputs, vertexLayout, alphaBlends),
                CreatePipeline(factory, modelOutputs, vertexLayout, addBlends),
            };
        }

        IGpuPipeline CreatePipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs,
            GpuVertexLayoutDescription vertexLayout, GpuBlendAttachment[] blends) =>
            factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = blends,
                // Read the meshes' depth (interleave) but don't write it: transparent quads must not occlude each
                // other; ordering comes from submission / the host's back-to-front sort.
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _visibilityShaders.TrySelect(factory, modelOutputs, ShaderSources.BillboardVert,
                    MotionMath.IsTemporal(modelOutputs) ? ShaderSources.TexturedBillboardMotionFrag : ShaderSources.TexturedBillboardFrag, preserve: true, out IGpuShaderSet visibilityShaders)
                    ? visibilityShaders : MotionMath.IsTemporal(modelOutputs)
                        ? _motionShaders ??= factory.CreateShadersFromSpirv(ShaderSources.BillboardVert, ShaderSources.TexturedBillboardMotionFrag)
                        : _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });

        /// <summary>Build a per-texture resource set (the shared ViewProj UBO + <paramref name="albedo"/> + sampler).
        /// Owned by the caller (Scene3D caches one per texture and disposes them).</summary>
        public IGpuResourceSet CreateTextureSet(IGpuTexture albedo) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _atmosphere.AppendResources(new GpuBufferRange(_ubo, 0, FrameSlotBytes), albedo, _sampler)));

        /// <summary>Upload the frame's view-projection once (clip-Y corrected for the live backend) before the draw
        /// runs. The same matrix the model pass uses, so quad depth is comparable to mesh depth.</summary>
        public void SetViewProj(IGpuCommandList cl, Matrix4x4 viewProj)
        {
            var frame = _atmosphere.PackFogBlendFrame(GpuClip.Correct(viewProj, _gd.Capabilities));
            cl.UpdateBuffer(_ubo, 0, in frame);
            if (_atmosphere.Enabled)
            {
                frame.FogBlend.X = 1f;
                cl.UpdateBuffer(_ubo, FrameSlotBytes, in frame);
            }
        }

        /// <summary>Draw <paramref name="verts"/> (a run of one texture's quads) into <paramref name="target"/>
        /// (the model FB, no clear) with <paramref name="textureSet"/> bound, using the additive pipeline when
        /// <paramref name="additive"/> else alpha. <see cref="SetViewProj"/> must have run this frame. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, ReadOnlySpan<BillboardRenderer.BillboardVertex> verts,
            IGpuFramebuffer target, IGpuResourceSet textureSet, bool additive)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipelines[additive ? AdditivePipeline : AlphaPipeline]);
            cl.SetGraphicsResourceSet(0, textureSet, additive && _atmosphere.Enabled ? FrameSlotBytes : 0u);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 256u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * BillboardRenderer.BillboardVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            foreach (var p in _pipelines) p.Dispose();
            _shaders.Dispose(); _visibilityShaders.Dispose();
            _motionShaders?.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
            // _sampler is the device built-in (non-owning); do not dispose it.
            if (_ownsAtmosphere) _atmosphere.Dispose();
        }
    }
}
