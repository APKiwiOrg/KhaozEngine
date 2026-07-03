using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws additive glowing BEAMS (lasers/thrusters/tethers) INTO the model MRT framebuffer alongside the lit
    /// meshes, so they interleave in depth: the depth test (less-or-equal, no write) reads the meshes' depth, so a
    /// nearer mesh occludes the beam and a beam in front draws over a farther mesh. Attachment 0 is additive;
    /// attachments 1 &amp; 2 (normal, depth) preserve their destination so the edge post-pass ignores the beam. A
    /// camera-facing strip per beam (built by <see cref="BeamGeometry"/>); all beams in ONE draw - the per-beam
    /// style is baked into the vertex, so there is no per-draw uniform rebinding (the Metal/Veldrid mid-list
    /// uniform hazard the skinned-bone path documents).
    /// </summary>
    internal sealed class BeamRenderer : IDisposable
    {
        /// <summary>One beam vertex: world position, (across,along) UV, split core/glow colours, and two packed
        /// param vectors (shape: coreFrac/glowSoftness/taper; anim: pulseSpeed/pulseAmount/scrollSpeed). 84 bytes.</summary>
        internal struct BeamVertex
        {
            public Vector3 Position;
            public Vector2 Uv;
            public Vector4 CoreColor;
            public Vector4 GlowColor;
            public Vector4 Shape;
            public Vector4 Anim;
            public BeamVertex(Vector3 position, Vector2 uv, Vector4 coreColor, Vector4 glowColor, Vector4 shape, Vector4 anim)
            { Position = position; Uv = uv; CoreColor = coreColor; GlowColor = glowColor; Shape = shape; Anim = anim; }
            public const uint SizeInBytes = 84;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FrameUniforms { public Matrix4x4 ViewProj; public Vector4 Time; }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // mat4 ViewProj + vec4 Time (80 bytes)
        readonly IGpuResourceLayout _layout;   // UBO (vertex + fragment)
        readonly IGpuResourceSet _set;
        readonly IGpuShaderSet _shaders;
        IGpuPipeline _pipeline;                // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public BeamRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(80, GpuBufferUsage.UniformBuffer)); // mat4 + vec4
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _shaders = factory.CreateShadersFromSpirv(ShaderSources.BeamVert, ShaderSources.BeamFrag);

            _pipeline = BuildPipeline(factory, modelOutputs);
        }

        /// <summary>Rebuild the pipeline for a new model-MRT output description (e.g. multisampled for MSAA - a
        /// pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs)
        {
            _pipeline.Dispose();
            _pipeline = BuildPipeline(_gd.Factory, modelOutputs);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory factory, GpuOutputDescription modelOutputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("CoreColor", GpuVertexElementFormat.Float4),
                new GpuVertexElement("GlowColor", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Shape", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Anim", GpuVertexElementFormat.Float4));

            // Attachment 0 additive (glow accumulation); normal/depth preserved so the edge pass reads the
            // meshes' normal/depth, not the beam's (no outline traced around the strip).
            var blends = new[] { GpuBlendAttachment.Additive, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination };

            return factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = blends,
                // Read the meshes' depth (interleave/occlude) but don't write it: additive glow must not occlude.
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's view-projection (clip-Y corrected) + time once before the draw.</summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj, float timeSeconds)
        {
            var u = new FrameUniforms
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                Time = new Vector4(timeSeconds, 0f, 0f, 0f),
            };
            cl.UpdateBuffer(_ubo, 0, in u);
        }

        /// <summary>Draw <paramref name="verts"/> (all beams' strips) into <paramref name="target"/> (the model FB,
        /// no clear). <see cref="SetFrameUniforms"/> must have run this frame. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, ReadOnlySpan<BeamVertex> verts, IGpuFramebuffer target)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 64u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * BeamVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _shaders.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
