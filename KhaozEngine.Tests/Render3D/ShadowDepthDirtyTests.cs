using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the shadow depth-pass dirty-skip decision (Scene3D.ShadowDepthPassDirty /
    /// ShadowCastersChanged). The pass re-renders the persistent light-space depth map only when a shadow-relevant
    /// input changed since the last rendered pass, and otherwise it reuses the prior map. These pure predicates carry the
    /// skip logic, so they are unit-tested without a GPU (the GPU proof that the reuse is pixel-identical lives in
    /// KhaozEngine.Tests/Gpu/ShadowDepthDirtySkipGpuTests).
    /// </summary>
    public sealed class ShadowDepthDirtyTests
    {
        static List<(int, int, uint)> Runs(params (int, int, uint)[] r) => new(r);
        static List<Matrix4x4> Models(params float[] tx)
        {
            var m = new List<Matrix4x4>();
            foreach (var x in tx) m.Add(Matrix4x4.CreateTranslation(x, 0f, 0f));
            return m;
        }

        [Fact]
        public void First_frame_is_always_dirty()
        {
            // hadPrevious false: no valid map to reuse yet, so the pass must render regardless of everything else.
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: false, anySkinnedCaster: false,
                resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
        }

        [Fact]
        public void Unchanged_static_scene_is_not_dirty()
        {
            Assert.False(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
        }

        [Fact]
        public void Any_skinned_caster_forces_dirty()
        {
            // A skinned caster animates its bones every frame. The palette is not hashed, so its mere presence is dirty.
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: true,
                resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
        }

        [Fact]
        public void Resolution_light_and_caster_changes_each_force_dirty()
        {
            Assert.True(Scene3D.ShadowDepthPassDirty(true, false, resolutionChanged: true, lightMatrixChanged: false, casterDataChanged: false));
            Assert.True(Scene3D.ShadowDepthPassDirty(true, false, resolutionChanged: false, lightMatrixChanged: true, casterDataChanged: false));
            Assert.True(Scene3D.ShadowDepthPassDirty(true, false, resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: true));
        }

        [Fact]
        public void Identical_caster_signatures_compare_equal()
        {
            var runsA = Runs((3, 1, 2), (5, 1, 1));
            var runsB = Runs((3, 1, 2), (5, 1, 1));
            var modelsA = Models(-1f, 0f, 2f);
            var modelsB = Models(-1f, 0f, 2f);
            Assert.False(Scene3D.ShadowCastersChanged(runsA, modelsA, runsB, modelsB));
        }

        [Fact]
        public void A_moved_caster_transform_is_a_change()
        {
            var runs = Runs((3, 1, 1));
            var a = Models(0f);
            var b = Models(0.001f);   // a tiny world translation still differs
            Assert.True(Scene3D.ShadowCastersChanged(runs, a, runs, b));
        }

        [Fact]
        public void Adding_or_removing_a_caster_is_a_change()
        {
            var runsA = Runs((3, 1, 1));
            var runsB = Runs((3, 1, 2));   // instance count grew
            Assert.True(Scene3D.ShadowCastersChanged(runsA, Models(0f), runsB, Models(0f, 1f)));

            // A different mesh handle (unloaded + reloaded into a new generation) is a change even at the same count.
            Assert.True(Scene3D.ShadowCastersChanged(Runs((3, 1, 1)), Models(0f), Runs((3, 2, 1)), Models(0f)));
        }

        // ---- Day/night readiness: a moving sun re-renders the shadow depth map --------------------------------------
        // Scene3D computes shadowLightVp = ComputeShadowLightViewProj(eye) -> ShadowMapMath.BuildLightViewProj(
        // normalize(Post.LightDirection), focus, ...) each frame, and dirties the depth pass when
        // _lastShadowLightVp != shadowLightVp (lightMatrixChanged). So a per-frame LightDirection change (a day/night
        // cycle) must produce a DIFFERENT light matrix and therefore re-render the depth map, not reuse the stale one.

        [Fact]
        public void A_changed_light_direction_produces_a_different_light_matrix()
        {
            // Same focus / radius / resolution, only the light direction moved (sun crossing the sky): the fitted
            // world->light-clip matrix must change, otherwise the shadow would stay frozen against the old sun.
            var focus = new Vector3(2f, 0f, -1f);
            var noon = ShadowMapMath.BuildLightViewProj(new Vector3(-0.2f, -1f, -0.1f), focus, radius: 16f, resolution: 2048);
            var evening = ShadowMapMath.BuildLightViewProj(new Vector3(-0.8f, -0.4f, -0.2f), focus, radius: 16f, resolution: 2048);
            Assert.NotEqual(noon, evening);
        }

        [Fact]
        public void A_moving_sun_dirties_the_shadow_depth_pass()
        {
            // Reproduce the exact Scene3D wiring: lightMatrixChanged = (_lastShadowLightVp != shadowLightVp). A sun
            // that moved between frames flips that true, so ShadowDepthPassDirty returns true (re-render), even though
            // nothing else changed (static casters, same resolution). This is the day/night dirty-tracking guarantee.
            var focus = new Vector3(2f, 0f, -1f);
            var last = ShadowMapMath.BuildLightViewProj(new Vector3(-0.2f, -1f, -0.1f), focus, 16f, 2048);
            var now = ShadowMapMath.BuildLightViewProj(new Vector3(-0.8f, -0.4f, -0.2f), focus, 16f, 2048);
            bool lightMatrixChanged = last != now;
            Assert.True(lightMatrixChanged);
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                resolutionChanged: false, lightMatrixChanged: lightMatrixChanged, casterDataChanged: false));

            // And when the sun HOLDS still (identical direction), the light matrix is unchanged, so a static scene
            // correctly reuses the persistent depth map (not dirtied by the light).
            var held = ShadowMapMath.BuildLightViewProj(new Vector3(-0.8f, -0.4f, -0.2f), focus, 16f, 2048);
            Assert.Equal(now, held);
            Assert.False(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                resolutionChanged: false, lightMatrixChanged: now != held, casterDataChanged: false));
        }

        [Fact]
        public void Caster_reorder_is_a_change()
        {
            // The signatures are captured in draw order, so swapping two runs' order is a change (blend order matters
            // less for a depth-only pass, but a reorder means the transforms no longer line up index-for-index).
            var runsA = Runs((3, 1, 1), (5, 1, 1));
            var runsB = Runs((5, 1, 1), (3, 1, 1));
            Assert.True(Scene3D.ShadowCastersChanged(runsA, Models(0f, 1f), runsB, Models(1f, 0f)));
        }
    }
}
