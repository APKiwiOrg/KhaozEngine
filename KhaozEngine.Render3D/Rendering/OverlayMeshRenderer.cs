using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws translucent, UNLIT overlay meshes (collision proxies first; nav / AoI / chunk-bounds layers later) INTO
    /// the model MRT framebuffer, after the meshes/billboards/beams and before the post chain. The depth test is on
    /// (less-or-equal, no write), so a proxy is occluded by nearer scene geometry yet still blends over farther
    /// geometry; depth is never written, so the overlay never occludes the real scene passes that follow. Colour is
    /// the mesh's own per-vertex <see cref="ModelVertex.Color"/> (unlit); alpha comes from the AlphaBlend attachment.
    ///
    /// Each queued draw supplies its world matrix via its OWN slot of a shared dynamic-offset UBO (the same robust
    /// per-draw UBO pattern <see cref="GroundDecalRenderer"/> uses): the buffer holds one 128-byte payload per draw
    /// (frame ViewProj + per-draw World), padded to a 256-byte dynamic-offset alignment, and each draw binds its
    /// slot by a byte offset. This renderer draws ONE overlay mesh per draw call (never instanced - proxy counts
    /// are low), so there is no per-instance vertex buffer here to begin with. Both matrices live in ONE uniform
    /// buffer because a SECOND UBO in a set mis-binds on Metal (see the shader note), not because of any
    /// per-instance-attribute limitation - per-instance vertex ATTRIBUTES consumed directly by the vertex shader
    /// ARE fine on Metal (<see cref="ModelRenderer"/>'s rigid instanced path uses real ones, instanceStepRate 1,
    /// in production, proven by its multi-instance tests). The actual Metal invariant, bisected via the skinned
    /// bone palette: a vertex shader must NOT index a SEPARATE buffer BY a per-instance attribute's value - that
    /// is what corrupted past element 0 in the windowed Veldrid/Metal swapchain context, which is why skinned
    /// meshes are deformed on the CPU instead of reading a per-instance bone index into a GPU bone buffer (see
    /// <see cref="ModelRenderer"/> and docs/USING-KHAOZENGINE.md's GPU-backend gotchas note).
    /// </summary>
    internal sealed class OverlayMeshRenderer : IDisposable
    {
        // Payload per draw: two mat4 (ViewProj + World) = 128 bytes. Each draw's payload occupies its own 256-byte
        // slot (the Metal/D3D11/Vulkan-safe dynamic-offset alignment), selected at draw time by a byte offset, so no
        // two draws share or overwrite a slot regardless of how a backend orders the mid-command-list buffer copies.
        const int PayloadBytes = 128;
        const int SlotBytes = 256;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipeline;   // rebuilt by SetOutputs when the MRT sample count (MSAA) changes
        // Grown-out UBOs and the sets that ranged over them (a prior frame's submitted command list may still be
        // reading one), freed in Dispose. The engine's buffer-lifetime rule, stated in
        // ModelRenderer.EnsureInstanceCapacity and copied by every sibling renderer that grows a buffer.
        readonly List<IDisposable> _retired = new();
        IGpuBuffer? _ubo;      // grown geometrically to hold _capacity slots; a regrown buffer is retired and freed in Dispose
        int _capacity;
        IGpuResourceSet? _set; // binds the 128-byte window into _ubo at offset 0; per-draw offset supplied at draw time
        Matrix4x4 _viewProj;   // this frame's clip-corrected view-projection (set by BeginFrame, written into every slot)

        // THE SLOTS AS ONE CONTIGUOUS CPU IMAGE, and the queue that lets them all be packed before the first draw.
        // A per-draw UpdateBuffer used to sit between the draws, and a PARTIAL write to a non-Dynamic uniform buffer
        // is the shape Veldrid's D3D11CommandList.UpdateBufferCore sent down its staging route: rent a staging
        // buffer, hand it to GraphicsDevice.UpdateBuffer, which Maps the IMMEDIATE context with D3D11_MAP_WRITE (not
        // WRITE_DISCARD, no DO_NOT_WAIT) and blocks until the GPU has released the buffer being recycled. Only a
        // write covering the whole buffer from offset 0 takes the cheap UpdateSubresource path, so a ten-proxy frame
        // was ten CPU/GPU sync points inside the overlay encode and is one UpdateSubresource now (#408, the same
        // packing ModelRenderer.FrameUbo.cs gave the model frame block in 17.18.0). Metal and Vulkan have no such
        // split. Each slot holds exactly the bytes the per-draw write put there, at the same base, so the pass
        // renders the same picture; what differs is the 128-byte pad each slot carries past its payload and the
        // slots past this frame's draw count, neither of which any dynamic offset binds.
        byte[] _image = Array.Empty<byte>();
        readonly List<QueuedDraw> _queue = new();

        public OverlayMeshRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitFrag);

            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                // Dynamic-offset UBO: the set binds a 128-byte window (ViewProj + World); each draw supplies its slot's
                // byte offset. Read in the vertex stage only.
                new GpuResourceLayoutElement("Draw", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));

            _pipeline = BuildPipeline(f, modelOutputs);
        }

        /// <summary>Rebuild the pipeline for a new model-MRT output description (e.g. multisampled for MSAA - a
        /// pipeline's sample count must match its framebuffer). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription modelOutputs)
        {
            _pipeline.Dispose();
            _pipeline = BuildPipeline(_gd.Factory, modelOutputs);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory f, GpuOutputDescription modelOutputs)
        {
            // Full ModelVertex layout so the model pass's GPU vertex buffer binds unchanged. Only Position (0) and
            // Color (2) carry meaning in the shader, and the rest are held live by its 1e-30 sink so the emitted
            // D3D11 vertex input signature stays gap-free (see the hazard note above OverlayUnlitVert).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));

            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                // Model FB has 3 colour attachments (lit colour, encoded normal, linear depth). Alpha-blend colour;
                // preserve the meshes' normal/depth so the edge pass still reads geometry, not the proxy.
                BlendAttachments = new[]
                {
                    GpuBlendAttachment.AlphaBlend,
                    GpuBlendAttachment.PreserveDestination,
                    GpuBlendAttachment.PreserveDestination,
                },
                // Read the scene depth (occlude behind geometry) but do NOT write it (the overlay must not occlude
                // the later scene passes, and overlapping proxies blend by submission order, not by depth).
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Cache this frame's view-projection (already clip-Y corrected for the live backend, matching the
        /// model pass). Packed into every per-draw slot by <see cref="Enqueue"/> so the overlay depth is comparable to
        /// the mesh depth. Call once before the draw loop.</summary>
        public void BeginFrame(Matrix4x4 clipCorrectedViewProj) => _viewProj = clipCorrectedViewProj;

        /// <summary>Queue one overlay mesh at <paramref name="world"/> and pack its UBO slot (ViewProj + World) into
        /// the CPU image. Nothing is recorded yet: <see cref="Flush"/> uploads every packed slot at once and then
        /// records the draws. <paramref name="drawIndex"/> is the zero-based index of this draw within the frame's
        /// overlay queue and selects the slot; the caller passes the queue length to <see cref="EnsureCapacity"/>
        /// once before the loop. <see cref="BeginFrame"/> must have run.</summary>
        public void Enqueue(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
            int drawIndex, Matrix4x4 world)
        {
            var slot = new DrawUbo { ViewProj = _viewProj, World = world };
            MemoryMarshal.Write(_image.AsSpan(drawIndex * SlotBytes, SlotBytes), in slot);
            _queue.Add(new QueuedDraw(vb, ib, indexCount, indexFormat, drawIndex));
        }

        /// <summary>Upload every packed slot in ONE whole-buffer write, then record the queued draws into the model
        /// FB (already bound). The upload is recorded ahead of the first draw that binds a slot, which is the whole
        /// point of the split; see the <see cref="_image"/> note. A frame that queued nothing records nothing.</summary>
        public void Flush(IGpuCommandList cl)
        {
            if (_queue.Count == 0) return;
            cl.UpdateBuffer(_ubo!, 0, (ReadOnlySpan<byte>)_image);
            foreach (QueuedDraw d in _queue)
            {
                cl.SetPipeline(_pipeline);
                cl.SetGraphicsResourceSet(0, _set!, (uint)(d.DrawIndex * SlotBytes));   // dynamic offset selects this draw's slot
                cl.SetVertexBuffer(0, d.Vb);
                cl.SetIndexBuffer(d.Ib, d.IndexFormat);
                cl.DrawIndexed((uint)d.IndexCount, 1, 0, 0, 0);
            }
            _queue.Clear();
        }

        /// <summary>Ensure the UBO holds at least <paramref name="drawCount"/> 256-byte slots, growing geometrically.
        /// A regrown buffer RETIRES the old one AND the set that ranged over it (a prior frame's command list may
        /// still read both), then binds a new set against the new buffer. Call once before the frame's draw
        /// loop.</summary>
        public void EnsureCapacity(int drawCount)
        {
            if (_ubo != null && _capacity >= drawCount)
            {
                _set ??= CreateSet();
                return;
            }
            if (_ubo != null) _retired.Add(_ubo);
            _capacity = Math.Max(drawCount, _capacity == 0 ? 8 : _capacity * 2);
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * SlotBytes), GpuBufferUsage.UniformBuffer));
            var image = new byte[checked(_capacity * SlotBytes)];
            _image.AsSpan().CopyTo(image);
            _image = image;
            // The set goes to the same retired list as the buffer, never disposed inline. It is bound by this
            // pass's own draws (Flush, with a dynamic offset), the frame path has no WaitForIdle, and this grow
            // runs mid-recording, so a prior frame's submitted command list may still be reading it: freeing it
            // here is the same use-after-free the buffer above avoids, one indirection out.
            if (_set != null) _retired.Add(_set);
            _set = CreateSet();
        }

        IGpuResourceSet CreateSet() =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, new GpuBufferRange(_ubo!, 0, PayloadBytes)));

        /// <summary>Per-draw UBO payload: the frame view-projection and this draw's world transform, both
        /// System.Numerics row-major mat4 (128 bytes, matching the Draw block in OverlayUnlitVert).</summary>
        struct DrawUbo
        {
            public Matrix4x4 ViewProj;
            public Matrix4x4 World;
        }

        /// <summary>One queued overlay draw: the mesh's buffers and the slot its packed uniforms landed in. Held
        /// only between <see cref="Enqueue"/> and <see cref="Flush"/>, so no GPU resource outlives the frame.</summary>
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
