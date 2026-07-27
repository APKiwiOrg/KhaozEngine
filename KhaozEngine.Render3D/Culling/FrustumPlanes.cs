using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The six clip-space planes of a view-projection frustum, in WORLD space, for conservative camera-frustum
    /// culling (drop only geometry provably outside the view). Pure <see cref="System.Numerics"/> math, no GPU,
    /// so it is headless-testable and allocation-free (a stack struct: <see cref="Extract"/> fills six
    /// <see cref="Vector4"/> planes in place, the tests are pure arithmetic).
    /// </summary>
    /// <remarks>
    /// <para><b>Convention.</b> The engine transforms a world point to clip space with the row-vector product
    /// <c>clip = Vector4.Transform(new Vector4(world, 1), viewProjection)</c> (see <c>CameraProjection</c> /
    /// <c>IsoCamera3D</c>), i.e. <c>clip = worldRow * VP</c>. In <see cref="Matrix4x4"/> (where <c>Mij</c> is row
    /// <c>i</c>, column <c>j</c>) that makes <c>clip.X = world . column0</c>, <c>clip.Y = world . column1</c>,
    /// <c>clip.Z = world . column2</c>, <c>clip.W = world . column3</c>. The clip-space depth range is [0, 1]
    /// (near maps to z = 0, far to z = 1) on every supported backend, matching <c>CameraProjection.WorldToScreen</c>
    /// and <c>GpuClip</c>'s note that depth is never remapped. So the six half-space tests are:
    /// left <c>clip.X &gt;= -clip.W</c>, right <c>clip.X &lt;= clip.W</c>, bottom <c>clip.Y &gt;= -clip.W</c>,
    /// top <c>clip.Y &lt;= clip.W</c>, near <c>clip.Z &gt;= 0</c>, far <c>clip.Z &lt;= clip.W</c>. Each becomes a
    /// plane by adding/subtracting the relevant column from column3 (the Gribb/Hartmann method adapted to the
    /// row-vector, zero-to-one-depth convention this engine actually uses).</para>
    /// <para><b>Use an ABSOLUTE view-projection</b>, never <c>camera.ViewProjection</c> while a render origin is in
    /// force. <c>Scene3D</c> latches an origin at <c>Begin</c>, and once it has, <c>camera.ViewProjection</c>
    /// returns a RENDER-RELATIVE matrix, so extracting planes from it culls absolute bounds against the wrong
    /// space entirely. Pass <c>Scene3D</c>'s internal <c>FrameAbsoluteViewProjection()</c> (or any other absolute
    /// view-projection) instead. This is unrelated to the <c>GpuClip.Correct</c>-adjusted matrix, which stays
    /// wrong for culling either way: the clip-space Y flip only reorients GPU rasterization, so the world-space
    /// frustum is the same regardless (exactly as picking/world-to-screen math stays authored).</para>
    /// </remarks>
    public struct FrustumPlanes
    {
        // Six planes as (Normal.xyz, D) with the half-space "inside" defined by Normal . p + D >= 0. Not normalized:
        // the AABB/sphere sign tests only need the sign of the dot, and normalizing the sphere test would cost a
        // sqrt per plane. (Normalize() is offered for when a true signed distance is wanted.)
        Vector4 _p0, _p1, _p2, _p3, _p4, _p5;

        /// <summary>Left, right, bottom, top, near, far in that order (index 0..5).</summary>
        public readonly Vector4 this[int i] => i switch
        {
            0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3, 4 => _p4, 5 => _p5,
            _ => throw new ArgumentOutOfRangeException(nameof(i)),
        };

        /// <summary>
        /// Extract the six world-space frustum planes from a row-vector view-projection with [0, 1] clip depth
        /// (the engine's convention: see the type remarks). Pass an ABSOLUTE view-projection, e.g. <c>Scene3D</c>'s
        /// internal <c>FrameAbsoluteViewProjection()</c>: <c>camera.ViewProjection</c> is render-relative once a
        /// render origin has latched.
        /// </summary>
        public static FrustumPlanes Extract(Matrix4x4 vp)
        {
            // Columns of vp. clip.X = world . col0, etc. (row-vector product).
            var col0 = new Vector4(vp.M11, vp.M21, vp.M31, vp.M41);
            var col1 = new Vector4(vp.M12, vp.M22, vp.M32, vp.M42);
            var col2 = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
            var col3 = new Vector4(vp.M14, vp.M24, vp.M34, vp.M44);

            FrustumPlanes fp = default;
            fp._p0 = col3 + col0;   // left:   clip.X >= -clip.W
            fp._p1 = col3 - col0;   // right:  clip.X <=  clip.W
            fp._p2 = col3 + col1;   // bottom: clip.Y >= -clip.W
            fp._p3 = col3 - col1;   // top:    clip.Y <=  clip.W
            fp._p4 = col2;          // near:   clip.Z >= 0  (zero-to-one depth)
            fp._p5 = col3 - col2;   // far:    clip.Z <=  clip.W
            return fp;
        }

        /// <summary>
        /// Conservative AABB visibility (positive-vertex / p-vertex method): the box is OUTSIDE only when it lies
        /// fully behind one plane. Returns <c>false</c> only for a box provably outside, so culling it is safe; a
        /// box that straddles a plane (or sits in the "corner" region several planes agree is outside but no single
        /// plane fully rejects) conservatively returns <c>true</c> and is drawn.
        /// </summary>
        public readonly bool IntersectsAabb(Vector3 min, Vector3 max)
        {
            // For each plane, pick the box corner farthest along the plane normal (the p-vertex). If even that
            // corner is behind the plane, every corner is, so the whole box is outside.
            for (int i = 0; i < 6; i++)
            {
                Vector4 pl = this[i];
                float px = pl.X >= 0f ? max.X : min.X;
                float py = pl.Y >= 0f ? max.Y : min.Y;
                float pz = pl.Z >= 0f ? max.Z : min.Z;
                if (pl.X * px + pl.Y * py + pl.Z * pz + pl.W < 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// Conservative sphere visibility: the sphere is OUTSIDE only when its centre is farther than
        /// <paramref name="radius"/> behind some plane. Uses the plane normal length so an un-normalized plane
        /// still gives the correct signed test (compares against <c>radius * |normal|</c>). Returns <c>false</c>
        /// only when provably outside.
        /// </summary>
        public readonly bool IntersectsSphere(Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector4 pl = this[i];
                float dist = pl.X * center.X + pl.Y * center.Y + pl.Z * center.Z + pl.W;
                if (dist < 0f)
                {
                    // dist is a scaled signed distance (scaled by |normal|). Behind by more than the radius (in the
                    // same scale) => fully outside this plane. Compare squared to avoid a sqrt when it is inside.
                    float nLen2 = pl.X * pl.X + pl.Y * pl.Y + pl.Z * pl.Z;
                    if (dist * dist > radius * radius * nLen2) return false;
                }
            }
            return true;
        }

        /// <summary>Return a copy with every plane normalized (unit normal), so <c>Normal . p + D</c> is a true
        /// signed world-space distance. Not needed for the visibility tests above (they only use the sign);
        /// offered for callers that want real distances.</summary>
        public readonly FrustumPlanes Normalized()
        {
            FrustumPlanes n = this;
            n._p0 = NormalizePlane(_p0);
            n._p1 = NormalizePlane(_p1);
            n._p2 = NormalizePlane(_p2);
            n._p3 = NormalizePlane(_p3);
            n._p4 = NormalizePlane(_p4);
            n._p5 = NormalizePlane(_p5);
            return n;
        }

        static Vector4 NormalizePlane(Vector4 p)
        {
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            return len > 1e-20f ? p / len : p;
        }
    }
}
