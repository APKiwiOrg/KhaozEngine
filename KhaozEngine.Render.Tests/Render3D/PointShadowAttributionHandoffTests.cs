using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointShadowAttributionHandoffTests
{
    [Fact]
    public void Game_host_forwards_control_and_cancellation_and_next_callback_clear_scene_references()
    {
        using var f = new ShadowStageProbeFixture();
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(8, 4, 16, 16, 32, 16, 8, 16));
        var request = new ShadowStageProbeRequest(capture.Epoch, 1, ShadowStageProbeMode.Counts);
        void Queue(Scene3D scene)
        {
            scene.Draw(f.Mesh, Matrix4x4.Identity);
            scene.AddLight(new(0, 2, 0), Color.White, 6, 1, LightShadow.Static(7));
        }
        void Render()
        {
            f.Commands.Begin(); f.Scene.RenderInternal(f.Commands, 32, 24, f.Target); f.Commands.End();
            f.Scene.PublishShadowStageProbe(f.Frame); f.Frame.CompleteShadowStageCallback();
        }
        f.Frame.BeginShadowStageCallback(request, capture);
        GameApp3D.PrepareProbedScene(f.Scene, Queue, f.Frame);
        Render();
        Assert.Equal(ShadowStageProbeStatus.Valid, f.Frame.CompletedShadowStageCallback.Snapshot!.Value.Status);
        Assert.Equal(1, capture.FrameCount);
        Assert.NotNull(f.Frame.CompletedShadowStageCallback.Snapshot.Value.PointAttribution);

        f.Frame.BeginShadowStageCallback(request with { Sequence = 2 }, capture);
        var original = new InvalidOperationException("planned queue failure");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => GameApp3D.PrepareProbedScene(f.Scene, scene =>
        {
            Queue(scene); scene.PreparePointShadowRequests(Vector3.Zero); throw original;
        }, f.Frame)));
        Assert.Equal(PointAttributionAvailability.Abandoned, capture.GetFrame(1).Availability);
        Assert.False(capture.GetFrame(1).Complete);
        f.Scene.PreparePointShadowRequests(Vector3.Zero);
        Assert.Equal(2, capture.FrameCount);

        f.Frame.BeginShadowStageCallback(request with { Sequence = 3 });
        Assert.Null(f.Frame.ShadowStagePointAttribution);
        GameApp3D.PrepareProbedScene(f.Scene, Queue, f.Frame);
        Render();
        Assert.Null(f.Frame.CompletedShadowStageCallback.Snapshot!.Value.PointAttribution);
        Assert.Equal(2, capture.FrameCount);
    }
}
