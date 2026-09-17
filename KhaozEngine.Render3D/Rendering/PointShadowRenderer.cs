using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The depth-only pass that fills the point-light shadow atlas: four pipelines (three caster variants plus the
    /// row clear), one 256-byte-aligned dynamic uniform slot per (light, face), and the scissor bookkeeping that
    /// keeps one cell's geometry inside one cell.
    /// <para>
    /// It is <see cref="ShadowMapRenderer"/> one geometry over, and deliberately so: same R32F single-colour target,
    /// same one-buffer dynamic-offset slot ring, same reuse of the model pass's already-uploaded instance buffer,
    /// same per-cell scissor standing in for a viewport the command-list seam does not have. What differs is the
    /// projection (a 90 degree face rather than a cascade ortho), the stored value (linear distance over radius
    /// rather than clip depth) and the clear (per row, by a quad).
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTHING IS CULLED BY FACE WINDING HERE, which is the one place this pass deliberately departs from the
    /// cascade one. Two reasons, either sufficient. The face bases are mirrored (see <see cref="PointShadowMath"/>),
    /// so winding is reversed in this pass and a cull mode would mean the opposite of what it says. And the stored
    /// value wants the NEAREST surface: the receiver's compare is <c>distance &lt;= stored + bias</c>, so the front
    /// face is the one to keep, where the cascade pass keeps the far side as a bias trick a directional light can
    /// afford.
    /// </para>
    /// <para>
    /// THE UNIFORM SLOT RING IS THE SKINNED DEPTH RING'S, at <see cref="FaceSlotBytes"/> each, grown geometrically
    /// and uploaded whole from offset 0 (only a whole-buffer write escapes Direct3D 11's partial-uniform-write
    /// staging route, see the note on ModelRenderer's frame image). One slot per face, six faces per light, so a
    /// frame rendering three lights packs eighteen slots and uploads once.
    /// </para>
    /// </remarks>
    internal sealed class PointShadowRenderer : IDisposable
    {
        /// <summary>One 256-byte-aligned dynamic slot per (light, face). The payload is 96 bytes (a mat4, the light
        /// position and radius, and the dissolve noise scale beside the render origin); 256 is the alignment every
        /// supported backend is safe at and the Direct3D 11 friendly 16-constant multiple.</summary>
        internal const uint FaceSlotBytes = 256;

        readonly IGpuDevice _gd;
        readonly PointShadowAtlas _atlas;
        readonly IGpuShaderSet _shaders;
        readonly IGpuShaderSet _dissolveShaders;
        readonly IGpuShaderSet _dissolveInvertedShaders;
        readonly IGpuShaderSet _clearShaders;
        readonly IGpuResourceLayout _layout;     // set 0: this face's matrix + light + noise (dynamic-offset UBO)
        readonly IGpuPipeline _pipeline;
        readonly IGpuPipeline _dissolvePipeline;
        readonly IGpuPipeline _dissolveInvertedPipeline;
        readonly IGpuPipeline _clearPipeline;

        IGpuBuffer _faceUbo;
        IGpuResourceSet _set;
        uint _faceSlots;
        byte[] _faceImage = Array.Empty<byte>();
        // Grown-out slot buffers and their window sets. A prior frame's command list may still be reading one, so
        // they are retired rather than disposed inline, exactly as the instance buffer is.
        readonly List<IDisposable> _retired = new();
        // The whole atlas is cleared once, the first time the pass binds it, so a row nobody has ever rendered
        // still reads 1.0. Every clear after that is per row and goes through the scissored quad (decision 7).
        bool _cleared;

        public PointShadowRenderer(IGpuDevice gd, PointShadowAtlas atlas)
        {
            ArgumentNullException.ThrowIfNull(gd);
            ArgumentNullException.ThrowIfNull(atlas);
            _gd = gd;
            _atlas = atlas;
            IGpuResourceFactory f = gd.Factory;

            _shaders = f.CreateShadersFromSpirv(ShaderSources.PointShadowRigidVert, ShaderSources.PointShadowRigidFrag);
            _dissolveShaders = f.CreateShadersFromSpirv(
                ShaderSources.PointShadowRigidDissolveVert, ShaderSources.PointShadowRigidDissolveFrag);
            _dissolveInvertedShaders = f.CreateShadersFromSpirv(
                ShaderSources.PointShadowRigidDissolveVert, ShaderSources.PointShadowRigidDissolveInvertedFrag);
            _clearShaders = f.CreateShadersFromSpirv(ShaderSources.PointShadowClearVert, ShaderSources.PointShadowClearFrag);

            // One layout for all four pipelines. Both stages read it: the vertex takes the matrix and the fragment
            // takes the light position and radius it divides the distance by.
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer,
                    GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true)));

            GpuOutputDescription outputs = atlas.Framebuffer.Outputs;
            _pipeline = BuildCasterPipeline(f, outputs, _shaders, dissolve: false);
            _dissolvePipeline = BuildCasterPipeline(f, outputs, _dissolveShaders, dissolve: true);
            _dissolveInvertedPipeline = BuildCasterPipeline(f, outputs, _dissolveInvertedShaders, dissolve: true);
            _clearPipeline = BuildClearPipeline(f, outputs);

            // One light's worth of slots up front, so the window set exists before the first pack and ClearRow can
            // bind it at offset 0 whether or not anything has been packed yet.
            (_faceUbo, _set, _faceSlots) = AllocateSlots(PointShadowMath.FaceCount);
            _faceImage = new byte[checked((int)(_faceSlots * FaceSlotBytes))];
        }

        /// <summary>The atlas this pass writes into.</summary>
        public PointShadowAtlas Atlas => _atlas;

        /// <summary>Ensure the slot ring holds <paramref name="slotsThisFrame"/> lights' worth of faces, growing
        /// geometrically and retiring the replaced buffer and window set (a prior frame may still be reading
        /// them). Call before the frame's first <see cref="PackFace"/>.</summary>
        public void EnsureFaceCapacity(int slotsThisFrame)
        {
            uint wanted = (uint)Math.Max(1, slotsThisFrame) * PointShadowMath.FaceCount;
            if (_faceSlots >= wanted) return;
            _retired.Add(_faceUbo);
            _retired.Add(_set);
            uint grown = Math.Max(wanted, _faceSlots * 2);
            (_faceUbo, _set, _faceSlots) = AllocateSlots(grown);
            var image = new byte[checked((int)(_faceSlots * FaceSlotBytes))];
            _faceImage.AsSpan().CopyTo(image);
            _faceImage = image;
        }

        /// <summary>Pack one face's slot: its clip-corrected world-to-cell matrix, the light in the SAME space the
        /// caster geometry is in (render space when a render origin is in force) with its radius, and the dissolve
        /// noise scale beside this frame's render origin (which the dissolve variants add back to reach absolute
        /// world space). <paramref name="index"/> is the flat (light * 6 + face) slot.</summary>
        public void PackFace(int index, in Matrix4x4 faceViewProjRenderSpace, Vector3 lightPosRenderSpace,
            float radius, float noiseScale, Vector3 renderOrigin)
        {
            var lightPosRadius = new Vector4(lightPosRenderSpace, radius);
            var noise = new Vector4(noiseScale, renderOrigin.X, renderOrigin.Y, renderOrigin.Z);
            Span<byte> slot = _faceImage.AsSpan(
                checked((int)((uint)index * FaceSlotBytes)), checked((int)FaceSlotBytes));
            MemoryMarshal.Write(slot, in faceViewProjRenderSpace);
            MemoryMarshal.Write(slot.Slice(64), in lightPosRadius);
            MemoryMarshal.Write(slot.Slice(80), in noise);
        }

        /// <summary>Upload every packed slot in ONE whole-buffer write. Must run OUTSIDE the pass (before
        /// <see cref="BeginPass"/>), like the cascade pass's own upload, and covers offset 0 to the buffer size so
        /// Direct3D 11 takes its cheap whole-buffer route.</summary>
        public void UploadFaces(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);
            cl.UpdateBuffer(_faceUbo, 0, (ReadOnlySpan<byte>)_faceImage);
        }

        /// <summary>Bind the atlas framebuffer. The FIRST call in the atlas's life also clears the whole thing to
        /// 1.0 (no caster) and depth to 1.0, by an ordinary whole-framebuffer clear with no scissor in force, so a
        /// row that is never rendered still reads as unshadowed. Every later row clear is
        /// <see cref="ClearRow"/>'s scissored quad.</summary>
        public void BeginPass(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);
            cl.SetFramebuffer(_atlas.Framebuffer);
            if (_cleared) return;
            cl.ClearColorTarget(0, new Primitives.Color(1f, 1f, 1f, 1f));
            cl.ClearDepthStencil(1f);
            _cleared = true;
        }

        /// <summary>Clear one light row: scissor to the row's full width and draw the depth-ALWAYS clear quad, which
        /// writes 1.0 into every one of that row's six cells and resets their depth. Never
        /// <c>ClearColorTarget</c>: whether a clear honours the scissor differs per backend and a scissored draw
        /// does not (design decision 7). <see cref="BeginPass"/> must be bound.</summary>
        public void ClearRow(IGpuCommandList cl, int slot)
        {
            ArgumentNullException.ThrowIfNull(cl);
            uint res = (uint)_atlas.FaceResolution;
            cl.SetPipeline(_clearPipeline);
            cl.SetScissorRect(0, 0, (uint)Math.Clamp(slot, 0, _atlas.Rows - 1) * res, _atlas.Width, res);
            cl.SetGraphicsResourceSet(0, _set, 0);
            cl.Draw(3);
        }

        /// <summary>Bind one cell for the caster draws that follow: the pipeline its <paramref name="kind"/> asks
        /// for, the cell's scissor, and the slot <paramref name="packedIndex"/> named through the dynamic offset.
        /// <see cref="ShadowCastKind.None"/> is not a caster and is refused rather than silently drawn.</summary>
        public void BeginFace(IGpuCommandList cl, int packedIndex, int face, int slot, ShadowCastKind kind)
        {
            ArgumentNullException.ThrowIfNull(cl);
            cl.SetPipeline(kind switch
            {
                ShadowCastKind.Dissolving => _dissolvePipeline,
                ShadowCastKind.DissolvingInverted => _dissolveInvertedPipeline,
                ShadowCastKind.Opaque => _pipeline,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                    "ShadowCastKind.None writes no depth, so it has no point-shadow pipeline."),
            });
            uint res = (uint)_atlas.FaceResolution;
            cl.SetScissorRect(0, (uint)Math.Clamp(face, 0, PointShadowMath.FaceCount - 1) * res,
                (uint)Math.Clamp(slot, 0, _atlas.Rows - 1) * res, res, res);
            cl.SetGraphicsResourceSet(0, _set, (uint)packedIndex * FaceSlotBytes);
        }

        /// <summary>Draw one caster run into the CURRENTLY-BOUND cell: <paramref name="instanceCount"/> instances
        /// from <paramref name="instanceStart"/> of the model pass's shared instance buffer, so this pass costs no
        /// second upload. <see cref="BeginFace"/> must be bound.</summary>
        public void DrawCasterRun(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, IGpuBuffer instanceBuffer, uint instanceStart, uint instanceCount)
        {
            ArgumentNullException.ThrowIfNull(cl);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, instanceBuffer);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Reset the scissor to the full framebuffer after the pass, because the next pass expects a full
        /// one. Call once per <see cref="BeginPass"/>.</summary>
        public void EndPass(IGpuCommandList cl)
        {
            ArgumentNullException.ThrowIfNull(cl);
            cl.SetFullScissorRects();
        }

        (IGpuBuffer Buffer, IGpuResourceSet Set, uint Slots) AllocateSlots(uint slots)
        {
            IGpuResourceFactory f = _gd.Factory;
            IGpuBuffer buffer = f.CreateBuffer(new GpuBufferDescription(slots * FaceSlotBytes, GpuBufferUsage.UniformBuffer));
            IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(
                _layout, new GpuBufferRange(buffer, 0, FaceSlotBytes)));
            return (buffer, set, slots);
        }

        // The caster pipelines. The vertex stream is the model pass's, slot 0 per-vertex and slot 1 per-instance,
        // so the already-uploaded instance buffer binds unchanged and the dissolve variants simply declare three
        // more of its trailing elements. Depth test LESS-OR-EQUAL with write on, so the nearest caster wins a texel
        // against the row clear's 1.0. Scissor on, because the cell placement is a bake plus a scissor.
        IGpuPipeline BuildCasterPipeline(IGpuResourceFactory f, GpuOutputDescription outputs,
            IGpuShaderSet shaders, bool dissolve)
        {
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
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
                instanceElements.Add(new GpuVertexElement("IDissolveComplement", GpuVertexElementFormat.Float1));
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
                // No winding cull: see the type remarks. depthClip stays ON so the far plane (the light radius)
                // still clips, and the near plane clips too, which is correct for a point light (there is no
                // directional pancake to preserve here).
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = outputs,
            });
        }

        // The row-clear pipeline: a fullscreen triangle with no vertex inputs, depth ALWAYS and depth write on, so
        // one scissored draw resets a row's stored distance and its depth together.
        IGpuPipeline BuildClearPipeline(IGpuResourceFactory f, GpuOutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = new GpuDepthStencilState(true, true, GpuComparison.Always),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _clearShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        public void Dispose()
        {
            _clearPipeline.Dispose();
            _dissolveInvertedPipeline.Dispose();
            _dissolvePipeline.Dispose();
            _pipeline.Dispose();
            _set.Dispose();
            _faceUbo.Dispose();
            _layout.Dispose();
            _clearShaders.Dispose();
            _dissolveInvertedShaders.Dispose();
            _dissolveShaders.Dispose();
            _shaders.Dispose();
            foreach (IDisposable r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
