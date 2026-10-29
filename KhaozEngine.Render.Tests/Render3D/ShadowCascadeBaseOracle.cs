using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

// Frozen from 0af218d5b. Keep the arithmetic verbatim, independent of production helpers.
// Only Scene3D resource plumbing is supplied here for the origin-zero fixture.
internal sealed class ShadowCascadeBaseOracle
{
    readonly IIsoCamera3D ActiveCamera;
    readonly PixelPostProcessSettings Post;
    readonly Model _model;
    readonly View _currentFrameView;
    readonly Vector3[] _frustumCornersScratch = new Vector3[8];
    readonly Matrix4x4[] _cascadeCpuVps = new Matrix4x4[4];
    readonly Matrix4x4[] _cascadeCpuVpsAbsolute = new Matrix4x4[4];
    readonly float[] _cascadeRadii = new float[4];
    Vector3 _heldLightDir;
    bool _hasHeldLightDir;
    int _cascadeCount;
    static bool _frameOriginActive => false;
    static Vector3 ToRender(Vector3 value) => value;

    internal ShadowCascadeBaseOracle(IIsoCamera3D camera, PixelPostProcessSettings post)
    {
        ActiveCamera = camera;
        Post = post;
        _model = new Model(new ShadowMap(2048, 4));
        _currentFrameView = new View(((IRenderOriginAware)camera).AbsoluteViewProjection);
    }

    internal ReadOnlySpan<Matrix4x4> Matrices => _cascadeCpuVps;
    internal ReadOnlySpan<Matrix4x4> AbsoluteMatrices => _cascadeCpuVpsAbsolute;
    internal Vector3 Held => _heldLightDir;
    internal void Frame() => ComputeShadowCascades();
    internal void Splits(Span<float> splits) => ShadowMapMath.FillCascadeSplits(splits, 4,
        Post.Quality.Shadows.ShadowNearDistance, Post.Quality.Shadows.ResolvedMaxDistance);

    sealed record Model(ShadowMap ShadowMap);
    sealed record ShadowMap(int Resolution, int CascadeCount);
    sealed record View(Matrix4x4 AbsoluteViewProjection);

