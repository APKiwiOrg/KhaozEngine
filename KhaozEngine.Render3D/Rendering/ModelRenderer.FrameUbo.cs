using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The model pass's PER-FRAME UNIFORM BLOCK: what goes in it, and how it reaches the GPU. Split out of
    /// <c>ModelRenderer.cs</c> when camera-relative rendering added the render-origin member, because the frame
    /// block is one coherent thing (its cached values, the pure light packing, and the single upload every pipeline
    /// that binds this block shares) rather than an arbitrary slice of the renderer.
    /// <para>
    /// The layout constants themselves stay in <c>ModelRenderer.cs</c> beside the GPU-skinning offsets derived from
    /// them. Everything here fills or uploads that layout.
    /// </para>
    /// </summary>
    internal sealed partial class ModelRenderer
    {
        /// <summary>Upload the per-frame uniforms once per frame, before the instanced draws. <paramref name="lights"/>
        /// is the host's per-frame point-light list; it is clamped to <see cref="MaxPointLights"/> (the host is
        /// responsible for picking the N nearest) and the active count is written into <c>Params.y</c>. An empty
        /// span leaves the shader's point-light loop unentered, so the render is bit-identical to the key+fill path.
        /// <para>
        /// <paramref name="viewProj"/>, <paramref name="cameraPos"/> and the light positions are all expected in the
        /// RENDER frame, i.e. already reduced by <paramref name="renderOrigin"/> (which the caller also passes so the
        /// fragment can reconstruct absolute world positions for world-anchored texturing and noise). The light
        /// reduction happens here rather than at the caller so the queue it hands in stays absolute.
        /// <see cref="Vector3.Zero"/> (the default) is the pre-floating-origin behaviour, bit for bit.
        /// </para></summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj, Vector3 cameraPos,
            PixelPostProcessSettings s, ReadOnlySpan<PointLightData> lights, Vector3 renderOrigin = default)
        {
            int count = BuildLightArrays(lights, _lightPosRadius, _lightColorIntensity, renderOrigin);
            _renderOrigin = new Vector4(renderOrigin, 0f);

            // Clip-space-Y correction is derived from the live backend (GpuClip), not baked for Metal: it is the
            // identity on Metal/D3D (byte-identical render) and flips clip-Y on inverted-Y backends (Vulkan).
            // Applied only to the GPU-uploaded matrix; IsoCamera3D.ScreenToGround picking keeps the raw
            // Camera.ViewProjection, so render and picking stay consistent (an earlier unconditional flip broke both).
            _frame = new FrameUbo
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                Dir = new Vector4(Vector3.Normalize(s.LightDirection), 0f),
                Color = s.LightColor,
                Ambient = s.AmbientColor,
                Params = new Vector4(s.CelBands, count, 0, 0),
                FillDir = new Vector4(Vector3.Normalize(s.FillLightDirection), 0f),
                FillColor = s.FillLightColor,
                CameraPos = new Vector4(cameraPos, 1f),
            };
            _frameImageDirty = true;
            WriteFrameUniformsTo(cl, _ubo);
        }

        // Cached this-frame uniforms (set in SetFrameUniforms), re-uploaded into each splat material's combined UBO
        // by WriteFrameUniformsTo so the splat pipeline reads the current frame from its own single UBO.
        FrameUbo _frame;
        // Cached this-frame shadow tail (set in SetShadowUniforms, default = strength 0 = inactive). Written into
        // the frame UBO + every splat material UBO by WriteFrameUniformsTo, so both passes receive shadows.
        ShadowUbo _shadow;
        // Cached this-frame render origin (set in SetFrameUniforms, default = zero = the absolute path), written at
        // RenderOriginOffset by WriteFrameUniformsTo. Only the world-anchored patterns read it.
        Vector4 _renderOrigin;

        /// <summary>Set this frame's cascaded shadow tail (per-cascade RECEIVER matrices + PCF/bias/strength/fade
        /// params). Call after <see cref="SetFrameUniforms"/> when the shadow-map tier is active. Leave unset (or pass
        /// a zero-strength tail) for no shadows. The value is uploaded by
        /// <see cref="WriteFrameUniformsTo(IGpuCommandList,IGpuBuffer)"/> into the model UBO and each splat material
        /// UBO. <paramref name="receiverMats"/> are already GPU-clip-corrected by the caller (up to
        /// <see cref="MaxCascades"/> entries, the first <paramref name="cascadeCount"/> are read).
        /// <paramref name="cascadeBlend"/> is the inner-cascade cross-fade band width (fraction of cascade-local UV
        /// from each edge) that blends toward the next cascade's result near a hand-off. <paramref name="normalOffsets"/>
        /// is the per-cascade normal-offset world size (index 0..cascadeCount-1).</summary>
        public void SetShadowUniforms(ReadOnlySpan<Matrix4x4> receiverMats, int cascadeCount, float texelStep,
            float constantBias, float slopeBias, float strength, float maxDistance, float borderFrac,
            float cascadeBlend, ReadOnlySpan<float> normalOffsets)
        {
            var s = new ShadowUbo
            {
                Params = new Vector4(cascadeCount, strength, constantBias, slopeBias),
                Params2 = new Vector4(texelStep, maxDistance, borderFrac, cascadeBlend),
            };
            // Fill up to MaxCascades matrices + normal offsets, leaving unread slots (past cascadeCount) at identity/zero.
            Matrix4x4 m0 = Matrix4x4.Identity, m1 = Matrix4x4.Identity, m2 = Matrix4x4.Identity, m3 = Matrix4x4.Identity;
            Vector4 no = Vector4.Zero;
            int n = Math.Min(cascadeCount, MaxCascades);
            if (n > 0) { m0 = receiverMats[0]; no.X = normalOffsets[0]; }
            if (n > 1) { m1 = receiverMats[1]; no.Y = normalOffsets[1]; }
            if (n > 2) { m2 = receiverMats[2]; no.Z = normalOffsets[2]; }
            if (n > 3) { m3 = receiverMats[3]; no.W = normalOffsets[3]; }
            s.Cascade0 = m0; s.Cascade1 = m1; s.Cascade2 = m2; s.Cascade3 = m3;
            s.NormalOffsets = no;
            _shadow = s;
            _frameImageDirty = true;
        }

        /// <summary>Clear the shadow tail to inactive (strength 0), so the frame renders with no shadow map (the key
        /// light is unshadowed). Call each frame before the model pass unless the shadow tier is active; keeps the
        /// ShadowMode.Off render byte-stable.</summary>
        public void ClearShadowUniforms() { _shadow = default; _frameImageDirty = true; }

        // The frame block as ONE contiguous CPU image, packed once per frame and uploaded in a SINGLE UpdateBuffer.
        //
        // It used to go up as five writes (header, both light arrays, shadow tail, render origin). They tile the
        // block exactly - 0..176, 176..432, 432..688, 688..992, 992..1008 - so one write of the same bytes at the
        // same base is byte-identical, and every backend records four fewer commands per destination.
        //
        // On D3D11 it is not a micro-optimization, it is the difference between two code paths. Veldrid's
        // D3D11CommandList.UpdateBufferCore sends a PARTIAL write to a non-Dynamic UniformBuffer down the staging
        // route: rent a staging buffer, then GraphicsDevice.UpdateBuffer it, which Maps the IMMEDIATE context with
        // D3D11_MAP_WRITE (not WRITE_DISCARD, and no DO_NOT_WAIT flag). That blocks the calling thread until the GPU
        // has finished with the staging buffer it just recycled, so each partial write is a CPU/GPU sync point in
        // the middle of the pass encode. Only a write covering the WHOLE buffer from offset 0 takes the cheap
        // UpdateSubresource path. The model pass's own UBO is exactly UboBytes, so packing it into one write moves
        // it off the staging route entirely. A splat material's combined UBO is larger (params tail), so it stays on
        // the staging route but pays for one trip instead of five. Metal and Vulkan have no such split, they are
        // simply issued fewer commands.
        readonly byte[] _frameImage = new byte[UboBytes];
        bool _frameImageDirty = true;

        // Repack the image from the cached header, light arrays, shadow tail and render origin. The offsets here are
        // the same constants the five writes used, so the bytes are the same bytes.
        void PackFrameImage()
        {
            Span<byte> img = _frameImage;
            MemoryMarshal.Write(img, in _frame);
            MemoryMarshal.AsBytes<Vector4>(_lightPosRadius).CopyTo(img.Slice((int)HeaderBytes));
            MemoryMarshal.AsBytes<Vector4>(_lightColorIntensity).CopyTo(img.Slice((int)(HeaderBytes + LightArrayBytes)));
            MemoryMarshal.Write(img.Slice((int)ShadowTailOffset), in _shadow);
            MemoryMarshal.Write(img.Slice((int)RenderOriginOffset), in _renderOrigin);
            _frameImageDirty = false;
        }

        /// <summary>This frame's packed frame block, for the callers that embed it in a larger contiguous upload
        /// (the GPU-skinning combined slot). Repacks only when a setter has moved since the last read.</summary>
        internal ReadOnlySpan<byte> FrameImage
        {
            get
            {
                if (_frameImageDirty) PackFrameImage();
                return _frameImage;
            }
        }

        /// <summary>Upload the cached frame uniforms (header + the two point-light arrays) into <paramref name="dst"/>
        /// at offset 0. <paramref name="dst"/> must be at least <see cref="UboBytes"/> bytes; a splat material's
        /// combined UBO is larger (params follow at <see cref="UboBytes"/>) and that tail is left untouched.</summary>
        public void WriteFrameUniformsTo(IGpuCommandList cl, IGpuBuffer dst) => WriteFrameUniformsTo(cl, dst, 0);

        /// <summary>As <see cref="WriteFrameUniformsTo(IGpuCommandList,IGpuBuffer)"/>, but writes the frame block at an
        /// arbitrary <paramref name="baseOffset"/> into <paramref name="dst"/> (must be 256-aligned for a UBO region).
        /// The GPU-skinning combined slot embeds the frame block at <see cref="SkinnedFrameOffset"/> within each
        /// per-draw slot, so the skinned fragment reads this frame's lighting from its one bound buffer.</summary>
        /// <remarks>ONE upload of the whole <see cref="UboBytes"/> block (header, both full fixed-size point-light
        /// arrays so a previous frame's lights never leak past the active count, the shadow tail which is always
        /// written so the Off render stays byte-stable, then the render origin). See <see cref="_frameImage"/> for
        /// why this is a single write rather than five.</remarks>
        public void WriteFrameUniformsTo(IGpuCommandList cl, IGpuBuffer dst, uint baseOffset)
            => cl.UpdateBuffer(dst, baseOffset, FrameImage);

        /// <summary>Pure, headless-testable packing of the host light list into the two fixed-size UBO arrays:
        /// copies up to <see cref="MaxPointLights"/> lights (extras are dropped - the host selects the N nearest),
        /// zero-fills the remaining tail, and returns the active count. Both output arrays must be length
        /// <see cref="MaxPointLights"/>.</summary>
        internal static int BuildLightArrays(ReadOnlySpan<PointLightData> lights, Vector4[] posRadius,
            Vector4[] colorIntensity, Vector3 renderOrigin = default)
        {
            int count = Math.Min(lights.Length, MaxPointLights);
            var originXyz = new Vector4(renderOrigin, 0f);   // the radius (w) is a distance and is frame-invariant
            for (int i = 0; i < count; i++)
            {
                posRadius[i] = lights[i].PosRadius - originXyz;
                colorIntensity[i] = lights[i].ColorIntensity;
            }
            for (int i = count; i < MaxPointLights; i++)
            {
                posRadius[i] = Vector4.Zero;
                colorIntensity[i] = Vector4.Zero;
            }
            return count;
        }
    }
}
