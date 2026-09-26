using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The skinned draw surface: the CPU-skinned upload and draw path, the GPU-skinned main and bone
/// palette slots, and the skinned shadow casters. Split out of the main file because it is one
/// responsibility with its own buffers, and because the file crossed the size cap when the clustered
/// lighting work and the shared retirement queue landed in the same release.</summary>
internal sealed partial class ModelRenderer
{
    internal IGpuResourceLayout SkinnedBonePaletteLayout => _bonePalette.Layout;
    internal IGpuResourceSet SkinnedBonePaletteSet => _bonePalette.Set;
    internal IGpuBuffer? CpuSkinnedVertexBuffer => _skinnedVertexBuffer;
    internal IGpuBuffer? CpuSkinnedInstanceBuffer => _skinnedInstanceBuffer;

    /// <summary>Draw one CPU-skinned caster into the shadow map, reusing the shared skinned vertex + instance
    /// buffers (<see cref="UploadCpuSkinned"/> must have run this frame). <see cref="BeginShadowPass"/> bound.</summary>
    public void DrawShadowSkinnedCaster(IGpuCommandList cl, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat,
        int baseVertex, uint drawIndex) =>
        _shadowMap.DrawSkinnedCaster(cl, _skinnedVertexBuffer!, _skinnedInstanceBuffer!, ib, indexCount, indexFormat, baseVertex, drawIndex);

    /// <summary>Upload this frame's CPU-skinned geometry: <paramref name="verts"/> is every skinned draw's
    /// deformed vertices concatenated; <paramref name="instances"/> is one <see cref="InstanceData"/> per draw
    /// (its world transform / tint / material), parallel to the draw order. Both buffers grow geometrically and
    /// retire (not dispose) the replaced buffer, matching the instance-buffer lifetime rule.</summary>
    public void UploadCpuSkinned(IGpuCommandList cl, ReadOnlySpan<ModelVertex> verts, ReadOnlySpan<InstanceData> instances)
    {
        if (verts.Length == 0 || instances.Length == 0) return;
        if (_skinnedVertexBuffer == null || _skinnedVertexCapacity < (uint)verts.Length)
        {
            if (_skinnedVertexBuffer != null) _retired.Retire(_skinnedVertexBuffer);
            _skinnedVertexCapacity = Math.Max((uint)verts.Length, _skinnedVertexCapacity == 0 ? 4096u : _skinnedVertexCapacity * 2);
            _skinnedVertexBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_skinnedVertexCapacity * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }
        if (_skinnedInstanceBuffer == null || _skinnedInstanceCapacity < (uint)instances.Length)
        {
            if (_skinnedInstanceBuffer != null) _retired.Retire(_skinnedInstanceBuffer);
            _skinnedInstanceCapacity = Math.Max((uint)instances.Length, _skinnedInstanceCapacity == 0 ? 64u : _skinnedInstanceCapacity * 2);
            _skinnedInstanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_skinnedInstanceCapacity * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }
        cl.UpdateBuffer(_skinnedVertexBuffer!, 0, verts);
        cl.UpdateBuffer(_skinnedInstanceBuffer!, 0, instances);
    }

