using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure coverage of <see cref="Scene3D.ClassifySkinnedVisibility"/>: the main-pass / shadow-caster visibility
    /// split that decides whether a queued skinned draw is CPU-skinned this frame at all (Scene3D.RenderInternal).
    /// Mirrors <see cref="FrustumPlanesTests"/>'s style - real engine camera / light matrices, not hand-authored
    /// ones - and specifically pins the critical invariant a GPU test also covers end to end
    /// (Gpu/FrustumCullingGpuTests): a draw camera-culled from the main pass but still inside the active shadow
    /// map's ortho volume must be kept alive for the shadow pass (VisibleShadow true), never dropped outright.
    /// </summary>
    public class Scene3DSkinnedCullingTests
    {
        static readonly MeshBounds UnitCubeBounds = new(new Vector3(-1f), new Vector3(1f)); // radius sqrt(3) ~= 1.732

        static FrustumPlanes OrthoFrustum() =>
            FrustumPlanes.Extract(new IsoCamera3D
            {
                Target = Vector3.Zero, Azimuth = 0f, Elevation = 0f, OrthoSize = 10f, AspectRatio = 1f,
            }.ViewProjection);   // ortho half-extent 5 on X and Y (see FrustumPlanesTests)

        [Fact]
        public void CullingOff_and_ShadowInactive_IsAlwaysVisibleMain_NeverShadow()
        {
            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, Matrix4x4.CreateTranslation(1000f, 0f, 0f),
                cullMain: false, mainFrustum: default,
                shadowActive: false, shadowFrustum: default);

            Assert.True(main);
            Assert.False(shadow);
        }

        [Fact]
        public void CullingOff_ButShadowActive_IsStillAlwaysVisibleMain()
        {
            // FrustumCulling off is the rigid-instance parity path: everything draws in the main pass regardless
            // of the shadow tier. VisibleShadow is still evaluated on its own merits (unused by the caller once
            // VisibleMain is true), so it may come back either way depending on geometry - only VisibleMain is
            // pinned here.
            FrustumPlanes shadowFrustum = FrustumPlanes.Extract(
                ShadowMapMath.BuildLightViewProj(new Vector3(-0.3f, -1f, -0.2f), Vector3.Zero, radius: 5f, resolution: 512));

            var (main, _) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, Matrix4x4.CreateTranslation(1000f, 0f, 0f),
                cullMain: false, mainFrustum: default,
                shadowActive: true, shadowFrustum: shadowFrustum);

            Assert.True(main);
        }

        [Fact]
        public void InCameraFrustum_IsVisibleMain_ShadowInactive_NeverShadow()
        {
            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, Matrix4x4.Identity,
                cullMain: true, mainFrustum: OrthoFrustum(),
                shadowActive: false, shadowFrustum: default);

            Assert.True(main);
            Assert.False(shadow);
        }

        [Fact]
        public void OutOfCameraFrustum_AndShadowInactive_IsFullyCulled()
        {
            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, Matrix4x4.CreateTranslation(1000f, 0f, 0f),
                cullMain: true, mainFrustum: OrthoFrustum(),
                shadowActive: false, shadowFrustum: default);

            Assert.False(main);
            Assert.False(shadow);
        }

        [Fact]
        public void OutOfCameraFrustum_ButInsideShadowVolume_IsKeptAliveForShadowOnly()
        {
            // THE critical invariant: an off-camera character must still cast a shadow if it sits inside the
            // shadow map's own light-space ortho volume. The shadow focus here is centred far from the camera's
            // ortho view (x=30) specifically so the draw is providably outside the CAMERA frustum yet inside the
            // SHADOW frustum - the exact split the CPU-skin pass relies on to avoid dropping a visible shadow.
            FrustumPlanes shadowFrustum = FrustumPlanes.Extract(
                ShadowMapMath.BuildLightViewProj(new Vector3(-0.3f, -1f, -0.2f), focus: new Vector3(30f, 0f, 0f), radius: 10f, resolution: 512));
            var world = Matrix4x4.CreateTranslation(30f, 0f, 0f);

            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, world,
                cullMain: true, mainFrustum: OrthoFrustum(),
                shadowActive: true, shadowFrustum: shadowFrustum);

            Assert.False(main);
            Assert.True(shadow);
        }

        [Fact]
        public void OutOfBothVolumes_IsFullyCulled()
        {
            FrustumPlanes shadowFrustum = FrustumPlanes.Extract(
                ShadowMapMath.BuildLightViewProj(new Vector3(-0.3f, -1f, -0.2f), Vector3.Zero, radius: 10f, resolution: 512));
            var world = Matrix4x4.CreateTranslation(1000f, 0f, 0f);   // far outside both the camera and shadow volumes

            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, world,
                cullMain: true, mainFrustum: OrthoFrustum(),
                shadowActive: true, shadowFrustum: shadowFrustum);

            Assert.False(main);
            Assert.False(shadow);
        }

        [Fact]
        public void InsideBothVolumes_IsVisibleInBoth()
        {
            FrustumPlanes shadowFrustum = FrustumPlanes.Extract(
                ShadowMapMath.BuildLightViewProj(new Vector3(-0.3f, -1f, -0.2f), Vector3.Zero, radius: 10f, resolution: 512));

            var (main, shadow) = Scene3D.ClassifySkinnedVisibility(
                UnitCubeBounds, Matrix4x4.Identity,
                cullMain: true, mainFrustum: OrthoFrustum(),
                shadowActive: true, shadowFrustum: shadowFrustum);

            Assert.True(main);
            Assert.True(shadow);
        }

        [Fact]
        public void SafetyFactor_KeepsADraw_JustOutsideRawRestBounds_ButWithinTheInflatedRadius()
        {
            // A small rest-pose bound placed so its RAW sphere does not reach the frustum's right face (at X=5),
            // but SkinnedCullSafetyFactor's inflation does - proving the inflation is genuinely applied (an
            // animation excursion beyond the static rest pose still culls conservatively rather than clipping a
            // silhouette that has posed into view).
            Assert.True(Scene3D.SkinnedCullSafetyFactor > 1f, "the safety factor must actually inflate the radius");

            var tinyBounds = new MeshBounds(new Vector3(-0.1f), new Vector3(0.1f));
            tinyBounds.WorldSphere(Matrix4x4.Identity, out _, out float rawRadius);
            const float distanceToFace = 0.2f;   // right face at X=5, sphere centred at X=5.2
            Assert.True(rawRadius < distanceToFace, $"test premise: raw radius {rawRadius} must NOT reach the face");
            Assert.True(rawRadius * Scene3D.SkinnedCullSafetyFactor > distanceToFace,
                $"test premise: the inflated radius {rawRadius * Scene3D.SkinnedCullSafetyFactor} must reach the face");

            var world = Matrix4x4.CreateTranslation(5f + distanceToFace, 0f, 0f);
            var (main, _) = Scene3D.ClassifySkinnedVisibility(
                tinyBounds, world, cullMain: true, mainFrustum: OrthoFrustum(), shadowActive: false, shadowFrustum: default);

            Assert.True(main, "the safety-inflated sphere should reach back across the frustum face");
        }
    }
}
