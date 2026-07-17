using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU-level proof that skinned-draw frustum culling (Scene3D.RenderInternal's CPU-skin pass) never breaks
    /// shadow correctness: a skinned character camera-culled from the main pass must still be CPU-skinned and
    /// rendered into the light-space shadow map when it sits inside the shadow's own ortho volume. Mirrors
    /// <see cref="FrustumCullingGpuTests"/>'s rigid-instance proof, exercised through the skinned queue instead.
    /// Invariant tests (no committed golden): they assert relationships between renders, so they run on any
    /// backend without a baked reference. Gated on KE_GPU_TESTS like the goldens.
    /// </summary>
    public sealed class SkinnedFrustumCullingGpuTests
    {
        const int W = 480, H = 320;

        static SkinnedGltfMesh Tube() => SkinnedMeshBuilder.BuildTube(0.5f, 2f, 8, 8, 4, Axis.Z);

        [GpuFact]
        public void OffCameraSkinnedCaster_IsCulledFromTheMainPass_YetStillWritesTheShadowMap()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            SkinnedMeshHandle caster = scene.LoadSkinnedMesh(Tube());
            SkinnedGltfMesh tube = Tube();   // same layout, for the rest pose
            scene.FrustumCulling = true;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowNearDistance = 10f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            // Frame a small region near the origin. The skinned caster at x=-10 sits outside this camera frustum
            // but inside the ShadowNearDistance=10 ortho volume centred near the camera's ground focus.
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(5f, 4f, 5f));
            var casterPos = Matrix4x4.CreateTranslation(-10f, 0.9f, 0f);

            // Without the caster: the shadow map holds only the (non-casting) floor's baseline near-texel count.
            preview.Capture(s => s.Draw(floor, Matrix4x4.Identity));
            int nearNoCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            // With the off-frustum skinned caster: it is culled from the visible pass (CulledSkinnedInstances
            // counts it, DrawnSkinnedInstances does not) yet the shadow pass still renders it.
            preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.DrawSkinned(caster, tube.RestPose, casterPos, new Color(0.15f, 0.75f, 0.2f, 1f));
            });
            int drawnSkinned = scene.DrawnSkinnedInstances;
            int culledSkinned = scene.CulledSkinnedInstances;
            int nearWithCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            Assert.Equal(0, drawnSkinned);
            Assert.Equal(1, culledSkinned);
            Assert.True(nearWithCaster > nearNoCaster + 20,
                $"the culled skinned caster must still write the shadow map: near texels {nearNoCaster} -> {nearWithCaster}");

            scene.UnloadSkinnedMesh(caster);
        }

        [GpuFact]
        public void SkinnedCaster_OutsideBothTheCameraAndShadowVolumes_WritesNoShadowTexelsAndIsFullyCulled()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            SkinnedMeshHandle caster = scene.LoadSkinnedMesh(Tube());
            SkinnedGltfMesh tube = Tube();
            scene.FrustumCulling = true;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowNearDistance = 6f;   // narrower than the caster's distance below
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(5f, 4f, 5f));
            // Far enough that it is outside BOTH the camera frustum AND the narrow shadow ortho volume.
            var casterPos = Matrix4x4.CreateTranslation(-500f, 0.9f, 0f);

            preview.Capture(s => s.Draw(floor, Matrix4x4.Identity));
            int nearNoCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.DrawSkinned(caster, tube.RestPose, casterPos, new Color(0.15f, 0.75f, 0.2f, 1f));
            });
            int nearWithCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            Assert.Equal(0, scene.DrawnSkinnedInstances);
            Assert.Equal(1, scene.CulledSkinnedInstances);
            Assert.Equal(nearNoCaster, nearWithCaster);

            scene.UnloadSkinnedMesh(caster);
        }

        // Count shadow-map texels that hold a NEAR (caster-written) depth. The map clears to 1.0 (far), and a caster
        // writes a value < 1, so counting sub-1 texels measures caster coverage in the light-space depth map.
        static int NearTexels(float[] depth)
        {
            int n = 0;
            for (int i = 0; i < depth.Length; i++) if (depth[i] < 0.999f) n++;
            return n;
        }
    }
}
