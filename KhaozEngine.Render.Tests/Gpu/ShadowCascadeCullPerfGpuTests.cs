using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A bounded draw-count check for per-cascade caster culling. The near/far pair follows the
    /// fall-through fixture in <see cref="ShadowCascadeCullGpuTests"/>, without a timing workload.
    /// </summary>
    public sealed class ShadowCascadeCullPerfGpuTests
    {
        const int W = 160, H = 112, Resolution = 256, Cascades = 4;
        const int ObservedFrames = 2;

        [GpuFact]
        public void A_small_caster_fixture_draws_fewer_instances_with_culling()
        {
            var shadows = new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = Resolution,
                ShadowCascadeCount = Cascades,
                ShadowNearDistance = 8f,
                ShadowLightHoldTexels = 0f,
            };
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H, shadows);
            Scene3D scene = preview.Scene;
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Camera.Azimuth = 0.5f;
            scene.Camera.Elevation = 0.6f;
            scene.Camera.Frame(new Vector3(0f, 0f, 20f), new Vector3(18f, 6f, 50f));
            scene.ShadowCullMergeGap = Scene3D.DefaultShadowCullMergeGap;

            // Distinct mesh spans keep the merge gap from folding the rejected far caster into the near one.
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(100f, 0.1f));
            MeshHandle nearBox = scene.LoadMesh(MeshPrimitives.Box(1.5f));
            MeshHandle farBox = scene.LoadMesh(MeshPrimitives.Box(1.6f));
            float sunRadians = 0f;
            void DrawFrame(Scene3D s)
            {
                // Keep every observed depth pass dirty, so cached counts cannot satisfy the check.
                s.Post.LightDirection = Vector3.Transform(new Vector3(-0.55f, -0.8f, -0.25f),
                    Matrix4x4.CreateRotationY(sunRadians));
                sunRadians += 0.0001f;
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 20f),
                    new Color(0.60f, 0.61f, 0.63f, 1f), Material.None, false);
                s.Draw(nearBox, Matrix4x4.CreateScale(1f, 2f, 1f) * Matrix4x4.CreateTranslation(0f, 1.6f, 4f),
                    new Color(0.2f, 0.75f, 0.25f, 1f));
                s.Draw(farBox, Matrix4x4.CreateScale(1f, 2.2f, 1f) * Matrix4x4.CreateTranslation(0f, 1.8f, 44f),
                    new Color(0.85f, 0.35f, 0.15f, 1f));
            }

            (int Candidates, int Draws)[] Observe(bool culling)
            {
                scene.ShadowCascadeCulling = culling;
                preview.Capture(DrawFrame);
                var samples = new (int Candidates, int Draws)[ObservedFrames];
                for (int frame = 0; frame < samples.Length; frame++)
                {
                    preview.Capture(DrawFrame);
                    ShadowPassDiagnostics pass = scene.LastShadowPassDiagnostics;
                    Assert.True(pass.Rendered, "the moving sun must record a fresh shadow pass");
                    Assert.Equal(Cascades, pass.CascadeCount);
                    Assert.Equal(2, scene.ShadowCasterCandidateCount);
                    int draws = 0;
                    for (int cascade = 0; cascade < Cascades; cascade++)
                    {
                        int count = scene.ShadowCascadeCasterCount(cascade);
                        if (!culling) Assert.Equal(2, count);
                        draws += count;
                    }
                    Assert.True(draws > 0, "the fixture must render casters into the shadow cascades");
                    samples[frame] = (scene.ShadowCasterCandidateCount, draws);
                }
                return samples;
            }

            var full = Observe(false);
            var culled = Observe(true);
            for (int frame = 0; frame < ObservedFrames; frame++)
            {
                Assert.Equal(full[frame].Candidates, culled[frame].Candidates);
                Assert.True(culled[frame].Draws < full[frame].Draws,
                    $"culling should draw fewer caster instances, got {culled[frame].Draws} of {full[frame].Draws}");
            }
        }
    }
}