    int ComputeShadowCascades()
    {
        var shadows = Post.Quality.Shadows;
        // ABSOLUTE: the fit, the radii and the caster classification stay byte-identical at any render origin.
        if (!ShadowMapMath.FrustumCornersWorld(_currentFrameView.AbsoluteViewProjection, _frustumCornersScratch))
        {
            _cascadeCount = 0;
            return 0;
        }
        Vector3 eye = ActiveCamera.Eye;
        Vector3 fwd = ActiveCamera.Forward;
        // View depths of the near/far planes read off the unprojected corners (camera-type-agnostic: no
        // reliance on perspective-projection matrix fields, so iso/ortho cameras fit identically).
        Vector3 nearC = (_frustumCornersScratch[0] + _frustumCornersScratch[1] + _frustumCornersScratch[2] + _frustumCornersScratch[3]) * 0.25f;
        Vector3 farC = (_frustumCornersScratch[4] + _frustumCornersScratch[5] + _frustumCornersScratch[6] + _frustumCornersScratch[7]) * 0.25f;
        float camNear = Vector3.Dot(nearC - eye, fwd);
        float camFar = Vector3.Dot(farC - eye, fwd);
        float range = MathF.Max(camFar - camNear, 1e-3f);

        int res = _model.ShadowMap.Resolution;         // the actual allocated per-cascade resolution (clamped)
        int count = _model.ShadowMap.CascadeCount;     // the actual allocated cascade count (clamped)
        Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
        ShadowMapMath.FillCascadeSplits(splits, count, shadows.ShadowNearDistance, shadows.ResolvedMaxDistance);
        Vector3 lightDir = HeldLightDirection(splits, count, camNear, camFar, range, res);
        float prev = camNear;
        for (int i = 0; i < count; i++)
        {
            float d = Math.Clamp(splits[i], camNear, camFar);
            ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                (prev - camNear) / range, (d - camNear) / range, out Vector3 center, out float radius);
            FitCascade(i, lightDir, center, radius, res);
            _cascadeRadii[i] = radius;
            prev = MathF.Max(d, prev);
        }
        _cascadeCount = count;
        return count;
    }

    Vector3 HeldLightDirection(ReadOnlySpan<float> splits, int count, float camNear, float camFar, float range, int res)
    {
        Vector3 live = Vector3.Normalize(Post.LightDirection);
        float budget = Post.Quality.Shadows.ShadowLightHoldTexels;
        // Disabled, or nothing held yet (the first shadow frame, which must fit from the live sun anyway). The
        // early-out also keeps the disabled path off the radius walk below, so 0 costs nothing as well as
        // changing nothing.
        if (!(budget > 0f) || !_hasHeldLightDir)
        {
            _heldLightDir = live;
            _hasHeldLightDir = true;
            return live;
        }
        if (ShadowLightHold.ShouldAdopt(_heldLightDir, live,
                MinCascadeRadius(splits, count, camNear, camFar, range), res,
                Post.Quality.Shadows.ShadowLightHoldCasterHeight, budget))
            _heldLightDir = live;
        return _heldLightDir;
    }

    float MinCascadeRadius(ReadOnlySpan<float> splits, int count, float camNear, float camFar, float range)
    {
        float min = float.MaxValue;
        float prev = camNear;
        for (int i = 0; i < count && i < splits.Length; i++)
        {
            float d = Math.Clamp(splits[i], camNear, camFar);
            ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                (prev - camNear) / range, (d - camNear) / range, out _, out float radius);
            min = MathF.Min(min, radius);
            prev = MathF.Max(d, prev);
        }
        return min == float.MaxValue ? 0f : min;
    }

    void FitCascade(int i, Vector3 lightDir, Vector3 center, float radius, int resolution)
    {
        _cascadeCpuVpsAbsolute[i] = ShadowMapMath.BuildLightViewProj(lightDir, center, radius, resolution);
        _cascadeCpuVps[i] = _frameOriginActive
            ? ShadowMapMath.BuildLightViewProj(lightDir, ToRender(center), radius, resolution)
            : _cascadeCpuVpsAbsolute[i];
    }

    internal static class ShadowMapMath
    {
        static readonly Vector3 FallbackLightDir = new(0f, -1f, 0f);

        public const float DefaultSplitLambda = 0.6f;

        public static void FillCascadeSplits(Span<float> splits, int count, float nearDistance, float maxDistance, float lambda = DefaultSplitLambda)
        {
            int n = Math.Clamp(count, 1, splits.Length);
            float near = MathF.Max(nearDistance, 1e-3f);
            float far = MathF.Max(maxDistance, near);   // never fit the outer cascade tighter than the near one
            float lam = Math.Clamp(lambda, 0f, 1f);
            if (n == 1) { splits[0] = near; return; }
            for (int i = 0; i < n; i++)
            {
                float f = (float)i / (n - 1);                       // 0 at cascade 0, 1 at the outer cascade
                float lin = near + (far - near) * f;                // linear (uniform) progression
                float log = near * MathF.Pow(far / near, f);        // logarithmic (geometric) progression
                splits[i] = lam * log + (1f - lam) * lin;           // exact at the ends (f==0 -> near, f==1 -> far)
            }
        }

        public static Matrix4x4 AtlasColumnTransform(int index, int count)
        {
            int n = Math.Max(1, count);
            int i = Math.Clamp(index, 0, n - 1);
            float sx = 1f / n;
            float bx = -1f + (2f * i + 1f) / n;   // maps clip.x [-1,1] -> column i NDC range
            // Row-vector clip' = clip * C:  clip'.x = sx*clip.x + bx*clip.w, and y,z,w are unchanged.
            var c = Matrix4x4.Identity;
            c.M11 = sx;
            c.M41 = bx;
            return c;
        }

        public static int SelectCascade(ReadOnlySpan<Matrix4x4> receiverMats, int count, Vector3 worldPos, float uvMargin = 0f)
        {
            int n = Math.Min(count, receiverMats.Length);
            for (int i = 0; i < n; i++)
            {
                Vector4 lc = Vector4.Transform(new Vector4(worldPos, 1f), receiverMats[i]);
                if (lc.W <= 0f) continue;
                float x = lc.X / lc.W, y = lc.Y / lc.W, z = lc.Z / lc.W;
                float u = x * 0.5f + 0.5f, v = y * 0.5f + 0.5f;
                if (u < uvMargin || u > 1f - uvMargin || v < uvMargin || v > 1f - uvMargin || z < 0f || z > 1f) continue;
                return i;
            }
            return -1;
        }

        public static float PancakeDepth(float lightClipDepth) => MathF.Max(lightClipDepth, 0f);

        public static float TexelWorldSize(float radius, int resolution)
        {
            float r = MathF.Max(radius, 1e-3f);
            int res = Math.Max(resolution, 1);
            return (2f * r) / res;
        }

        public static bool FrustumCornersWorld(in Matrix4x4 viewProj, Span<Vector3> corners)
        {
            if (corners.Length < 8 || !Matrix4x4.Invert(viewProj, out Matrix4x4 inv)) return false;
            int k = 0;
            for (int z = 0; z <= 1; z++)
                for (int y = -1; y <= 1; y += 2)
                    for (int x = -1; x <= 1; x += 2)
                    {
                        Vector4 p = Vector4.Transform(new Vector4(x, y, z, 1f), inv);
                        if (MathF.Abs(p.W) < 1e-9f) return false;
                        corners[k++] = new Vector3(p.X, p.Y, p.Z) / p.W;
                    }
            return true;
        }

        public static void SliceBoundingSphere(ReadOnlySpan<Vector3> corners, float tNear, float tFar,
            out Vector3 center, out float radius)
        {
            Span<Vector3> s = stackalloc Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                Vector3 edge = corners[i + 4] - corners[i];
                s[i] = corners[i] + edge * tNear;
                s[i + 4] = corners[i] + edge * tFar;
            }
            Vector3 a = (s[0] + s[1] + s[2] + s[3]) * 0.25f;
            Vector3 b = (s[4] + s[5] + s[6] + s[7]) * 0.25f;
            float rn2 = 0f, rf2 = 0f;
            for (int i = 0; i < 4; i++)
            {
                rn2 = MathF.Max(rn2, (s[i] - a).LengthSquared());
                rf2 = MathF.Max(rf2, (s[i + 4] - b).LengthSquared());
            }
            Vector3 ab = b - a;
            float len2 = ab.LengthSquared();
            // Balance point on the axis where the worst near-corner and far-corner distances agree:
            // |c-a|^2 + rn^2 == |b-c|^2 + rf^2 solved for c = a + u*ab, clamped into the slice.
            float u = len2 > 1e-12f ? Math.Clamp((len2 + rf2 - rn2) / (2f * len2), 0f, 1f) : 0.5f;
            center = a + ab * u;
            float r2 = 0f;
            for (int i = 0; i < 8; i++) r2 = MathF.Max(r2, (s[i] - center).LengthSquared());
            radius = MathF.Sqrt(r2);
        }

        public static Matrix4x4 BuildLightViewProj(Vector3 lightDir, Vector3 focus, float radius, int resolution)
        {
            float r = MathF.Max(radius, 1e-3f);
            int res = Math.Max(resolution, 1);

            Vector3 dir = lightDir.LengthSquared() > 1e-8f ? Vector3.Normalize(lightDir) : FallbackLightDir;

            // Pick an up vector not parallel to the light axis (world-up unless the light is near-vertical).
            Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;

            // The light VIEW rotation depends only on the light direction + up (both constant across a camera pan), so
            // the light-space X/Y axes are fixed. Snap the FOCUS to the texel grid IN light-view space (not clip), so
            // the frustum origin steps in whole texels as the camera slides: the projected position of any fixed world
            // point then only ever moves by integer texels, killing the sub-texel shadow-edge swim. Build the view
            // around the ORIGIN first to read the fixed rotation, snap there, then place the eye.
            Vector3 axisEye = -dir; // a unit reference eye so CreateLookAt yields the pure light-view rotation basis
            Matrix4x4 rotView = Matrix4x4.CreateLookAt(axisEye, Vector3.Zero, up);

            // Focus in light-view space, quantized to texel-sized increments on X and Y (the map plane).
            Vector3 focusView = Vector3.Transform(focus, rotView);
            float texel = TexelWorldSize(r, res);
            focusView.X = MathF.Round(focusView.X / texel) * texel;
            focusView.Y = MathF.Round(focusView.Y / texel) * texel;
            // Back to world: the snapped focus the frustum centres on.
            Matrix4x4.Invert(rotView, out Matrix4x4 rotViewInv);
            Vector3 snappedFocus = Vector3.Transform(focusView, rotViewInv);

            // Depth slack along the light axis: the light "eye" sits a full diameter up-light of the focus.
            //
            // This is a TEXEL-DENSITY choice, not a correctness one, and the old comment here claimed otherwise (it
            // read "so a tall caster ... still writes depth and nothing clips the near plane", which assumed a
            // caster's up-light offset equalled its height - it is h / sin(elevation), so at a grazing sun a 12 m
            // tree sits 31 m up-light of the ground it shades and no fixed slack can bound it). The near plane no
            // longer loses casters because the DEPTH PASS CLAMPS instead of clipping: a caster in front of the near
            // plane records at it with its silhouette intact (see PancakeDepth and ShaderSources.ShadowDepthVert).
            // What 2r still buys is the depth RANGE the R32F atlas quantizes over, hence the density of the stored
            // depth: widening it costs precision, narrowing it costs nothing in coverage now but is a separate
            // retune. Changing it must not be justified as a fix for lost casters.
            float depthExtent = 2f * r;
            Vector3 eye = snappedFocus - dir * depthExtent;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, snappedFocus, up);
            // Ortho covering [-r, r] in X/Y. Near 0 at the eye, far spanning eye->focus->far side of the sphere.
            Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * r, 2f * r, 0f, depthExtent + 2f * r);
            return view * proj;
        }
    }

    internal static class ShadowLightHold
    {
        public const float MinResolvableRadians = 1e-5f;

        public static float SinElevation(Vector3 lightDir) => MathF.Abs(lightDir.Y);

        public static float ThresholdRadians(float minCascadeRadius, int resolution, float sinElevation,
            float maxCasterHeight, float texelBudget)
        {
            if (!(texelBudget > 0f) || !(maxCasterHeight > 0f) || !(minCascadeRadius > 0f) || resolution <= 0) return 0f;
            if (!(sinElevation > 0f)) return 0f;
            float texelWorld = ShadowMapMath.TexelWorldSize(minCascadeRadius, resolution);
            float threshold = texelBudget * texelWorld * sinElevation * sinElevation / maxCasterHeight;
            return float.IsFinite(threshold) && threshold > 0f ? threshold : 0f;
        }

        public static float AngleBetween(Vector3 a, Vector3 b)
        {
            float chord = (a - b).Length();
            return 2f * MathF.Asin(MathF.Min(1f, chord * 0.5f));
        }

        public static bool ShouldAdopt(Vector3 held, Vector3 live, float minCascadeRadius, int resolution,
            float maxCasterHeight, float texelBudget)
        {
            if (!float.IsFinite(live.X) || !float.IsFinite(live.Y) || !float.IsFinite(live.Z)) return true;
            float sinE = MathF.Min(SinElevation(held), SinElevation(live));
            float threshold = ThresholdRadians(minCascadeRadius, resolution, sinE, maxCasterHeight, texelBudget);
            if (threshold < MinResolvableRadians) return true;
            return AngleBetween(held, live) >= threshold;
        }
    }
}
