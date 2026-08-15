using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The light-movement epsilon for the shadow cascade fit (issue #410, design section 3.3): the cascade fit keeps
    /// using the light direction it last fitted with until the sun has turned far enough to move the recorded shadow
    /// by more than <see cref="ShadowSettings.ShadowLightHoldTexels"/> texels, and only then adopts the new one.
    /// A stationary scene under a slowly moving sun then stops re-recording the whole atlas every frame for a shadow
    /// displacement no viewer can see. The arithmetic is <see cref="Internal.ShadowLightHold"/>, which is pure and
    /// headless-tested. What lives here is the held direction itself and the two coupling hazards below, which are
    /// the reason this is built as "do not re-fit" rather than the tempting "fit and skip".
    /// </summary>
    public sealed partial class Scene3D
    {
        // The light direction the cascade fit last ADOPTED, and whether one has been adopted at all. This is the
        // whole of the hold's state, deliberately: see hazard 1 in HeldLightDirection for what must NOT be kept here.
        Vector3 _heldLightDir;
        bool _hasHeldLightDir;

        /// <summary>
        /// The light direction <see cref="ComputeShadowCascades"/> fits this frame's cascades from: the held one
        /// while the sun's total rotation away from it stays under this frame's threshold, otherwise the live
        /// <c>Post.LightDirection</c>, which is adopted and becomes the new held direction. With
        /// <see cref="ShadowSettings.ShadowLightHoldTexels"/> at 0 this returns the live direction on every frame and
        /// the fit is byte-for-byte what it was before the epsilon existed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Hazard 1: what is frozen is the light direction INPUT, never the fitted output matrix.</b> The fit is a
        /// function of the camera as well as the light. <see cref="ComputeShadowCascades"/> re-derives every
        /// cascade's bounding sphere from THIS frame's frustum corners every frame, and it keeps doing so here: only
        /// the direction handed to <c>FitCascade</c> is held. Freeze the fitted matrix instead and a camera that
        /// moves leaves the cascade sphere sitting where the camera used to be, so the frustum walks out of the atlas
        /// and shadows stretch or vanish at the far edge, which is a far worse artifact than the one being avoided.
        /// Held direction plus current camera means a camera that moves past the existing texel snap simply changes
        /// the fitted matrix, trips <c>LightMatrixChanged</c> and re-records, which is correct: the cascade moved, so
        /// the atlas has to. The saving is the stationary-camera case.
        /// </para>
        /// <para>
        /// <b>Hazard 2: the receiver is rebuilt from the current fit every frame, including skipped frames.</b>
        /// <see cref="SetShadowReceiverTail"/> runs unconditionally and derives the receiver matrices, the atlas
        /// column transforms and the per-cascade normal offsets from <c>_cascadeCpuVps</c> as it stands. So a
        /// loosened dirty COMPARE (fit normally, then decline to record) would leave the receiver sampling an atlas
        /// recorded with the old matrix through the new one, shifting every shadow by the un-recorded delta, which is
        /// exactly the acne and edge swim the texel snap exists to prevent. Holding the fit's INPUT keeps the fit,
        /// the atlas, the receiver tail and the per-cascade cull (<c>ShadowCascadeCull.FromLightViewProj</c> reads
        /// the same fit) in agreement by construction, and the existing exact matrix compare then does the rest: a
        /// fit re-derived from a held direction and an unmoved camera compares equal, so <c>LightMatrixChanged</c>
        /// goes false and the pass skips on its own.
        /// </para>
        /// <para>
        /// The hold covers the SHADOW fit alone. <c>Post.LightDirection</c> still drives the diffuse and specular
        /// terms, the sky sun disc and the water lighting live, so only the cast shadow lags, and it lags by at most
        /// the displacement the threshold bounds.
        /// </para>
        /// </remarks>
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
            if (Internal.ShadowLightHold.ShouldAdopt(_heldLightDir, live,
                    MinCascadeRadius(splits, count, camNear, camFar, range), res,
                    Post.Quality.Shadows.ShadowLightHoldCasterHeight, budget))
                _heldLightDir = live;
            return _heldLightDir;
        }

        /// <summary>
        /// The smallest fitted slice-sphere radius over this frame's active cascades, which is the tightest cascade's
        /// texel quantum and therefore the one the whole-atlas hold has to satisfy. Walks the same slices
        /// <see cref="ComputeShadowCascades"/> is about to fit, from the same frustum corners and the same splits, so
        /// the threshold is sized against THIS frame's fit rather than the previous frame's kept radii.
        /// <para>
        /// It is a second walk of at most four <see cref="Internal.ShadowMapMath.SliceBoundingSphere"/> calls (eight
        /// corner lerps each) and it runs only when the hold is enabled. That is far below the measured cost of the
        /// caster-signature walk the dirty check already pays on every frame, and it buys a threshold with no
        /// one-frame lag in it.
        /// </para>
        /// </summary>
        float MinCascadeRadius(ReadOnlySpan<float> splits, int count, float camNear, float camFar, float range)
        {
            float min = float.MaxValue;
            float prev = camNear;
            for (int i = 0; i < count && i < splits.Length; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                Internal.ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                    (prev - camNear) / range, (d - camNear) / range, out _, out float radius);
                min = MathF.Min(min, radius);
                prev = MathF.Max(d, prev);
            }
            return min == float.MaxValue ? 0f : min;
        }
    }
}
