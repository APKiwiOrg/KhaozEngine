using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Camera-relative rendering: the half of <see cref="Scene3D"/> that owns the render origin and every
    /// world-to-render-frame reduction. See <c>docs/design/FLOATING-ORIGIN-DESIGN-2026-07-27.md</c> for the why.
    /// <para>
    /// ONE rule governs the whole file: <b>the subtraction happens where data is built for the GPU, and nowhere
    /// else.</b> Every submission queue stays ABSOLUTE, so every CPU-side spatial computation (frustum culling, the
    /// terrain identity fast path, shadow-caster classification, cascade fitting, the four transparency sorts) runs
    /// on absolute inputs and is byte-identical to the pre-floating-origin engine. Only the copy that reaches the
    /// GPU is relative. A site that forgets the reduction renders visibly displaced at range, which is loud; a cull
    /// that silently ran in the wrong space is not.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // The explicit origin a consumer set, or null for the automatic quantized-eye default. Kept separate from
        // the latched value so "never set" and "deliberately set to Zero" (the opt-out) stay distinguishable.
        Vector3? _renderOriginOverride;
        // LATCHED at Begin(): what everything submitted this frame is expressed against on its way to the GPU.
        Vector3 _frameOrigin;
        bool _frameOriginActive;

        // Reused staging buffers for the GPU-bound copies. Grown, never per-frame allocated, exactly like
        // _instanceVisible: a staging copy costs one pass over the stream and buys an invariant that no later edit
        // can quietly break by inserting a CPU read between a subtract and an add-back.
        readonly List<ModelRenderer.InstanceData> _instanceDataRelative = new();
        readonly List<GroundDecal> _decalsRelative = new();
        readonly List<WaterPlane> _waterPlanesRelative = new();
        readonly List<DistortionSprite> _distortionRelative = new();
        readonly List<TrailSample> _trailSamplesRelative = new();
        // Per-cascade ABSOLUTE fit matrices, beside the render-relative ones in _cascadeCpuVps. The CPU
        // caster-visibility test extracts its frustums from these, so caster classification stays in absolute space
        // against absolute bounds and is byte-identical to the pre-release engine.
        readonly Matrix4x4[] _cascadeCpuVpsAbsolute = new Matrix4x4[ShadowSettings.MaxCascades];

        /// <summary>
        /// The origin every world position submitted this frame is expressed against before it reaches the GPU.
        /// Defaults to <c>WorldFrame.Nearest(ActiveCamera.Eye).Anchor</c>: quantized to the 128 m frame grid so it
        /// does not jitter per frame (an unquantized eye-following origin makes goldens irreproducible) and exactly
        /// representable in float32, so the reduction introduces no error at all. Set it explicitly to a simulation
        /// frame's anchor when running one, so render and simulation share a space.
        /// <see cref="System.Numerics.Vector3.Zero"/> reproduces the pre-floating-origin output exactly, and is the
        /// opt-out for a consumer with goldens it has not rebaked.
        /// <para>
        /// LATCHED AT <see cref="Begin"/>: the value in force for a frame is read once, at Begin, and a write during
        /// the frame is ignored until the next one. So the getter returns THIS frame's origin, not necessarily what
        /// was last assigned. A frame that submitted half its geometry against one origin and uploaded it against
        /// another would be displaced by the difference, and would be stable enough between re-anchors to read as a
        /// content bug rather than as a renderer one.
        /// </para>
        /// </summary>
        public Vector3 RenderOrigin
        {
            get => _frameOrigin;
            set => _renderOriginOverride = value;
        }

        /// <summary>
        /// Whether the render origin is actually in effect this frame. False when <see cref="RenderOrigin"/> latched
        /// to zero, and false when <see cref="CameraOverride"/> is a camera that does not implement
        /// <see cref="IRenderOriginAware"/> - in which case the WHOLE pipeline falls back to the pre-release absolute
        /// path rather than half-applying an origin the camera cannot honour. A consumer camera is then exactly as
        /// precise as it was before this feature existed, and this property says so.
        /// </summary>
        public bool RenderOriginActive => _frameOriginActive;

        /// <summary>
        /// Latch this frame's render origin. Called first thing in <see cref="Begin"/>, so every submission that
        /// follows lands in one frame, and so the camera is already in that frame for any
        /// <c>WorldToScreen</c> a consumer runs between Begin and the render.
        /// </summary>
        void LatchRenderOrigin()
        {
            IIsoCamera3D cam = ActiveCamera;
            Vector3 wanted = _renderOriginOverride ?? WorldFrame.Nearest(cam.Eye).Anchor;
            // Whole-pipeline fallback, never a partial one: a camera that cannot build its view against an origin
            // renders entirely absolute, so its geometry and its view can never end up in different spaces.
            if (cam is not IRenderOriginAware) wanted = Vector3.Zero;
            _frameOrigin = wanted;
            _frameOriginActive = wanted != Vector3.Zero;
            ApplyOriginToCamera(cam);
        }

        /// <summary>Push the latched origin onto <paramref name="cam"/> when it can take one. Idempotent, and
        /// re-asserted at render time because a consumer may swap <see cref="CameraOverride"/> between
        /// <see cref="Begin"/> and the render.</summary>
        void ApplyOriginToCamera(IIsoCamera3D cam)
        {
            if (cam is IRenderOriginAware aware) aware.RenderOrigin = _frameOrigin;
        }

        /// <summary>
        /// This frame's RENDER-RELATIVE view-projection: what every GPU pass rasterizes with. Re-asserts the latched
        /// origin on the camera first.
        /// <para>
        /// The fallback branch handles the one case the Begin-time latch cannot: <see cref="CameraOverride"/> swapped
        /// to a camera that is NOT <see cref="IRenderOriginAware"/> after the frame's vertices were already reduced.
        /// Composing the translation onto its view-projection is geometrically correct and no less precise than that
        /// camera ever was, which beats rendering the frame 128 m from where it belongs.
        /// </para>
        /// </summary>
        Matrix4x4 FrameViewProjection()
        {
            IIsoCamera3D cam = ActiveCamera;
            if (cam is IRenderOriginAware aware)
            {
                aware.RenderOrigin = _frameOrigin;
                return cam.ViewProjection;
            }
            return _frameOriginActive
                ? Matrix4x4.CreateTranslation(-_frameOrigin) * cam.ViewProjection
                : cam.ViewProjection;
        }

        /// <summary>
        /// This frame's ABSOLUTE view-projection: the pre-shift matrix every CPU-side spatial computation runs
        /// against (frustum culling, shadow-cascade fitting, caster classification), so those paths stay
        /// byte-identical to the pre-release engine at any origin. Identical to
        /// <see cref="FrameViewProjection"/> when the origin is zero.
        /// </summary>
        Matrix4x4 FrameAbsoluteViewProjection() =>
            ActiveCamera is IRenderOriginAware aware ? aware.AbsoluteViewProjection : ActiveCamera.ViewProjection;

        /// <summary>A world point in this frame's render space. Exact under the design's lemma: the origin is an
        /// exact multiple of the frame grid and the result is smaller in magnitude than the input, so the
        /// subtraction introduces no error at all.</summary>
        Vector3 ToRender(Vector3 world) => world - _frameOrigin;

        /// <summary>
        /// An affine world matrix in this frame's render space: the TRANSLATION COLUMN moves, nothing else does.
        /// For an affine matrix the translation column IS the world position of the local origin, so this is exactly
        /// <c>T(-origin) * m</c> with no rounding beyond the (exact) subtraction. It also fixes the identity draws
        /// for free: terrain chunks and merged HLOD meshes are submitted at <see cref="Matrix4x4.Identity"/> with
        /// absolute vertices, and an identity's translation column becomes <c>-origin</c>, which is what geometry
        /// with absolute vertices needs.
        /// </summary>
        Matrix4x4 ToRender(Matrix4x4 m)
        {
            AssertAffine(m);
            m.M41 -= _frameOrigin.X;
            m.M42 -= _frameOrigin.Y;
            m.M43 -= _frameOrigin.Z;
            return m;
        }

        /// <summary>The translation-column reduction assumes an affine matrix (a fourth ROW of (0,0,0,1), i.e. no
        /// projective terms in M14/M24/M34). Nothing in the engine submits a projective model matrix; this documents
        /// the assumption where it lives and compiles out of Release.</summary>
        [Conditional("DEBUG")]
        static void AssertAffine(in Matrix4x4 m) =>
            System.Diagnostics.Debug.Assert(m.M14 == 0f && m.M24 == 0f && m.M34 == 0f,
                "Scene3D was handed a projective model matrix. The render-origin reduction moves the translation " +
                "column, which is only equivalent to T(-origin) * m for an affine transform.");

        /// <summary>
        /// Upload this frame's grouped instances to the GPU in the render frame. <c>_instanceData</c> itself stays
        /// ABSOLUTE for its whole CPU life - <see cref="ComputeMainPassVisibility"/> (including the terrain identity
        /// fast path) and <see cref="CaptureShadowCasters"/> both read it after this call - so the reduction lands in
        /// a reused staging copy that differs only in each matrix's translation column. With no origin in force the
        /// absolute list is uploaded directly, so the byte traffic and the frame are unchanged.
        /// </summary>
        void UploadInstancesRelative(IGpuCommandList cl)
        {
            if (_instanceData.Count == 0) return;
            List<ModelRenderer.InstanceData> src = _instanceData;
            if (_frameOriginActive)
            {
                _instanceDataRelative.Clear();
                for (int i = 0; i < src.Count; i++)
                {
                    ModelRenderer.InstanceData d = src[i];
                    d.Model = ToRender(d.Model);
                    _instanceDataRelative.Add(d);
                }
                src = _instanceDataRelative;
            }
            _model.UploadInstances(cl, CollectionsMarshal.AsSpan(src));
            _frameStats.BufferUpdateBytes += (long)src.Count * Unsafe.SizeOf<ModelRenderer.InstanceData>();
        }

        /// <summary>The queued ground decals with their centres in the render frame. The queue itself stays
        /// absolute; the staging list is reused across both decal passes (each consumes its span inside the
        /// call).</summary>
        ReadOnlySpan<GroundDecal> RelativeDecals(List<GroundDecal> src)
        {
            if (!_frameOriginActive) return CollectionsMarshal.AsSpan(src);
            _decalsRelative.Clear();
            for (int i = 0; i < src.Count; i++)
            {
                GroundDecal d = src[i];
                d.Center = ToRender(d.Center);
                _decalsRelative.Add(d);
            }
            return CollectionsMarshal.AsSpan(_decalsRelative);
        }

        /// <summary>The queued water planes with their centres in the render frame. The surface height moves with
        /// the origin's Y for consistency with every other reduction; a <c>WorldFrame</c> anchor has Y = 0, so in
        /// practice the height is untouched (Y is never framed).</summary>
        ReadOnlySpan<WaterPlane> RelativeWaterPlanes()
        {
            if (!_frameOriginActive) return CollectionsMarshal.AsSpan(_waterPlanes);
            _waterPlanesRelative.Clear();
            for (int i = 0; i < _waterPlanes.Count; i++)
            {
                WaterPlane p = _waterPlanes[i];
                _waterPlanesRelative.Add(new WaterPlane(p.CenterX - _frameOrigin.X, p.SurfaceY - _frameOrigin.Y,
                    p.CenterZ - _frameOrigin.Z, p.HalfExtentX, p.HalfExtentZ));
            }
            return CollectionsMarshal.AsSpan(_waterPlanesRelative);
        }

        /// <summary>The queued distortion sprites with their positions in the render frame.</summary>
        ReadOnlySpan<DistortionSprite> RelativeDistortionSprites()
        {
            if (!_frameOriginActive) return CollectionsMarshal.AsSpan(_distortionSprites);
            _distortionRelative.Clear();
            for (int i = 0; i < _distortionSprites.Count; i++)
            {
                DistortionSprite s = _distortionSprites[i];
                s.Position = ToRender(s.Position);
                _distortionRelative.Add(s);
            }
            return CollectionsMarshal.AsSpan(_distortionRelative);
        }

        /// <summary>One particle sprite in the render frame. Applied while building the back-to-front sorted stream,
        /// i.e. AFTER the sort has already keyed on absolute centres.</summary>
        ParticleSprite ToRender(ParticleSprite s)
        {
            s.Position = ToRender(s.Position);
            return s;
        }

        /// <summary>One trail's samples in the render frame, into a reused buffer. Applied at the geometry
        /// expansion, so the caller's queued samples stay absolute.</summary>
        ReadOnlySpan<TrailSample> RelativeTrailSamples(ReadOnlySpan<TrailSample> src)
        {
            if (!_frameOriginActive) return src;
            _trailSamplesRelative.Clear();
            for (int i = 0; i < src.Length; i++)
                _trailSamplesRelative.Add(src[i] with { Position = ToRender(src[i].Position) });
            return CollectionsMarshal.AsSpan(_trailSamplesRelative);
        }

        /// <summary>
        /// True when <paramref name="m"/> is (within a small epsilon) the identity transform, so a mesh's local-space
        /// AABB doubles as its world AABB (terrain chunks draw at identity with world-space verts).
        /// <para>
        /// It lives in this file because it is the clearest thing the subtraction point protects. Had the reduction
        /// happened at SUBMISSION rather than on the GPU-bound copy, every terrain chunk in the world would carry a
        /// <c>-origin</c> translation, fail this test permanently, and fall to the far more conservative bounding-
        /// sphere cull for the rest of the program's life: a silent, whole-scene overdraw regression that no golden
        /// would ever show. <c>_instanceData</c> staying absolute is what keeps this returning true.
        /// </para>
        /// </summary>
        static bool IsIdentityTransform(in Matrix4x4 m)
        {
            const float e = 1e-5f;
            return MathF.Abs(m.M11 - 1f) < e && MathF.Abs(m.M22 - 1f) < e && MathF.Abs(m.M33 - 1f) < e && MathF.Abs(m.M44 - 1f) < e
                && MathF.Abs(m.M12) < e && MathF.Abs(m.M13) < e && MathF.Abs(m.M14) < e
                && MathF.Abs(m.M21) < e && MathF.Abs(m.M23) < e && MathF.Abs(m.M24) < e
                && MathF.Abs(m.M31) < e && MathF.Abs(m.M32) < e && MathF.Abs(m.M34) < e
                && MathF.Abs(m.M41) < e && MathF.Abs(m.M42) < e && MathF.Abs(m.M43) < e;
        }

        /// <summary>
        /// Build the render-relative cascade matrix beside the absolute fitted one. The dirty check that decides
        /// whether the shadow atlas can be reused compares <see cref="_cascadeCpuVps"/> frame to frame, so it is
        /// these RELATIVE matrices it sees: an origin step changes them by the step and forces the depth pass to
        /// re-render, which is what stops the atlas being reused against a frame it was not baked for. The rotation is unchanged and
        /// only the focus moves, so the ortho extents and the texel world size are identical; the light-space texel
        /// snap is not origin-invariant, so a shadow edge can jump by one texel for the one frame an origin steps
        /// (accepted and documented in the design doc, section 9).
        /// </summary>
        void FitCascade(int i, Vector3 lightDir, Vector3 center, float radius, int resolution)
        {
            _cascadeCpuVpsAbsolute[i] = Internal.ShadowMapMath.BuildLightViewProj(lightDir, center, radius, resolution);
            _cascadeCpuVps[i] = _frameOriginActive
                ? Internal.ShadowMapMath.BuildLightViewProj(lightDir, ToRender(center), radius, resolution)
                : _cascadeCpuVpsAbsolute[i];
        }
    }
}
