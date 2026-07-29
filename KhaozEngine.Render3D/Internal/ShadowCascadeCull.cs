using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The per-cascade caster test for the shadow depth pass: does this caster's world bounding sphere reach any
    /// part of ONE cascade's light-space footprint. Built once per cascade per frame from that cascade's
    /// ORTHOGRAPHIC world-to-light-clip matrix (<see cref="ShadowMapMath.BuildLightViewProj"/>), then asked once per
    /// caster instance. Pure math, no GPU, no engine state, so the whole contract below is headless-testable.
    /// <para>
    /// <b>Why light-space XY culling is EXACT for shadows, not conservative.</b> Under a directional light every
    /// light ray is parallel to the light-view Z axis, so a caster and every point it shadows share the same
    /// light-space XY. A cascade's map covers exactly the light-space XY rect that clips to [-1,1]x[-1,1], and a
    /// receiver only ever samples inside that rect. A caster whose XY extent misses the rect therefore rasterizes
    /// nothing into that cascade's column (the rasterizer clips it) AND can shadow nothing sampled from it. Dropping
    /// it leaves the cascade's depth texels bit-identical. That is a stronger statement than ordinary frustum
    /// culling, which only claims "not visible".
    /// </para>
    /// <para>
    /// <b>The near plane is NEVER a cull plane.</b> Since 17.13.0 the depth pass PANCAKES: a caster in front of the
    /// light's near plane records AT the near plane instead of clipping away, so its silhouette still shadows
    /// everything below it (see <see cref="ShadowMapMath.PancakeDepth"/> and the grazing-sun case in issue #394 -
    /// at a low sun a 12 m tree sits 31 m up-light of the ground it shades, far outside any fixed near slack).
    /// Culling on the near plane would delete exactly those casters and re-open that defect, so
    /// <see cref="Intersects"/> tests light-space XY and the FAR plane only. The far plane is safe because the
    /// rasterizer already clips depth past 1 identically.
    /// </para>
    /// <para>
    /// <b>The margin.</b> The test is already conservative (a mesh's bounding sphere is the AABB half-diagonal), but
    /// the CPU matrix math and the GPU rasterizer are not bit-for-bit the same arithmetic, so the box is widened by
    /// <see cref="MarginTexels"/> shadow texels before rejecting. At 2048 that is 0.4 percent of a cascade's extent,
    /// which costs nothing measurable in culling and removes any question of a boundary-straddling caster being
    /// dropped by rounding.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Row-vector convention, matching <see cref="FrustumPlanes"/> and the rest of the engine:
    /// <c>clip = Vector4.Transform(new Vector4(world, 1), m)</c>, so <c>clip.X = world . column0 + m.M41</c>. The
    /// gradient of <c>clip.X</c> with respect to world position IS column0, so a world sphere of radius <c>r</c>
    /// spans <c>r * |column0|</c> in clip X. That is what turns a world-space radius into a clip-space one exactly,
    /// with no assumption about how the fit chose its extents. The light matrix is ORTHOGRAPHIC, so
    /// <c>clip.W == 1</c> everywhere and the affine test below is the whole story (pinned by a headless test).
    /// </remarks>
    internal readonly struct ShadowCascadeCull
    {
        /// <summary>Shadow texels of slack added to each side of the light-clip box before a caster is rejected.
        /// Absorbs CPU-vs-GPU float differences at the boundary. Four texels is well past any plausible
        /// disagreement and still under half a percent of a 2048 cascade.</summary>
        public const int MarginTexels = 4;

        readonly Vector3 _gradX, _gradY, _gradZ;   // d(clip.xyz)/d(world), i.e. columns 0..2 of the light matrix
        readonly float _offX, _offY, _offZ;        // the translation row (M41, M42, M43)
        readonly float _scaleX, _scaleY, _scaleZ;  // |grad*|: world radius -> clip radius on that axis
        readonly float _margin;                    // clip-space slack, both XY axes and the far plane

        ShadowCascadeCull(in Matrix4x4 m, float margin)
        {
            _gradX = new Vector3(m.M11, m.M21, m.M31);
            _gradY = new Vector3(m.M12, m.M22, m.M32);
            _gradZ = new Vector3(m.M13, m.M23, m.M33);
            _offX = m.M41; _offY = m.M42; _offZ = m.M43;
            _scaleX = _gradX.Length();
            _scaleY = _gradY.Length();
            _scaleZ = _gradZ.Length();
            _margin = margin;
        }

        /// <summary>Clip-space slack worth <see cref="MarginTexels"/> texels at <paramref name="resolution"/>. The
        /// clip box spans 2 units across <paramref name="resolution"/> texels, so one texel is <c>2/resolution</c>
        /// of clip space.</summary>
        public static float ClipMargin(int resolution) => (2f * MarginTexels) / Math.Max(1, resolution);

        /// <summary>Build the test for ONE cascade from its world-to-light-clip matrix (the ABSOLUTE-space fit, to
        /// match the absolute instance transforms the caster test reads) at the atlas's per-cascade
        /// <paramref name="resolution"/>.</summary>
        public static ShadowCascadeCull FromLightViewProj(in Matrix4x4 lightViewProj, int resolution)
            => new(lightViewProj, ClipMargin(resolution));

        /// <summary>
        /// Does a caster bounded by the world sphere (<paramref name="center"/>, <paramref name="radius"/>) reach
        /// this cascade. <c>false</c> ONLY when the whole sphere is provably outside the cascade's light-space XY
        /// rect, or wholly beyond its far plane, both widened by the margin. Never rejects on the near plane: see
        /// the pancaking contract in the type summary. A negative radius is treated as zero.
        /// <para>
        /// Force-inlined: this runs once per caster per cascade on every dirty shadow frame (tens of thousands of
        /// calls), and left as a call it costs more than the work inside it.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Intersects(Vector3 center, float radius)
        {
            float r = MathF.Max(radius, 0f);
            float limit = 1f + _margin;

            float x = Vector3.Dot(center, _gradX) + _offX;
            float rx = r * _scaleX;
            if (x + rx < -limit || x - rx > limit) return false;

            float y = Vector3.Dot(center, _gradY) + _offY;
            float ry = r * _scaleY;
            if (y + ry < -limit || y - ry > limit) return false;

            // FAR only. The rasterizer clips depth past 1 the same way, so this drops nothing it would have kept.
            // The matching near test (z + rz < 0) is deliberately absent and must stay absent.
            float z = Vector3.Dot(center, _gradZ) + _offZ;
            float rz = r * _scaleZ;
            return !(z - rz > limit);
        }
    }
}
