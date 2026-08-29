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
    /// Draws entity SILHOUETTES (the per-entity highlight: a clicked monster, a selected prop) as inverted
    /// hulls INTO the model MRT framebuffer, after the overlay meshes and before the depth/normal resolve. Each
    /// queued draw re-renders a model mesh with its vertices pushed along their world normals by a width in
    /// metres, FRONT faces culled (the shadow pass's precedent, and the whole trick: the pushed-out back faces
    /// form a rim around the model's own silhouette), flat colour from the draw's uniform slot, depth tested
    /// less-or-equal without writing so nearer scene geometry occludes the rim and the rim never occludes the
    /// passes that follow.
    /// <para>
    /// The per-draw dynamic-offset UBO pattern is <see cref="OverlayMeshRenderer"/>'s, one 256-byte slot per
    /// draw packed into a CPU image and uploaded in one whole-buffer write (#408's D3D11 partial-write stall).
    /// The payload grows to 160 bytes (ViewProj + World + Color + Params), still inside one slot.
    /// </para>
    /// </summary>
    internal sealed class SilhouetteRenderer : IDisposable
    {
        // ViewProj (64) + World (64) + Color (16) + Params (16) = 160 bytes, in a 256-byte dynamic-offset slot.
        const int PayloadBytes = 160;
        const int SlotBytes = 256;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipeline;
        readonly List<IDisposable> _retired = new();
        IGpuBuffer? _ubo;
        int _capacity;
        IGpuResourceSet? _set;
        Matrix4x4 _viewProj;

        byte[] _image = Array.Empty<byte>();
        readonly List<QueuedDraw> _queue = new();

        public SilhouetteRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.SilhouetteVert, ShaderSources.SilhouetteFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                // Read in BOTH stages: the vertex stage takes ViewProj/World/Params, the fragment stage takes
                // Color, and one dynamic-offset window serves both.
                new GpuResourceLayoutElement("Draw", GpuResourceKind.UniformBuffer,
                    GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));
            _pipeline = BuildPipeline(f, modelOutputs);
        }

        /// <summary>Rebuild the pipeline for a new model-MRT output description (MSAA changes).</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs)
        {
            _pipeline.Dispose();
            _pipeline = BuildPipeline(_gd.Factory, modelOutputs);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory f, GpuOutputDescription modelOutputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));

            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[]
                {
                    GpuBlendAttachment.AlphaBlend,
                    GpuBlendAttachment.PreserveDestination,
                    GpuBlendAttachment.PreserveDestination,
                },
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                // FRONT-face culling is the inverted hull: only the pushed-out back faces draw, and the model's
                // own depth (written by the opaque pass) eats the hull's interior, leaving the rim. FrontFace is
                // COUNTER-clockwise here because the engine's meshes wind CCW-front: declared clockwise, the
                // front cull kept the shell's NEAR side and painted the whole model (caught by the first bake's
                // evidence PNG, a fully crimson box).
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Front, GpuPolygonFill.Solid,
                    GpuFrontFace.CounterClockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Cache this frame's clip-corrected view-projection. Call once before the draw loop.</summary>
        public void BeginFrame(Matrix4x4 clipCorrectedViewProj) => _viewProj = clipCorrectedViewProj;

        /// <summary>Queue one silhouette and pack its UBO slot. <see cref="Flush"/> uploads and records.</summary>
        public void Enqueue(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
            int drawIndex, Matrix4x4 world, Color color, float widthMetres)
        {
            var slot = new DrawUbo
            {
                ViewProj = _viewProj,
                World = world,
                Color = color.ToVector4(),
                Params = new Vector4(widthMetres, 0f, 0f, 0f),
            };
            MemoryMarshal.Write(_image.AsSpan(drawIndex * SlotBytes, SlotBytes), in slot);
            _queue.Add(new QueuedDraw(vb, ib, indexCount, indexFormat, drawIndex));
        }

        /// <summary>Upload every packed slot in ONE whole-buffer write, then record the queued draws into the
        /// model FB (already bound). A frame that queued nothing records nothing.</summary>
        public void Flush(IGpuCommandList cl)
        {
            if (_queue.Count == 0) return;
            cl.UpdateBuffer(_ubo!, 0, (ReadOnlySpan<byte>)_image);
            foreach (QueuedDraw d in _queue)
            {
                cl.SetPipeline(_pipeline);
                cl.SetGraphicsResourceSet(0, _set!, (uint)(d.DrawIndex * SlotBytes));
                cl.SetVertexBuffer(0, d.Vb);
                cl.SetIndexBuffer(d.Ib, d.IndexFormat);
                cl.DrawIndexed((uint)d.IndexCount, 1, 0, 0, 0);
            }
            _queue.Clear();
        }

        /// <summary>Ensure the UBO holds at least <paramref name="drawCount"/> slots, growing geometrically and
        /// RETIRING (never inline-disposing) an outgrown buffer and its set, the shared buffer-lifetime rule.</summary>
        public void EnsureCapacity(int drawCount)
        {
            if (_ubo != null && _capacity >= drawCount)
            {
                _set ??= CreateSet();
                return;
            }
            if (_ubo != null) _retired.Add(_ubo);
            _capacity = Math.Max(drawCount, _capacity == 0 ? 4 : _capacity * 2);
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * SlotBytes), GpuBufferUsage.UniformBuffer));
            var image = new byte[checked(_capacity * SlotBytes)];
            _image.AsSpan().CopyTo(image);
            _image = image;
            if (_set != null) _retired.Add(_set);
            _set = CreateSet();
        }

        IGpuResourceSet CreateSet() =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, new GpuBufferRange(_ubo!, 0, PayloadBytes)));

        /// <summary>Per-draw payload matching the Draw block in SilhouetteVert/Frag.</summary>
        struct DrawUbo
        {
            public Matrix4x4 ViewProj;
            public Matrix4x4 World;
            public Vector4 Color;
            public Vector4 Params;   // x = width in metres
        }

        readonly record struct QueuedDraw(IGpuBuffer Vb, IGpuBuffer Ib, int IndexCount, GpuIndexFormat IndexFormat, int DrawIndex);

        public void Dispose()
        {
            _set?.Dispose();
            _pipeline.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _ubo?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
