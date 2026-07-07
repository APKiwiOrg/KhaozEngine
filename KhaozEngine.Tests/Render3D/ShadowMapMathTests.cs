using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) tests for the directional shadow-map light-space fitting + texel snapping
    /// (<see cref="ShadowMapMath"/>). These pin the two properties the shimmer-free shadow map depends on:
    /// the ortho frustum CONTAINS the focus sphere (nothing the camera looks at falls outside the map), and
    /// the light-space origin is QUANTIZED to shadow-texel increments so a sub-texel camera pan does not move
    /// the sampled shadow edge (the classic swimming-shadow fix). Run under plain <c>dotnet test</c>.
    /// </summary>
    public sealed class ShadowMapMathTests
    {
        static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.5f, -0.85f, -0.35f));

        [Fact]
        public void Fit_ContainsFocusSphere_AllCardinalPointsInsideNdc()
        {
            var focus = new Vector3(3f, 1f, -2f);
            const float radius = 5f;
            Matrix4x4 lightVp = ShadowMapMath.BuildLightViewProj(LightDir, focus, radius, resolution: 2048);

            // Every point on the focus sphere must project inside the light clip box [-1,1] x [-1,1] x [0,1].
            foreach (var dir in new[]
            {
                Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
                Vector3.Normalize(Vector3.One), Vector3.Normalize(new Vector3(1, -1, 1)),
            })
            {
                Vector3 p = focus + dir * radius;
                Vector4 clip = Vector4.Transform(new Vector4(p, 1f), lightVp);
                // Ortho: w == 1, so clip == NDC.
                Assert.InRange(clip.X, -1.0001f, 1.0001f);
                Assert.InRange(clip.Y, -1.0001f, 1.0001f);
                Assert.InRange(clip.Z, -0.0001f, 1.0001f);
            }
        }

        [Fact]
        public void Fit_FocusCenter_ProjectsNearClipCentre()
        {
            var focus = new Vector3(-2f, 0.5f, 4f);
            Matrix4x4 lightVp = ShadowMapMath.BuildLightViewProj(LightDir, focus, radius: 6f, resolution: 2048);
            Vector4 clip = Vector4.Transform(new Vector4(focus, 1f), lightVp);
            // The focus centre lands within one texel of the clip-space centre (texel snapping offsets it slightly).
            float texel = 2f / 2048f;
            Assert.InRange(clip.X, -2f * texel, 2f * texel);
            Assert.InRange(clip.Y, -2f * texel, 2f * texel);
        }

        [Fact]
        public void Snap_SubTexelCameraPan_DoesNotMoveProjectedWorldPoint()
        {
            // Two focus points separated by LESS than one shadow texel in light space must project a FIXED world
            // point to the SAME shadow-map texel (the snap quantizes the origin, killing the sub-texel swim).
            const int resolution = 1024;
            const float radius = 8f;
            float worldPerTexel = (2f * radius) / resolution;

            var focusA = new Vector3(0f, 0f, 0f);
            var focusB = focusA + new Vector3(worldPerTexel * 0.3f, 0f, 0f); // < 1 texel pan

            Matrix4x4 vpA = ShadowMapMath.BuildLightViewProj(LightDir, focusA, radius, resolution);
            Matrix4x4 vpB = ShadowMapMath.BuildLightViewProj(LightDir, focusB, radius, resolution);

            // A fixed world probe point (well inside both frusta).
            var probe = new Vector3(1.2f, 0.4f, -0.7f);
            Vector4 a = Vector4.Transform(new Vector4(probe, 1f), vpA);
            Vector4 b = Vector4.Transform(new Vector4(probe, 1f), vpB);

            // Convert to texel coordinates; the snap must keep them within a fraction of a texel of each other.
            float ax = (a.X * 0.5f + 0.5f) * resolution, ay = (a.Y * 0.5f + 0.5f) * resolution;
            float bx = (b.X * 0.5f + 0.5f) * resolution, by = (b.Y * 0.5f + 0.5f) * resolution;
            Assert.True(System.MathF.Abs(ax - bx) < 0.05f, $"x moved {System.MathF.Abs(ax - bx)} texels under a sub-texel pan");
            Assert.True(System.MathF.Abs(ay - by) < 0.05f, $"y moved {System.MathF.Abs(ay - by)} texels under a sub-texel pan");
        }

        [Fact]
        public void Snap_SuperTexelCameraPan_DoesMoveByWholeTexels()
        {
            // A pan LARGER than a texel must move the projection - but by an INTEGER number of texels (the origin
            // tracks the camera in texel-sized steps, not continuously and not frozen).
            const int resolution = 1024;
            const float radius = 8f;
            float worldPerTexel = (2f * radius) / resolution;

            var focusA = new Vector3(0f, 0f, 0f);
            var focusB = focusA + new Vector3(worldPerTexel * 10f, 0f, 0f); // 10-texel pan

            Matrix4x4 vpA = ShadowMapMath.BuildLightViewProj(LightDir, focusA, radius, resolution);
            Matrix4x4 vpB = ShadowMapMath.BuildLightViewProj(LightDir, focusB, radius, resolution);
            var probe = new Vector3(1.2f, 0.4f, -0.7f);
            Vector4 a = Vector4.Transform(new Vector4(probe, 1f), vpA);
            Vector4 b = Vector4.Transform(new Vector4(probe, 1f), vpB);
            float ax = (a.X * 0.5f + 0.5f) * resolution;
            float bx = (b.X * 0.5f + 0.5f) * resolution;
            float deltaTexels = ax - bx;
            // Non-trivially moved, and (post-snap) a near-integer number of texels.
            Assert.True(System.MathF.Abs(deltaTexels) > 1f, "a 10-texel pan should move the projection");
            float frac = System.MathF.Abs(deltaTexels - System.MathF.Round(deltaTexels));
            Assert.True(frac < 0.05f, $"post-snap movement should be whole texels; fractional part {frac}");
        }

        [Fact]
        public void TexelWorldSize_MatchesOrthoExtentOverResolution()
        {
            Assert.Equal(2f * 4f / 2048f, ShadowMapMath.TexelWorldSize(radius: 4f, resolution: 2048), 6);
        }

        [Fact]
        public void Fit_DegenerateLightDir_DoesNotThrowOrNaN()
        {
            // A near-zero light direction must fall back to a sane down-vector, not produce NaNs.
            Matrix4x4 vp = ShadowMapMath.BuildLightViewProj(Vector3.Zero, Vector3.Zero, radius: 3f, resolution: 512);
            Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), vp);
            Assert.False(float.IsNaN(clip.X) || float.IsNaN(clip.Y) || float.IsNaN(clip.Z));
        }
    }
}
