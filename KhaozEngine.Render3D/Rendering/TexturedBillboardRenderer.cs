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

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // one mat4 ViewProj (64 bytes)
        readonly IGpuResourceLayout _layout;   // UBO(vert) + texture(frag) + sampler(frag)
        readonly IGpuSampler _sampler;         // device built-in linear sampler (non-owning)
        readonly IGpuShaderSet _shaders;
        readonly IGpuPipeline[] _pipelines;    // [0] alpha, [1] additive
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public TexturedBillboardRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer)); // mat4 ViewProj

            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            // The same built-in linear sampler the model pass / Render2D use (verified on D3D11/WARP). Non-owning.
            _sampler = gd.LinearSampler;

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4));

            // The model FB has 3 colour attachments (lit colour, encoded normal, linear depth). We only paint
            // colour: attachment 0 gets the chosen blend; attachments 1 & 2 preserve their destination so the
            // outline post-pass still reads the meshes' normal/depth, not the quad's.
            var alphaBlends = new[] { GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination };
            var addBlends = new[] { GpuBlendAttachment.Additive, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination };

            _pipelines = new[]
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
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });

        /// <summary>Build a per-texture resource set (the shared ViewProj UBO + <paramref name="albedo"/> + sampler).
        /// Owned by the caller (Scene3D caches one per texture and disposes them).</summary>
        public IGpuResourceSet CreateTextureSet(IGpuTexture albedo) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo, albedo, _sampler));

        /// <summary>Upload the frame's view-projection once (clip-Y corrected for the live backend) before the draw
        /// runs. The same matrix the model pass uses, so quad depth is comparable to mesh depth.</summary>
        public void SetViewProj(IGpuCommandList cl, Matrix4x4 viewProj)
        {
            var clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            cl.UpdateBuffer(_ubo, 0, in clipVp);
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
            cl.SetGraphicsResourceSet(0, textureSet);
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
            _shaders.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
            // _sampler is the device built-in (non-owning); do not dispose it.
        }
    }
}
