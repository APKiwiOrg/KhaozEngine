using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The CPU half of the temporal resolve (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3): the reprojection matrices, the
    /// depth parameters and the lock decay the shader reads, and C# mirrors of the kernel, the depth linearisation, the
    /// static reprojection and the luma weighting it applies, kept in sync with
    /// <c>ShaderSources.TemporalResolveCoreGlsl</c> so the tests can compute what the shader must output. Pure and
    /// allocation-free.
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

        /// <summary>
        /// The two reprojections of <see cref="TemporalResolveUniforms"/>, each built in double and rounded to float
        /// once. <paramref name="currentToPrevious"/> is this frame's unprojection then the rigid view change, from
        /// <c>(ndc.x * w, ndc.y * w, linear depth, 1)</c> to last frame's view space. <paramref name="backgroundToPrevious"/>
        /// is this frame's NDC to last frame's clip space with both views' translation removed. False when the current
        /// view or its rotation cannot be inverted or the current projection has no x or y scale, which the resolve
        /// treats as no history.
        /// <para>
        /// Why the surface matrix ends in view space and not in clip space. An NDC to clip matrix is well conditioned
        /// for x and y but not for depth. Its expected depth is <c>z / w</c> linearised near 1, which multiplies an error
        /// of one float step there by about 6000 at 590 m with a 0.1 m near plane. That leaves 0.03 percent on a still
        /// camera and over 1 percent on a 1.5 m step, against a 2 percent tolerance, even with the matrix built in
        /// double. Built in float it was off by up to 47 percent. The view-space result takes the depth as minus z,
        /// which stays within the depth target's own float quantisation.
        /// </para>
        /// </summary>
        public static bool TryReprojection(in TemporalViewInput current, in TemporalViewInput previous,
            out Matrix4x4 currentToPrevious, out Matrix4x4 backgroundToPrevious)
        {
            currentToPrevious = Matrix4x4.Identity;
            backgroundToPrevious = Matrix4x4.Identity;
            if (!TryUnprojection(current.Projection, out DoubleMatrix4x4 unprojection)) return false;
            if (!DoubleMatrix4x4.TryInvert(new DoubleMatrix4x4(current.View), out DoubleMatrix4x4 inverseView)) return false;
            DoubleMatrix4x4 rotationNow = new DoubleMatrix4x4(RotationOnly(current.View)) * new DoubleMatrix4x4(current.Projection);
            if (!DoubleMatrix4x4.TryInvert(rotationNow, out DoubleMatrix4x4 inverseRotation)) return false;
            DoubleMatrix4x4 viewChange = inverseView * new DoubleMatrix4x4(previous.View);
            DoubleMatrix4x4 rotationThen = new DoubleMatrix4x4(RotationOnly(previous.View)) * new DoubleMatrix4x4(previous.Projection);
            currentToPrevious = (unprojection * viewChange).ToSingle();
            backgroundToPrevious = (inverseRotation * rotationThen).ToSingle();
            return true;
        }

        /// <summary>The linear map from <c>(ndc.x * w, ndc.y * w, linear depth, 1)</c> to view space for a System.Numerics
        /// projection without skew, whose clip w is the linear depth under perspective and 1 under orthographic:
        /// <c>x = (u.x + u.z * M31 - M41) / M11</c>, <c>y = (u.y + u.z * M32 - M42) / M22</c>, <c>z = -u.z</c>.</summary>
        static bool TryUnprojection(in Matrix4x4 projection, out DoubleMatrix4x4 unprojection)
        {
            double sx = projection.M11, sy = projection.M22;
            if (!(Math.Abs(sx) > 0.0) || !(Math.Abs(sy) > 0.0) || !double.IsFinite(sx) || !double.IsFinite(sy))
            {
                unprojection = default;
                return false;
            }
            unprojection = new DoubleMatrix4x4(
                1.0 / sx, 0.0, 0.0, 0.0,
                0.0, 1.0 / sy, 0.0, 0.0,
                projection.M31 / sx, projection.M32 / sy, -1.0, 0.0,
                -projection.M41 / sx, -projection.M42 / sy, 0.0, 1.0);
            return true;
        }

        /// <summary>The linear depth this frame's surface point had from last frame's camera, which the resolve's
        /// disocclusion test compares with the stored previous depth. <paramref name="ndcXY"/> is the unjittered NDC of the
        /// sample the depth was read at, the dilated texel's own sample in the resolve, and <paramref name="linearDepth"/>
        /// that depth linearised with <see cref="TemporalResolveUniforms.CurrentDepth"/>.
        /// Zero or less when the point was on or behind last frame's camera plane. The shader computes exactly this,
        /// <c>-(CurrentToPrevious * vec4(ndcXY * w, linearDepth, 1.0)).z</c> with w the linear depth under perspective and
        /// 1 under orthographic, in float.</summary>
        public static float ExpectedPreviousDepth(in TemporalResolveUniforms uniforms, Vector2 ndcXY, float linearDepth)
        {
            float w = uniforms.CurrentDepth.X > 0.5f ? linearDepth : 1f;
            return -Vector4.Transform(new Vector4(ndcXY * w, linearDepth, 1f), uniforms.CurrentToPrevious).Z;
        }

        /// <summary>The UV this frame's surface point had last frame if it did not move: the view-space point
        /// <see cref="ExpectedPreviousDepth"/> reads its depth from, through
        /// <see cref="TemporalResolveUniforms.PreviousProjection"/>, divided by w, then NDC to UV with V running down the
        /// image as <see cref="MotionMath.UvMotion"/> writes it. So this frame's UV minus this is the motion the camera
        /// alone gives the point. The resolve takes the point at the dilated texel's own sample, which is the point the
        /// motion target wrote that texel's motion for, and compares the two to find a moving surface.
        /// Null when the previous clip w is at or below <see cref="MotionMath.MinPreviousClipW"/> or not a number, where
        /// the resolve treats the surface as moving. The shader computes exactly this, in float.</summary>
        public static Vector2? StaticPreviousUv(in TemporalResolveUniforms uniforms, Vector2 ndcXY, float linearDepth)
        {
            float w = uniforms.CurrentDepth.X > 0.5f ? linearDepth : 1f;
            Vector4 view = Vector4.Transform(new Vector4(ndcXY * w, linearDepth, 1f), uniforms.CurrentToPrevious);
            Vector4 clip = Vector4.Transform(view, uniforms.PreviousProjection);
            if (!(clip.W > MotionMath.MinPreviousClipW)) return null;
            return new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f);
        }

        /// <summary>The resolve's uniforms for one frame. <paramref name="previous"/> is the previous frame view rebased to
        /// this frame's origin, or null when there is none. Both views are unjittered, as the motion target's clip
        /// positions are, so the static previous UV and the motion agree. History is readable only when it is valid and a
        /// previous view exists and both reprojections invert. The previous projection and depth parameters are the
        /// current ones when it is not. <paramref name="phaseCount"/> is the jitter cycle the caller
        /// already runs, <see cref="TemporalJitter.PhaseCount"/> of the unrounded display over internal scale with the
        /// render cap included, <c>1 / (EffectiveUpscaleRatio * capScale)</c>, where <c>capScale</c> is the
        /// <see cref="KhaozEngine.Primitives.ViewportMath.Fit"/> scale <see cref="Scene3D.ComputeTargetSize"/> applies
        /// before rounding to whole pixels (1 when the cap does not bite). Never
        /// <see cref="DisplayOverInternal"/>, which reads the rounded sizes and can overshoot by a phase, and never the
        /// bare preset ratio, which under-covers when the cap bites. Native on a 5120x2880 display renders 3840x2160, a
        /// scale of 1.333, and needs 15 phases, not 8.</summary>
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
            Matrix4x4 previousProjection = readable && previous is { } shown ? shown.Projection : current.Projection;
            float displayOverInternal = DisplayOverInternal(dw, dh, iw, ih);
            return new TemporalResolveUniforms
            {
                CurrentToPrevious = currentToPrevious,
                BackgroundToPrevious = backgroundToPrevious,
                PreviousProjection = previousProjection,
                Sizes = new Vector4(iw, ih, dw, dh),
                Jitter = new Vector4(jitterPixels.X, jitterPixels.Y, displayOverInternal, readable ? 1f : 0f),
                CurrentDepth = currentDepth,
                PreviousDepth = DepthParams(previousProjection),
                Params = new Vector4(1f / Math.Max(TemporalJitter.NativePhaseCount, phaseCount), 0f, 0f, 0f),
            };
        }

        /// <summary>The larger per-axis ratio of a display size to an internal size, at least 1, with every size below 1
        /// read as 1. A size ratio for sampling only, the resolve's reconstruction footprint (Jitter.z). It must never
        /// feed <see cref="TemporalJitter.PhaseCount"/>. The internal size already carries the render cap, the fit to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/> by <see cref="PixelPostProcessSettings.MaxRenderHeight"/>,
        /// and is rounded to whole pixels, so this can land just above the unrounded scale, and <c>ceil(8 * r * r)</c>
        /// then takes one phase more, 19 instead of 18 for a 3456x2234 display on Quality. The jitter cycle reads the
        /// unrounded scale described on <see cref="BuildUniforms"/>.</summary>
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
