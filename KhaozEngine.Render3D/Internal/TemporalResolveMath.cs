using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The CPU half of the temporal resolve (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3): the reprojection matrices, the
    /// depth parameters and the lock decay the shader reads, and C# mirrors of the kernel, the depth linearisation and the
    /// luma weighting it applies, kept in sync with <c>ShaderSources.TemporalResolveCoreGlsl</c> so the tests can
    /// compute what the shader must output. Pure and allocation-free.
    /// </summary>
    internal static class TemporalResolveMath
    {
        /// <summary>The Lanczos 2 kernel, <c>sinc(x) * sinc(x / 2)</c> on <c>|x| &lt; 2</c>, zero beyond. Mirrors
        /// <c>lanczos2</c> in the shader.</summary>
        public static float Lanczos2(float x)
        {
            float ax = MathF.Abs(x);
            if (ax < 1.0e-5f) return 1f;
            if (ax >= 2f) return 0f;
            float px = MathF.PI * ax;
            return 2f * MathF.Sin(px) * MathF.Sin(0.5f * px) / (px * px);
        }

        /// <summary>The unjittered pixel position a texel's sample shows. The jittered projection moves content by the
        /// jitter (x right, y down), so the texel centred at <c>texel + 0.5</c> shows what sits at
        /// <c>texel + 0.5 - jitter</c> unjittered. Mirrors the shader's <c>vec2(texel) + 0.5 - jitter</c>.</summary>
        public static Vector2 UnjitteredSamplePosition(Vector2 texel, Vector2 jitterPixels)
            => texel + new Vector2(0.5f) - jitterPixels;

        /// <summary>(1 for a perspective projection else 0, near, far, 0) of a System.Numerics projection with a [0, 1]
        /// depth range. Perspective: <c>near = M43 / M33</c>, <c>far = M43 / (M33 + 1)</c>, as
        /// <see cref="OutlineMath.ExtractCameraDepth"/>. Orthographic: <c>near = M43 / M33</c>,
        /// <c>far = (M43 - 1) / M33</c>.</summary>
        public static Vector4 DepthParams(in Matrix4x4 projection)
        {
            if (MathF.Abs(projection.M34) > 1e-6f)
                return new Vector4(1f, projection.M43 / projection.M33, projection.M43 / (projection.M33 + 1f), 0f);
            return new Vector4(0f, projection.M43 / projection.M33, (projection.M43 - 1f) / projection.M33, 0f);
        }

        /// <summary>View distance from NDC depth. Perspective mirrors <see cref="OutlineMath.LinearizeDepth"/>,
        /// orthographic is linear between near and far. Mirrors <c>temporalLinearDepth</c> in the shader.</summary>
        public static float LinearDepth(float ndcDepth, Vector4 depthParams) => depthParams.X > 0.5f
            ? depthParams.Y * depthParams.Z / (depthParams.Z - ndcDepth * (depthParams.Z - depthParams.Y))
            : depthParams.Y + ndcDepth * (depthParams.Z - depthParams.Y);

        /// <summary>A row-vector view matrix with its translation removed.</summary>
        public static Matrix4x4 RotationOnly(Matrix4x4 view)
        {
            view.M41 = 0f;
            view.M42 = 0f;
            view.M43 = 0f;
            return view;
        }

        /// <summary>This frame's NDC to last frame's clip space, for a surface and for the background. False when a
        /// matrix cannot be inverted, which the resolve treats as no history.</summary>
        public static bool TryReprojection(in TemporalViewInput current, in TemporalViewInput previous,
            out Matrix4x4 currentToPrevious, out Matrix4x4 backgroundToPrevious)
        {
            currentToPrevious = Matrix4x4.Identity;
            backgroundToPrevious = Matrix4x4.Identity;
            if (!Matrix4x4.Invert(current.ViewProjection, out Matrix4x4 inverse)) return false;
            if (!Matrix4x4.Invert(RotationOnly(current.View) * current.Projection, out Matrix4x4 inverseRotation)) return false;
            currentToPrevious = inverse * previous.ViewProjection;
            backgroundToPrevious = inverseRotation * (RotationOnly(previous.View) * previous.Projection);
            return true;
        }

        /// <summary>The resolve's uniforms for one frame. <paramref name="previous"/> is the previous frame view rebased to
        /// this frame's origin, or null when there is none. History is readable only when it is valid and a previous
        /// view exists and both reprojections invert. <paramref name="phaseCount"/> is the jitter cycle the caller
        /// already runs, <see cref="TemporalJitter.PhaseCount"/> of the preset's exact ratio
        /// (<see cref="TemporalSettings.DisplayOverInternal"/>) or of the reciprocal of
        /// <see cref="TemporalSettings.ResolvedUpscaleRatio"/> for an explicit ratio, never of
        /// <see cref="DisplayOverInternal"/>.</summary>
        public static TemporalResolveUniforms BuildUniforms(in TemporalViewInput current, TemporalViewInput? previous,
            Vector2 jitterPixels, int internalWidth, int internalHeight, int displayWidth, int displayHeight,
            bool historyValid, int phaseCount)
        {
            int iw = Math.Max(1, internalWidth), ih = Math.Max(1, internalHeight);
            int dw = Math.Max(1, displayWidth), dh = Math.Max(1, displayHeight);
            Matrix4x4 currentToPrevious = Matrix4x4.Identity, backgroundToPrevious = Matrix4x4.Identity;
            bool readable = historyValid && previous is { } prev
                && TryReprojection(current, prev, out currentToPrevious, out backgroundToPrevious);
            Vector4 currentDepth = DepthParams(current.Projection);
            float displayOverInternal = DisplayOverInternal(dw, dh, iw, ih);
            return new TemporalResolveUniforms
            {
                CurrentToPrevious = currentToPrevious,
                BackgroundToPrevious = backgroundToPrevious,
                Sizes = new Vector4(iw, ih, dw, dh),
                Jitter = new Vector4(jitterPixels.X, jitterPixels.Y, displayOverInternal, readable ? 1f : 0f),
                CurrentDepth = currentDepth,
                PreviousDepth = readable && previous is { } last ? DepthParams(last.Projection) : currentDepth,
                Params = new Vector4(1f / Math.Max(TemporalJitter.NativePhaseCount, phaseCount), 0f, 0f, 0f),
            };
        }

        /// <summary>The larger per-axis ratio of a display size to an internal size, at least 1, with every size below 1
        /// read as 1. A size ratio for sampling only, the resolve's reconstruction footprint (Jitter.z). It must never
        /// feed <see cref="TemporalJitter.PhaseCount"/>. The internal size is the display size times the ratio rounded
        /// to whole pixels, so this can land just above a preset's exact ratio, and <c>ceil(8 * r * r)</c> then takes one
        /// phase more than the preset's cycle, 19 instead of 18 for a 3456x2234 display on Quality.</summary>
        public static float DisplayOverInternal(int displayWidth, int displayHeight, int internalWidth, int internalHeight)
            => MathF.Max(1f, MathF.Max(Math.Max(1, displayWidth) / (float)Math.Max(1, internalWidth),
                Math.Max(1, displayHeight) / (float)Math.Max(1, internalHeight)));

        /// <summary>The depth store's uniforms: this frame's depth parameters.</summary>
        public static TemporalDepthStoreUniforms BuildDepthStore(in Matrix4x4 projection)
            => new() { CurrentDepth = DepthParams(projection) };

        /// <summary>Rec. 709 luma of a linear colour. Mirrors <c>temporalLuma</c>.</summary>
        public static float Luma(Vector3 c) => Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f));

        /// <summary><c>c / (1 + luma(c))</c>: the space the resolve clips and blends in, so a firefly cannot dominate.
        /// Mirrors <c>toWeighted</c>.</summary>
        public static Vector3 ToWeighted(Vector3 c) => c / (1f + Luma(c));

        /// <summary>The inverse of <see cref="ToWeighted"/>. Mirrors <c>fromWeighted</c>.</summary>
        public static Vector3 FromWeighted(Vector3 w) => w / MathF.Max(1f - Luma(w), 1.0e-4f);
    }
}
