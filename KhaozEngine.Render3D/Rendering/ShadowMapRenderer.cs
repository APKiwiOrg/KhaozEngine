using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The key-light directional CASCADED shadow map: a depth-only pass that renders the instanced casters into an
    /// orthographic light-space depth ATLAS - one R32F colour target holding <see cref="CascadeCount"/> frustum-slice
    /// cascades side by side, one column per cascade (tightest near slice in column 0, growing outward). Storing all cascades in one texture
    /// keeps the receivers sampling a plain texture2D and manual-PCF depth-comparing (no depth-sampling /
    /// comparison-sampler seam) - the single-texture binding model already proven on Metal/D3D11/Vulkan. The receivers
    /// (<see cref="ModelRenderer"/>'s model + splat fragments) bind this atlas + a clamp sampler and shadow the key
    /// light through the shared lighting block, picking the tightest cascade containing each fragment.
    /// </summary>
    /// <remarks>
    /// There is no viewport in the command-list seam, so each cascade is placed into its atlas column by BAKING an
    /// X-only clip-space column transform into the depth-pass matrix (see <see cref="ShadowMapMath.AtlasColumnTransform"/>)
    /// and clipping the overflow with a per-column scissor rect (the depth pipelines enable scissor test). The
    /// per-cascade world-&gt;light-clip matrices ride in ONE dynamic-offset uniform buffer (a 256-byte-aligned slot per
    /// cascade), so a cascade is selected by a per-draw dynamic offset - the same one-buffer pattern the skinned crowd
    /// uses, avoiding interleaved buffer updates mid-pass. The atlas is allocated once for the configured
    /// resolution/count and reused every frame, and a change to either reallocates it (<see cref="EnsureLayout"/>). It is
    /// created up front (even before the tier is switched on) so the material resource sets can bind a STABLE texture
    /// handle - the shader gates on <c>ShadowParams.y</c> (strength), so an inactive frame never taps it and stays
    /// byte-identical to ShadowMode.Off. The depth pass reuses the model pass's instance buffer (no second upload).
    /// </remarks>
    internal sealed class ShadowMapRenderer : IDisposable
    {
        /// <summary>Default per-cascade shadow-map resolution per axis (a game may scale it down via
        /// <see cref="ShadowSettings.ShadowMapResolution"/>).</summary>
        public const int DefaultResolution = 2048;
        const int MinResolution = 256;
        /// <summary>Maximum cascade columns (mirrors <see cref="ModelRenderer.MaxCascades"/> / the shader arrays).</summary>
        internal const int MaxCascades = ModelRenderer.MaxCascades;
        // One 256-byte-aligned dynamic slot per cascade in the rigid depth light UBO (each slot holds one mat4).
        const uint CascadeSlotBytes = 256;

        // GPU-skinning shadow mirror: the skinned depth vertex reads ONE combined UBO at set 0 laid out as
        // { mat4 LightMvp; mat4 bones[128] } (see ShaderSources.SkinnedShadowDepthVert), one 256-byte-aligned slot per
        // (caster,cascade) selected by a per-draw dynamic offset. Same fold-matrix one-vertex-buffer shape as the model pass.
        internal static readonly uint SkinnedDepthSlotBytes =
            Align256((1u + (uint)SkinningMath.MaxBonesPerDraw) * 64);   // (1+128)*64=8256 -> 8448
        static uint Align256(uint n) => (n + 255u) & ~255u;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;    // set 0: the per-cascade light matrix (dynamic-offset UBO, vertex only)
        readonly IGpuBuffer _lightUbo;          // MaxCascades * 256: one light-clip matrix per cascade slot
        readonly IGpuResourceSet _set;          // 64-byte window over _lightUbo, rebased per cascade by a dynamic offset
        readonly IGpuSampler _sampler;          // clamp/linear sampler the RECEIVERS use to PCF-sample the atlas (owned)
        IGpuPipeline _pipeline = null!;

        // GPU-skinning depth pipeline (mirrors _pipeline for skinned casters) + its combined-UBO grow-with-retire buffer.
        readonly IGpuShaderSet _skinnedShaders;
        readonly IGpuResourceLayout _skinnedLayout;   // set 0: combined { LightMvp; bones[128] } dynamic UBO, vertex only
        IGpuPipeline _skinnedPipeline = null!;         // rebuilt in EnsureLayout alongside _pipeline
        IGpuBuffer? _skinnedUbo; uint _skinnedSlots; IGpuResourceSet? _skinnedSet;
        readonly List<IDisposable> _retiredSkinned = new();   // grown-out combined UBOs/sets (a prior frame may still read them)
        readonly Matrix4x4[] _skinnedScratch = new Matrix4x4[1 + SkinningMath.MaxBonesPerDraw];

        IGpuTexture _atlas = null!;             // R32F: all cascades' light-space depth side by side (the map the receivers sample)
        IGpuTexture _depthStencil = null!;      // depth-test buffer for the depth pass (never sampled), atlas-sized
        IGpuFramebuffer _fb = null!;
        int _perCascadeRes;
        int _cascadeCount;

        /// <summary>The shadow atlas the receivers sample (R32F light-space depth, <see cref="CascadeCount"/> columns).
        /// Stable handle across frames, reallocated only on a resolution/count change (see <see cref="EnsureLayout"/>).</summary>
        public IGpuTexture ShadowTexture => _atlas;

        /// <summary>The clamp/linear sampler the receivers PCF-sample the atlas with (owned here).</summary>
        public IGpuSampler ShadowSampler => _sampler;

        /// <summary>The current per-cascade allocated resolution per axis (one atlas column).</summary>
        public int Resolution => _perCascadeRes;

        /// <summary>The current number of cascade columns in the atlas.</summary>
        public int CascadeCount => _cascadeCount;

        public ShadowMapRenderer(IGpuDevice gd, int resolution, int cascadeCount)
        {
            _gd = gd;
            var f = gd.Factory;

            _shaders = f.CreateShadersFromSpirv(ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag);
            // The per-cascade light matrix is bound via a dynamic offset (one 256-byte slot per cascade), so one buffer
            // carries all cascades and a cascade is picked per draw without interleaved buffer updates mid-pass.
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
            _lightUbo = f.CreateBuffer(new GpuBufferDescription((uint)MaxCascades * CascadeSlotBytes, GpuBufferUsage.UniformBuffer));
            _set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, new GpuBufferRange(_lightUbo, 0, 64)));

            // GPU-skinning depth shaders/layout (the fragment is the shared ShadowDepthFrag). Set 0 = combined
            // { LightMvp; bones[128] } dynamic UBO, vertex only. The pipeline is built per layout in EnsureLayout.
            _skinnedShaders = f.CreateShadersFromSpirv(ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag);
            _skinnedLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("VBlock", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));

            // Clamp addressing so a PCF tap off a column edge reads the border (never wraps), and linear so the 3x3 taps
            // blend smoothly. The receiver additionally clamps each tap inside the selected cascade's column, so a tap
            // never bleeds into a neighbour cascade.
            _sampler = f.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.MinLinearMagLinearMipLinear,
                GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));

            _perCascadeRes = 0;
            _cascadeCount = 0;
            EnsureLayout(resolution, cascadeCount);
        }

        /// <summary>(Re)allocate the atlas targets + pipelines for <paramref name="resolution"/> (per cascade, clamped
        /// to a sane minimum) x <paramref name="cascadeCount"/> columns if either changed. The atlas handle changes on
        /// a realloc, so callers that bound the old texture into a resource set must rebuild it (ModelRenderer rebuilds
        /// its material sets' shadow binding).</summary>
        public void EnsureLayout(int resolution, int cascadeCount)
        {
            int res = Math.Max(MinResolution, resolution);
            int count = Math.Clamp(cascadeCount, 1, MaxCascades);
            if (res == _perCascadeRes && count == _cascadeCount) return;
            _gd.WaitForIdle();   // a prior frame's pass may still reference the old targets; a layout change is rare
            _pipeline?.Dispose();
            _skinnedPipeline?.Dispose();
            _fb?.Dispose();
            _atlas?.Dispose();
            _depthStencil?.Dispose();

            _perCascadeRes = res;
            _cascadeCount = count;
            uint w = (uint)(res * count);   // atlas width = one column per cascade
            uint h = (uint)res;
            var f = _gd.Factory;
            _atlas = f.CreateTexture(GpuTextureDescription.Texture2D(
                w, h, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _depthStencil = f.CreateTexture(GpuTextureDescription.Texture2D(
                w, h, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil));
            _fb = f.CreateFramebuffer(_depthStencil, _atlas);

            _pipeline = BuildPipeline(f, _fb.Outputs);
            _skinnedPipeline = BuildSkinnedPipeline(f, _fb.Outputs);
        }

        // GPU-skinning depth pipeline: the rest-pose SkinnedVertex stream (locations 0..6) at slot 0, the combined
        // { LightMvp; bones[128] } dynamic UBO at set 0. Front-face cull (the same second-depth trick as the rigid
        // depth pass), scissor test on (per-column clip). Rebuilt with _pipeline whenever the layout reallocates.
        IGpuPipeline BuildSkinnedPipeline(IGpuResourceFactory f, GpuOutputDescription outputs)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
                new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _skinnedLayout },
                ShaderSet = _skinnedShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = outputs,
            });
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory f, GpuOutputDescription outputs)
        {
            // Slot 0: per-vertex geometry (locations 0..4) - same layout the model pass uses, so the shared model
            // vertex buffer binds unchanged (only Position is read).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
            // Slot 1: the model pass's per-instance stream (locations 5..11), reused verbatim - no second upload.
            var instanceLayout = new GpuVertexLayoutDescription(
                stride: ModelRenderer.InstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                });

            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                // Cull FRONT faces (draw back faces) so the stored depth is the caster's FAR side. This is the classic
                // second-depth trick that lets the constant/slope bias defeat self-shadow acne on the lit front faces
                // without peter-panning. Falls back gracefully for open/non-manifold meshes (they simply store their
                // single face). depthClip on so nothing past the light far plane writes, and scissor test on so each
                // cascade's column-transformed geometry is clipped to its atlas column (no bleed into a neighbour).
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = outputs,
            });
        }

        /// <summary>Begin the cascaded depth pass: bind + clear the whole atlas, upload each cascade's DEPTH matrix
        /// (world-&gt;light-clip already GPU-clip-corrected AND column-transformed) into its dynamic slot, and bind the
        /// rigid depth pipeline. Clear the R32F atlas to 1.0 (far plane) so an unwritten texel reads "nothing in front"
        /// = unshadowed. Follow with <see cref="BeginCascadeRigid"/> per cascade to draw casters, then
        /// <see cref="EndDepthPass"/>.</summary>
        public void BeginDepthPass(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> depthMats, int cascadeCount)
        {
            int count = Math.Min(cascadeCount, _cascadeCount);
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 m = depthMats[i];
                cl.UpdateBuffer(_lightUbo, (uint)i * CascadeSlotBytes, in m);
            }
            cl.SetFramebuffer(_fb);
            cl.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f));  // 1.0 = far plane = no caster (whole atlas)
            cl.ClearDepthStencil(1f);
            cl.SetPipeline(_pipeline);
        }

        /// <summary>Bind cascade <paramref name="cascade"/> for the RIGID (and CPU-skinned) caster draws: scissor the
        /// output to that cascade's atlas column and rebase the light UBO window to that cascade's slot. Call before
        /// each cascade's caster runs. <see cref="BeginDepthPass"/> must be bound.</summary>
        public void BeginCascadeRigid(IGpuCommandList cl, int cascade)
        {
            cl.SetPipeline(_pipeline);
            SetCascadeScissor(cl, cascade);
            cl.SetGraphicsResourceSet(0, _set, (uint)cascade * CascadeSlotBytes);
        }

        void SetCascadeScissor(IGpuCommandList cl, int cascade)
        {
            uint res = (uint)_perCascadeRes;
            cl.SetScissorRect(0, (uint)cascade * res, 0, res, res);
        }

        /// <summary>Reset the scissor to the full framebuffer after the cascaded pass (the next pass expects a full
        /// scissor). Call once after all cascades are drawn.</summary>
        public void EndDepthPass(IGpuCommandList cl) => cl.SetFullScissorRects();

        /// <summary>Draw one caster run into the CURRENTLY-BOUND cascade: <paramref name="instanceCount"/> instances
        /// from <paramref name="instanceStart"/> of the shared model instance buffer. The mesh's own vertex + index
        /// buffers supply geometry.</summary>
        public void DrawCasterRun(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, IGpuBuffer instanceBuffer, uint instanceStart, uint instanceCount)
        {
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, instanceBuffer);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Draw one CPU-skinned caster into the CURRENTLY-BOUND cascade: its deformed vertices live at
        /// <paramref name="baseVertex"/>.. in the shared skinned vertex buffer, and its instance data is element
        /// <paramref name="drawIndex"/> of the skinned instance buffer (both supplied by the model pass).</summary>
        public void DrawSkinnedCaster(IGpuCommandList cl, IGpuBuffer skinnedVb, IGpuBuffer skinnedInstanceBuffer,
            IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, uint drawIndex)
        {
            cl.SetVertexBuffer(0, skinnedVb);
            cl.SetVertexBuffer(1, skinnedInstanceBuffer);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, baseVertex, drawIndex);
        }

        // ---- GPU-skinning shadow casters (opt-in). Mirror the model pass's fold-matrix combined-UBO binding. Each
        //      caster gets ONE slot per cascade (its LightMvp folds that cascade's column-transformed matrix). ----

        /// <summary>Ensure the combined skinned-depth UBO holds at least <paramref name="slotCount"/> slots (each
        /// <see cref="SkinnedDepthSlotBytes"/>), growing geometrically + retiring the old buffer + its window set.
        /// With cascades a caster needs one slot per cascade, so pass <c>casterCount * cascadeCount</c>.</summary>
        public void EnsureSkinnedShadowCapacity(uint slotCount)
        {
            if (_skinnedUbo != null && _skinnedSlots >= slotCount) return;
            if (_skinnedUbo != null) _retiredSkinned.Add(_skinnedUbo);
            if (_skinnedSet != null) _retiredSkinned.Add(_skinnedSet);
            _skinnedSlots = Math.Max(slotCount, _skinnedSlots == 0 ? 8u : _skinnedSlots * 2);
            _skinnedUbo = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_skinnedSlots * SkinnedDepthSlotBytes, GpuBufferUsage.UniformBuffer));
            _skinnedSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedLayout, new GpuBufferRange(_skinnedUbo, 0, SkinnedDepthSlotBytes)));
        }

        /// <summary>Pack one skinned caster's depth slot for one cascade: <c>LightMvp = model * cascadeDepthMat</c>
        /// folded per draw + the composed <paramref name="bones"/> (uploaded raw, read column-major = transpose).
        /// <paramref name="cascadeDepthMat"/> is the cascade's GPU-clip-corrected AND column-transformed matrix.
        /// Uploads only the mesh's bones (indices validated at load).</summary>
        public void PackSkinnedShadowSlot(IGpuCommandList cl, uint slot, in Matrix4x4 model, in Matrix4x4 cascadeDepthMat, ReadOnlySpan<Matrix4x4> bones)
        {
            _skinnedScratch[0] = model * cascadeDepthMat;   // System.Numerics order: p * model * cascadeDepthMat
            for (int b = 0; b < bones.Length; b++) _skinnedScratch[1 + b] = bones[b];
            cl.UpdateBuffer(_skinnedUbo!, slot * SkinnedDepthSlotBytes, _skinnedScratch.AsSpan(0, 1 + bones.Length));
        }

        /// <summary>Bind cascade <paramref name="cascade"/> for the GPU-SKINNED caster draws: scissor to that cascade's
        /// atlas column and switch to the skinned depth pipeline. Call after the rigid caster runs, before the skinned
        /// casters (<see cref="BeginDepthPass"/> must be bound). The skinned window set is bound per draw.</summary>
        public void BindCascadeSkinned(IGpuCommandList cl, int cascade)
        {
            cl.SetPipeline(_skinnedPipeline);
            SetCascadeScissor(cl, cascade);
            cl.SetGraphicsResourceSet(0, _skinnedSet!, 0);   // rebound per draw with the slot's dynamic offset below
        }

        /// <summary>Draw one GPU-skinned caster into the CURRENTLY-BOUND cascade: its rest-pose <paramref name="restVb"/>
        /// at slot 0, the combined UBO window at set 0 selected by <paramref name="slot"/>'s dynamic offset. One instance.</summary>
        public void DrawGpuSkinnedCaster(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, uint slot)
        {
            cl.SetGraphicsResourceSet(0, _skinnedSet!, slot * SkinnedDepthSlotBytes);
            cl.SetVertexBuffer(0, restVb);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
        }

        public void Dispose()
        {
            _pipeline?.Dispose();
            _skinnedPipeline?.Dispose();
            _fb?.Dispose();
            _atlas?.Dispose();
            _depthStencil?.Dispose();
            _set.Dispose();
            _lightUbo.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _sampler.Dispose();
            _skinnedShaders.Dispose();
            _skinnedLayout.Dispose();
            _skinnedUbo?.Dispose();
            _skinnedSet?.Dispose();
            foreach (var r in _retiredSkinned) r.Dispose();
            _retiredSkinned.Clear();
        }
    }
}
