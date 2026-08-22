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
        readonly IGpuBuffer _lightUbo;          // MaxCascades * 256: one light-clip matrix + the render origin per cascade slot
        // CPU mirror of _lightUbo, uploaded whole once per depth pass (see BeginDepthPass).
        readonly byte[] _lightImage = new byte[MaxCascades * CascadeSlotBytes];
        readonly IGpuResourceSet _set;          // 64-byte window over _lightUbo, rebased per cascade by a dynamic offset
        readonly IGpuSampler _sampler;          // clamp/POINT sampler the RECEIVERS use to PCF-sample the atlas (owned)
        IGpuPipeline _pipeline = null!;

        // Dissolve-aware depth pipeline (issue #287): the same layout/outputs/raster state as _pipeline, with the
        // instance layout extended to the model pass's locations 12..13 and a fragment that noise-discards by the
        // per-instance dissolve. Bound ONLY for caster spans that carry a dissolve, so a scene with none never
        // touches it and its depth pass is byte-identical to before. Its set is a FULL-slot (256-byte) window over
        // the same _lightUbo, because this vertex also reads the RenderOrigin (offset 64) and this cascade's
        // dissolve noise scale (offset 80).
        readonly IGpuShaderSet _dissolveShaders;
        readonly IGpuResourceSet _dissolveSet;
        IGpuPipeline _dissolvePipeline = null!;   // rebuilt in EnsureLayout alongside _pipeline

        // INVERTED dissolve depth pipeline (issue #391): the same vertex, layout, outputs, raster state and UBO
        // window as _dissolvePipeline, differing ONLY in a fragment that keeps what the plain one discards. Bound
        // for the merged half of an HLOD crossfade, so the two halves' dithers complement instead of nesting and
        // their union covers the whole mask across the band. A scene that never marks a caster inverted never
        // binds it. Shares _dissolveSet (same UBO window) - only the pipeline differs.
        readonly IGpuShaderSet _dissolveInvertedShaders;
        IGpuPipeline _dissolveInvertedPipeline = null!;   // rebuilt in EnsureLayout alongside _pipeline

        // GPU-skinning depth pipeline (mirrors _pipeline for skinned casters) + its combined-UBO grow-with-retire buffer.
        readonly IGpuShaderSet _skinnedShaders;
        readonly IGpuResourceLayout _skinnedLayout;   // set 0: combined { LightMvp; bones[128] } dynamic UBO, vertex only
        IGpuPipeline _skinnedPipeline = null!;         // rebuilt in EnsureLayout alongside _pipeline
        IGpuBuffer? _skinnedUbo; uint _skinnedSlots; IGpuResourceSet? _skinnedSet;
        // Persistent CPU image of the complete skinned-depth UBO. D3D11 takes its cheap UpdateSubresource route only
        // for a whole uniform-buffer write from offset 0, so every cascade/caster slot is packed here before one
        // upload records the entire buffer.
        byte[] _skinnedImage = Array.Empty<byte>();
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

        /// <summary>The clamp/POINT sampler the receivers PCF-sample the atlas with (owned here). Point is required,
        /// not preferred: the receivers compare depths themselves, so any pre-compare filtering blends the clear
        /// value into a tap and only ever lightens. See the ctor note.</summary>
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

            // Dissolve depth shaders + their window over the same buffer. The window is the WHOLE 256-byte slot (not
            // 64) so the vertex can read RenderOrigin at offset 64 as well as the matrix, and 256 bytes is also the
            // D3D11-friendly 16-constant multiple. The layout description is identical to the plain one, so the same
            // _layout object backs both sets and both pipelines.
            _dissolveShaders = f.CreateShadersFromSpirv(ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag);
            _dissolveInvertedShaders = f.CreateShadersFromSpirv(ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveInvertedFrag);
            _dissolveSet = f.CreateResourceSet(new GpuResourceSetDescription(_layout, new GpuBufferRange(_lightUbo, 0, CascadeSlotBytes)));

            // GPU-skinning depth shaders/layout (the fragment is the shared ShadowDepthFrag). Set 0 = combined
            // { LightMvp; bones[128] } dynamic UBO, vertex only. The pipeline is built per layout in EnsureLayout.
            _skinnedShaders = f.CreateShadersFromSpirv(ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag);
            _skinnedLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("VBlock", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));

            // Clamp addressing so a PCF tap off a column edge reads the border (never wraps). The receiver additionally
            // clamps each tap inside the selected cascade's column, so a tap never bleeds into a neighbour cascade.
            //
            // POINT filtering, deliberately (issue #391). This atlas is a MANUAL-compare depth map: the receiver
            // fetches a stored depth and compares it itself (pcfCascade in ShaderSources.Lighting), so filtering
            // belongs AFTER the compare, never before. A linear pre-filter averages stored DEPTHS, which is not a
            // meaningful operation on this map: the clear value is 1.0 = "no caster", so a tap next to a gap blends
            // a sentinel into a depth and lands wherever the numbers fall rather than where the geometry is. The
            // flip point is around h / (2 * cascadeRadius) of admixture for a caster h above its receiver, a few
            // percent in the far cascades, so which way a mixed tap resolves is set by the backend's filtering
            // support rather than by the scene. It was harmless while every caster was solid (only silhouette texels
            // mix) and became load-bearing once 17.10.0 started dithering depth, which punches gaps through the
            // whole footprint. pcfCascade already averages nine COMPARISON results, which is the correct order, so
            // the 3x3 kernel keeps giving a soft edge with point taps.
            _sampler = f.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.MinPointMagPointMipPoint,
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
            _dissolvePipeline?.Dispose();
            _dissolveInvertedPipeline?.Dispose();
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
            _dissolvePipeline = BuildPipeline(f, _fb.Outputs, dissolve: true);
            _dissolveInvertedPipeline = BuildPipeline(f, _fb.Outputs, dissolve: true, invertedDissolve: true);
            _skinnedPipeline = BuildSkinnedPipeline(f, _fb.Outputs);
        }

        // GPU-skinning depth pipeline: the rest-pose SkinnedVertex stream (locations 0..6) at slot 0, the combined
        // { LightMvp; bones[128] } dynamic UBO at set 0. Front-face cull (the same second-depth trick as the rigid
        // depth pass), scissor test on (per-column clip). Rebuilt with _pipeline whenever the layout reallocates.
        // depthClipEnabled stays TRUE here for the same reason as the rigid pipeline below: the NEAR plane is handled
        // in the vertex (SkinnedShadowDepthVert's pancake) and the far plane should still clip, which the flag cannot
        // express because it turns off both planes together.
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

        // Build a rigid caster depth pipeline. <paramref name="dissolve"/> false is the plain depth-only pipeline,
        // unchanged. True is the issue #287 dissolve-aware variant, which differs ONLY in its shader set and in
        // declaring the model pass's two trailing instance elements (locations 12..13) so its vertex can read the
        // per-instance dissolve. Everything else - raster state, outputs, resource layout, the shared instance
        // stride - is identical, so a span drawn through either records the same depth when the dissolve is 0.
        // <paramref name="invertedDissolve"/> picks the issue #391 fragment that keeps what the plain dissolve
        // fragment discards (the complementary half of an HLOD crossfade); it is meaningless without dissolve.
        IGpuPipeline BuildPipeline(IGpuResourceFactory f, GpuOutputDescription outputs, bool dissolve = false,
            bool invertedDissolve = false)
        {
            // Slot 0: per-vertex geometry (locations 0..4) - same layout the model pass uses, so the shared model
            // vertex buffer binds unchanged (only Position is read).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
            // Slot 1: the model pass's per-instance stream (locations 5..11, plus 12..13 on the dissolve variant),
            // reused verbatim - no second upload. The stride is the full InstanceData either way, so the trailing
            // elements the plain pipeline omits are simply not fetched.
            var instanceElements = new List<GpuVertexElement>
            {
                new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
            };
            if (dissolve)
            {
                instanceElements.Add(new GpuVertexElement("IDynamic", GpuVertexElementFormat.Float1));
                instanceElements.Add(new GpuVertexElement("IDissolve", GpuVertexElementFormat.Float2));
            }
            var instanceLayout = new GpuVertexLayoutDescription(
                stride: ModelRenderer.InstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: instanceElements.ToArray());

            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                // Cull FRONT faces (draw back faces) so the stored depth is the caster's FAR side. This is the classic
                // second-depth trick that lets the constant/slope bias defeat self-shadow acne on the lit front faces
                // without peter-panning. Falls back gracefully for open/non-manifold meshes (they simply store their
                // single face). Scissor test on so each cascade's column-transformed geometry is clipped to its atlas
                // column (no bleed into a neighbour).
                //
                // depthClip stays ON, deliberately (issue #394). It is the FAR plane it now guards: nothing past the
                // light far plane is down-light of every receiver in the cascade, so clipping it is free. The NEAR
                // plane is handled in the vertex instead (the pancake in ShaderSources.ShadowDepthVert and its
                // siblings), and that split is the reason the flag is not the tool here: it turns off BOTH clip
                // planes at once, so flipping it would give up the free far-plane clip to buy a near-plane clamp the
                // vertex already provides. The flag itself is honoured everywhere now, including on both Metal paths
                // (issue #598, 17.39.0), which it was not when this pass was written.
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = dissolve ? (invertedDissolve ? _dissolveInvertedShaders : _dissolveShaders) : _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = outputs,
            });
        }

        /// <summary>Begin the cascaded depth pass: bind + clear the whole atlas, upload each cascade's DEPTH matrix
        /// (world-&gt;light-clip already GPU-clip-corrected AND column-transformed), this frame's
        /// <paramref name="renderOrigin"/> and that cascade's dissolve <paramref name="noiseScales"/> entry into its
        /// dynamic slot, and bind the rigid depth pipeline. Clear the R32F atlas to 1.0 (far plane) so an unwritten
        /// texel reads "nothing in front" = unshadowed. Follow with <see cref="BeginCascadeRigid"/> (or
        /// <see cref="BeginCascadeRigidDissolve"/> / <see cref="BeginCascadeRigidDissolveInverted"/>) per cascade to
        /// draw casters, then <see cref="EndDepthPass"/>. The origin rides at slot offset 64 and the noise scale at
        /// 80, both read only by the dissolve variants' vertex (the plain depth vertex declares just the matrix).
        /// A short <paramref name="noiseScales"/> falls back to the base scale for the cascades it does not cover.</summary>
        public void BeginDepthPass(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> depthMats, int cascadeCount,
            Vector3 renderOrigin, ReadOnlySpan<float> noiseScales)
        {
            int count = Math.Min(cascadeCount, _cascadeCount);
            var origin = new Vector4(renderOrigin, 0f);
            Span<byte> image = _lightImage;
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 m = depthMats[i];
                float scale = i < noiseScales.Length ? noiseScales[i] : ShadowDissolveNoise.BaseScale;
                var dissolveParams = new Vector4(scale, 0f, 0f, 0f);
                Span<byte> slotBytes = image.Slice((int)((uint)i * CascadeSlotBytes), (int)CascadeSlotBytes);
                MemoryMarshal.Write(slotBytes, in m);
                MemoryMarshal.Write(slotBytes.Slice(64), in origin);
                MemoryMarshal.Write(slotBytes.Slice(80), in dissolveParams);
            }
            // ONE upload of the whole cascade buffer instead of three writes per cascade (twelve at four cascades).
            // _lightImage is the CPU mirror of _lightUbo and nothing else writes that buffer, so carrying it across
            // frames keeps every byte identical to what the per-slot writes left behind - including the slots past
            // `count` and the unread tail of each slot, which the depth shaders never declare (the dissolve block
            // stops at 96 of the 256 slot bytes). Covering offset 0 to SizeInBytes also matters on D3D11: only a
            // whole-buffer write escapes Veldrid's partial-uniform-write staging route, which Maps the immediate
            // context and stalls on the GPU (see the _frameImage note in ModelRenderer.FrameUbo.cs).
            cl.UpdateBuffer(_lightUbo, 0, (ReadOnlySpan<byte>)_lightImage);
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

        /// <summary>As <see cref="BeginCascadeRigid"/>, but binds the DISSOLVE-AWARE depth pipeline (issue #287) for
        /// the caster spans that carry a per-instance dissolve: same cascade scissor, same light slot, a full-slot
        /// UBO window (the vertex also reads the render origin), and a fragment that noise-discards by the dissolve.
        /// Switch back with <see cref="BeginCascadeRigid"/> for the plain spans.</summary>
        public void BeginCascadeRigidDissolve(IGpuCommandList cl, int cascade)
        {
            cl.SetPipeline(_dissolvePipeline);
            SetCascadeScissor(cl, cascade);
            cl.SetGraphicsResourceSet(0, _dissolveSet, (uint)cascade * CascadeSlotBytes);
        }

        /// <summary>As <see cref="BeginCascadeRigidDissolve"/>, but binds the INVERTED dissolve fragment (issue
        /// #391): same cascade scissor, same light slot, same UBO window, and a discard test that keeps exactly
        /// what the plain dissolve fragment throws away. For the merged half of an HLOD crossfade, whose dither
        /// must complement the fading props' rather than nest inside it.</summary>
        public void BeginCascadeRigidDissolveInverted(IGpuCommandList cl, int cascade)
        {
            cl.SetPipeline(_dissolveInvertedPipeline);
            SetCascadeScissor(cl, cascade);
            cl.SetGraphicsResourceSet(0, _dissolveSet, (uint)cascade * CascadeSlotBytes);
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
            var image = new byte[checked((int)(_skinnedSlots * SkinnedDepthSlotBytes))];
            _skinnedImage.AsSpan().CopyTo(image);
            _skinnedImage = image;
            _skinnedUbo = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_skinnedSlots * SkinnedDepthSlotBytes, GpuBufferUsage.UniformBuffer));
            _skinnedSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _skinnedLayout, new GpuBufferRange(_skinnedUbo, 0, SkinnedDepthSlotBytes)));
        }

        /// <summary>Pack one skinned caster's depth slot for one cascade: <c>LightMvp = model * cascadeDepthMat</c>
        /// folded per draw + the composed <paramref name="bones"/> (uploaded raw, read column-major = transpose).
        /// <paramref name="cascadeDepthMat"/> is the cascade's GPU-clip-corrected AND column-transformed matrix.
        /// Uploads only the mesh's bones (indices validated at load).</summary>
        public void PackSkinnedShadowSlot(uint slot, in Matrix4x4 model, in Matrix4x4 cascadeDepthMat, ReadOnlySpan<Matrix4x4> bones)
        {
            _skinnedScratch[0] = model * cascadeDepthMat;   // System.Numerics order: p * model * cascadeDepthMat
            for (int b = 0; b < bones.Length; b++) _skinnedScratch[1 + b] = bones[b];
            MemoryMarshal.AsBytes(_skinnedScratch.AsSpan(0, 1 + bones.Length)).CopyTo(
                _skinnedImage.AsSpan(checked((int)(slot * SkinnedDepthSlotBytes)), checked((int)SkinnedDepthSlotBytes)));
        }

        /// <summary>Upload every packed GPU-skinned shadow slot in one whole-buffer write. Slots not selected by a
        /// depth draw this pass may retain old bytes because no dynamic offset binds them.</summary>
        public void UploadSkinnedShadowSlots(IGpuCommandList cl)
            => cl.UpdateBuffer(_skinnedUbo!, 0, (ReadOnlySpan<byte>)_skinnedImage);

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
            _dissolvePipeline?.Dispose();
            _dissolveInvertedPipeline?.Dispose();
            _skinnedPipeline?.Dispose();
            _fb?.Dispose();
            _atlas?.Dispose();
            _depthStencil?.Dispose();
            _set.Dispose();
            _dissolveSet.Dispose();
            _dissolveShaders.Dispose();
            _dissolveInvertedShaders.Dispose();
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
