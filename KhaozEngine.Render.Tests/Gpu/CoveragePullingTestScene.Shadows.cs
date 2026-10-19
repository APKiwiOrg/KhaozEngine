using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class CoveragePullingTestScene
{
    internal CoverageOriginalPoseControl CreateOriginalPoseControl() => new(device, Scene.CoverageGpuBatchForTests!, Actors, Bindings);

    internal readonly record struct Shadows(CoverageTargetReadback Cascades, CoverageTargetReadback Points);

    internal Shadows CaptureShadows(bool coverage, bool staticLight, bool shadowOnly, bool cameraCulled, CoverageOriginalPoseControl? control = null)
    {
        Scene.UseGpuSkinning = !coverage;
        Scene.FrustumCulling = true;
        Scene.Camera.Target = Vector3.Zero;
        Scene.Camera.OrthoSize = cameraCulled ? .5f : 5;
        Scene.Camera.FarPlane = 40;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        Scene.RequestShadowMapLayout(256, 4);
        Scene.Post.Quality.Shadows.ShadowMaxDistance = 30;
        Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
        void Frame(bool selected)
        {
            Scene.Begin(); Scene.EffectTimeSeconds = 0;
            Scene.AddLight(Vector3.Zero, Color.White, 6, 1, staticLight ? LightShadow.Static(100) : LightShadow.Dynamic);
            for (int i = 0; i < Actors.Length; i++)
                Actors[i].Draw(selected, false, CoverageGpuScene.ShadowWorlds[i], shadowOnly, outlines: false, referenceEquipment: true);
            Scene.PrepareFrame(160, 120);
            Bindings.Draws.Clear();
            using var commands = new RecordingGpuCommandList(device.Factory.CreateCommandList()) { CapturePayloads = true };
            bool controlUploaded = false;
            commands.BeforeNonIndexedDraw = (bound, count, instances, first, firstInstance) =>
            {
                if (control is not null && bound.Pipeline is { } pipeline
                    && ((UniformBufferTrackingGpuDevice)device).ProgramOf(pipeline) is { } program)
                {
                    bool cascade = program.Vertex == ShaderSources.CoverageCascadeVert;
                    bool point = program.Vertex == ShaderSources.CoveragePointVert;
                    if (cascade || point)
                    {
                        if (!controlUploaded) { control.Upload(commands); controlUploaded = true; }
                        uint slot = cascade ? 1u : 2u;
                        control.Bind(commands, cascade, bound.Sets[slot].DynamicOffset);
                    }
                }
                if (control is null) Bindings.Observe(commands, bound, count, instances, first, firstInstance);
            };
            using (GpuRecording.Open(device, commands, "CoverageShadows")) Scene.RenderInternal(commands, 160, 120, target);
            device.Submit(commands.Inner); device.WaitForIdle(); device.Present();
        }
        // Allocation, base residency and transient selection each advance at a frame boundary.
        Frame(false); Frame(false); Frame(false);
        Frame(coverage);
        return new(Scene.ReadCoverageCascadesForTests(), Scene.ReadCoveragePointsForTests(staticLight) ?? throw new InvalidOperationException("Selected point row is absent."));
    }
}