    /// <summary>Draw one CPU-skinned mesh through the pipeline <see cref="BindCpuSkinnedPass"/> or
    /// <see cref="BindDissolvePass"/> bound: its deformed vertices live at <paramref name="baseVertex"/>.. in the
    /// shared skinned vertex buffer (added per index via the draw's vertexOffset), and its instance data is element
    /// <paramref name="drawIndex"/> of the skinned instance buffer (selected by instanceStart). One
    /// <c>instanceCount=1</c> draw. <see cref="SetFrameUniforms"/> must already have run (the rigid pass shares the
    /// frame UBO). While the target is temporal the draw also binds the motion block at set 1 and last frame's
    /// positions at vertex slot 2, parallel to the deformed vertices.</summary>
    public void DrawCpuSkinned(IGpuCommandList cl, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, uint drawIndex, IGpuResourceSet? materialSet)
    {
        cl.SetGraphicsResourceSet(0, materialSet ?? _defaultSet);
        cl.SetVertexBuffer(0, _skinnedVertexBuffer!);
        cl.SetVertexBuffer(1, _skinnedInstanceBuffer!);
        if (_motion is { } motion)
        {
            // The CPU-skinned variant's set 1 and last frame's positions, parallel to the deformed vertices.
            cl.SetGraphicsResourceSet(1, motion.FrameSet);
            cl.SetVertexBuffer(2, motion.CpuPrevious);
        }
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, 1, 0, baseVertex, drawIndex);
    }

    // ---- GPU skinning (the default). See the field block + ShaderSources.SkinnedModelVert for the two-buffer design. ----

    /// <summary>Build a skinned mesh's set-1 material set (albedo/normal/roughness + shared sampler + shadow map),
    /// bound to the FRAGMENT-only skinned material layout. The frame UBO is NOT here - the skinned fragment reads
    /// it from set 0 binding 0, the shared block the model pass binds (see <see cref="EnsureSkinnedMainCapacity"/>),
    /// so this set stays pure per-mesh material data and never has to be rebuilt when a frame changes. Defaults to
    /// white/flat/zero so an untextured skinned mesh matches the CPU path. Owned by the caller (Scene3D), disposed
    /// when the mesh unloads.</summary>
    public IGpuResourceSet CreateSkinnedMaterialSet(IGpuTexture? albedo = null, IGpuTexture? normal = null, IGpuTexture? roughness = null) =>
        CreateShadowSamplingSet(_skinnedFragLayout, _shadowMap.ShadowTexture,
            albedo ?? _white, normal ?? _flatNormal, roughness ?? _defaultRough, _sampler);

    /// <summary>Ensure the per-draw main UBO holds at least <paramref name="slotCount"/> slots (each
    /// <see cref="SkinnedMainSlotBytes"/>), growing geometrically and retiring the old buffer + its set. Rebuilds
    /// the set-0 resource set, which carries both of the pipeline's uniform buffers: the shared frame block at
    /// binding 0, the point-light buffers at bindings 1 and 2, and a single-slot window over the per-draw
    /// buffer at binding 3, the
    /// one the dynamic offset indexes. One shared set, cheap to rebuild on the rare geometric grow. Call once
    /// before packing this frame's skinned main slots.</summary>
    public void EnsureSkinnedMainCapacity(uint slotCount)
    {
        if (_skinnedMainUbo != null && _skinnedMainSlots >= slotCount) return;
        if (_skinnedMainUbo != null) _retired.Retire(_skinnedMainUbo);
        if (_skinnedMainSet != null) _retired.Retire(_skinnedMainSet);
        _skinnedMainSlots = Math.Max(slotCount, _skinnedMainSlots == 0 ? 8u : _skinnedMainSlots * 2);
        var image = new byte[checked((int)(_skinnedMainSlots * SkinnedMainSlotBytes))];
        _skinnedMainImage.AsSpan().CopyTo(image);
        _skinnedMainImage = image;
        _skinnedMainUbo = _gd.Factory.CreateBuffer(
            new GpuBufferDescription(_skinnedMainSlots * SkinnedMainSlotBytes, GpuBufferUsage.UniformBuffer));
        _skinnedMainSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
            _skinnedMainLayout, _ubo, _pointLightBuffer, _pointLightClusterBuffer,
            new GpuBufferRange(_skinnedMainUbo, 0, SkinnedMainSlotBytes)));
    }

    /// <summary>Pack one skinned draw's per-draw slot: the two-matrix header alone (<c>Model</c> for world
    /// pos/normal/tangent, <c>P</c> packing tint/emissive/specParams into its three columns).
    /// <para>
    /// NEITHER THE FRAME BLOCK NOR THE PALETTE IS WRITTEN HERE ANY MORE. The slot used to open with a CPU-folded
    /// <c>Mvp</c> and carry a whole copy of the frame block, re-packed into every draw each frame, because the
    /// pipeline was allowed exactly one uniform buffer. Since #604 the vertex reads <c>ViewProj</c> straight out
    /// of the shared frame block at set 0 binding 0. The palette followed it out in #407 (see
    /// <see cref="PackSkinnedBonePalette"/>), because those bytes were identical in this pass and in every
    /// shadow cascade. What is left is 128 bytes that really are per draw.
    /// </para></summary>
    public void PackSkinnedMainSlot(uint slot, in Matrix4x4 model,
        Vector4 tint, Vector4 emissive, Vector4 specParams, Vector2 dissolve, float isDynamic = 1f)
    {
        uint baseOff = slot * SkinnedMainSlotBytes;
        _skinnedHeaderScratch[0] = model;
        // Row 3 is the P matrix's 4th column in the shader (GLSL reads the raw bytes column-major). Its .x carries
        // the dynamic-geometry decal mask (SkinnedModelVert -> vDynamic): every GPU-skinned draw is a skinned
        // character, so it defaults to 1 (dynamic), and the skinned fragment writes normal-target alpha 0 to keep
        // the main ground-decal pass off it. The row's last two components carry the dissolve parameters.
        _skinnedHeaderScratch[1] = new Matrix4x4(
            tint.X, tint.Y, tint.Z, tint.W,
            emissive.X, emissive.Y, emissive.Z, emissive.W,
            specParams.X, specParams.Y, specParams.Z, specParams.W,
            isDynamic, 0f, dissolve.X, dissolve.Y);
        // Straight into the persistent full-buffer image. UploadSkinnedMainSlots sends that image once every
        // slot is ready.
        Span<byte> destination = _skinnedMainImage.AsSpan(checked((int)baseOff), checked((int)SkinnedMainSlotBytes));
        MemoryMarshal.AsBytes<Matrix4x4>(_skinnedHeaderScratch).CopyTo(destination);
    }

    /// <summary>Ensure the shared per-caster bone palette holds at least <paramref name="slotCount"/> slots.
    /// ONE slot per CASTER: the main pass and every shadow cascade read the same one (#407).</summary>
    public void EnsureSkinnedBonePaletteCapacity(uint slotCount) => _bonePalette.EnsureCapacity(slotCount);

    /// <summary>Pack one caster's composed <paramref name="bones"/> into its palette slot (uploaded raw, read
    /// column-major = their transpose, so the shader blend equals <see cref="SkinningMath.SkinVertex"/>). Only
    /// the mesh's own bones are written (indices load-validated &lt; boneCount). Call once per caster per frame,
    /// for every caster in either pass, before <see cref="UploadSkinnedBonePalette"/>.</summary>
    public void PackSkinnedBonePalette(uint slot, ReadOnlySpan<Matrix4x4> bones) => _bonePalette.Pack(slot, bones);

    /// <summary>Upload every packed palette in ONE whole-buffer write, before either pass draws.</summary>
    public void UploadSkinnedBonePalette(IGpuCommandList cl) => _bonePalette.Upload(cl);

    /// <summary>Upload every packed GPU-skinned main slot in one whole-buffer write. Slots without a visible-main
    /// draw may retain old bytes because no draw binds them this frame.</summary>
    public void UploadSkinnedMainSlots(IGpuCommandList cl)
        => cl.UpdateBuffer(_skinnedMainUbo!, 0, (ReadOnlySpan<byte>)_skinnedMainImage);

    /// <summary>Bind the GPU-skinning model pipeline. Call after <see cref="BeginModelPass"/>/
    /// <see cref="SetFrameUniforms"/>, before the skinned draw loop.</summary>
    public void BindSkinnedPass(IGpuCommandList cl) => cl.SetPipeline(_skinnedPipeline);

    /// <summary>Bind the GPU-skinning CharDissolve pipeline variant (same layouts, dissolve fragment).</summary>
    public void BindSkinnedDissolvePass(IGpuCommandList cl) => cl.SetPipeline(_skinnedDissolvePipeline);

    /// <summary>Draw one GPU-skinned mesh: its rest-pose <paramref name="restVb"/> (uploaded once at load) at
    /// vertex slot 0, set 0 carrying the shared frame block plus this draw's per-draw window (selected by the
    /// dynamic offset <paramref name="slot"/> * <see cref="SkinnedMainSlotBytes"/>, which applies to binding 3
    /// alone because it is the only element the layout declares dynamic), <paramref name="skinnedFragSet"/>
    /// (or the white default when null) at set 1, and the shared bone palette at set 2 selected by
    /// <paramref name="paletteSlot"/>. One <c>instanceCount=1</c> indexed draw. The GPU skins in the vertex
    /// shader. A pipeline (<see cref="BindSkinnedPass"/>/<see cref="BindSkinnedDissolvePass"/>) must be
    /// bound.
    /// <para>
    /// The two slots are separate parameters because the two buffers are indexed by different things: the palette
    /// is per CASTER and the shadow pass reaches the same slot for a different cascade, while
    /// <paramref name="slot"/> indexes this pass's own per-draw window. In this pass they happen to be the same
    /// number, and it is the caller that knows so.
    /// </para></summary>
    public void DrawGpuSkinned(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat indexFormat, uint slot, uint paletteSlot, IGpuResourceSet? skinnedFragSet)
    {
        cl.SetGraphicsResourceSet(0, _skinnedMainSet!, slot * SkinnedMainSlotBytes);
        cl.SetGraphicsResourceSet(1, skinnedFragSet ?? _skinnedDefaultFragSet);
        cl.SetGraphicsResourceSet(2, _bonePalette.Set, SkinnedBonePalette.OffsetFor(paletteSlot));
        // The skinned variant's set 3: the motion block and this caster's last frame, at its own slot.
        if (_motion is { } motion)
            cl.SetGraphicsResourceSet(3, motion.SkinnedPalette.Set, SkinnedMotionPalette.OffsetFor(paletteSlot));
        cl.SetVertexBuffer(0, restVb);
        cl.SetIndexBuffer(ib, indexFormat);
        cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
    }

    /// <summary>Ensure the shadow map's skinned-depth UBO holds <paramref name="slotCount"/> slots (grows
    /// + retires like the main one). Forwards to <see cref="ShadowMapRenderer"/>.</summary>
    public void EnsureSkinnedShadowCapacity(uint slotCount) => _shadowMap.EnsureSkinnedShadowCapacity(slotCount);

    /// <summary>Pack one GPU-skinned caster's shadow-depth slot for one cascade: <c>LightMvp = model *
    /// cascadeDepthMat</c> folded per draw, and nothing else since #407 moved the palette to its own buffer.
    /// <paramref name="cascadeDepthMat"/> is that cascade's GPU-clip-corrected AND column-transformed matrix.
    /// Forwards to <see cref="ShadowMapRenderer"/>.</summary>
    public void PackSkinnedShadowSlot(uint slot, in Matrix4x4 model, in Matrix4x4 cascadeDepthMat) =>
        _shadowMap.PackSkinnedShadowSlot(slot, model, cascadeDepthMat);

    /// <summary>Pack one DISSOLVING GPU-skinned caster's shadow-depth slot for one cascade (issue #387): the
    /// folded <c>LightMvp</c> plus the render-relative <paramref name="model"/>, <paramref name="renderOrigin"/>,
    /// that cascade's <paramref name="noiseScale"/> and the caster's <paramref name="dissolveThreshold"/>. Forwards
    /// to <see cref="ShadowMapRenderer"/>.</summary>
    public void PackSkinnedShadowSlot(uint slot, in Matrix4x4 model, in Matrix4x4 cascadeDepthMat,
        Vector3 renderOrigin, float noiseScale, float dissolveThreshold) =>
        _shadowMap.PackSkinnedShadowSlot(slot, model, cascadeDepthMat, renderOrigin, noiseScale, dissolveThreshold);

    /// <summary>Upload every packed GPU-skinned shadow slot in one whole-buffer write.</summary>
    public void UploadSkinnedShadowSlots(IGpuCommandList cl) => _shadowMap.UploadSkinnedShadowSlots(cl);

    /// <summary>Bind cascade <paramref name="cascade"/> for the GPU-skinning depth draws: scissor its atlas column
    /// and switch to the skinned depth pipeline, or its dissolve-aware sibling when <paramref name="dissolve"/> is
    /// set (issue #387). Call per cascade after the rigid runs. Forwards to <see cref="ShadowMapRenderer"/>.</summary>
    public void BindShadowCascadeSkinned(IGpuCommandList cl, int cascade, bool dissolve = false) =>
        _shadowMap.BindCascadeSkinned(cl, cascade, dissolve);

    /// <summary>Draw one GPU-skinned caster into the CURRENTLY-BOUND cascade (rest-pose vertex buffer, the
    /// caster-cascade light-matrix slot at <paramref name="slot"/>, and the shared per-caster palette at
    /// <paramref name="paletteSlot"/>). <see cref="BindShadowCascadeSkinned"/> must be bound. Forwards to
    /// <see cref="ShadowMapRenderer"/>.</summary>
    public void DrawGpuSkinnedShadowCaster(IGpuCommandList cl, IGpuBuffer restVb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat indexFormat, uint slot, uint paletteSlot) =>
        _shadowMap.DrawGpuSkinnedCaster(cl, restVb, ib, indexCount, indexFormat, slot, paletteSlot);
}
