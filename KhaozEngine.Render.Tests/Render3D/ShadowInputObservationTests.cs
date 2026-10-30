using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowInputObservationTests
{
    [Fact]
    public void CascadeObservationCopiesTheClampedDistancesActuallyUsedByTheFits()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        f.Scene.Post.Quality.Shadows.ShadowNearDistance = 100000;
        f.Scene.Post.Quality.Shadows.ShadowMaxDistance = 200000;
        var input = f.Render(ShadowStageProbeMode.Counts, point: false).Inputs!.Value;
        // The fixture camera has a 200-unit far plane. The fit clamps this oversized request there.
        Assert.Equal(200, input.Fits.Splits.X, 3);
    }

    [Theory]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public void ObservationCopiesActualPrefixValuesWithoutReadingCameraAgain(ShadowStageProbeMode mode)
    {
        using var off = new ShadowStageProbeFixture(directional: true);
        using var on = new ShadowStageProbeFixture(directional: true);
        // The existing camera fixture mutates each read, exposing diagnostic re-reads.
        var offCamera = new InputCountingCamera();
        var onCamera = new InputCountingCamera();
        off.Scene.CameraOverride = offCamera;
        on.Scene.CameraOverride = onCamera;
        off.Scene.Post.LightDirection = on.Scene.Post.LightDirection = new Vector3(.25f, -1, .5f);
        off.Render(ShadowStageProbeMode.Off, point: false);
        var snapshot = on.Render(mode, point: false);
        Assert.Equal(offCamera.Reads, onCamera.Reads);
        Assert.Equal(off.Order(), on.Order());
        var input = Assert.IsType<ShadowStageProbeInputs>(snapshot.Inputs);
        Assert.True(input.Complete);
        var view = on.Scene.CurrentFrameView;
        Assert.Equal(view.View, input.View.View);
        Assert.Equal(view.Projection, input.View.Projection);
        Assert.Equal(view.ViewProjection, input.View.ViewProjection);
        Assert.Equal(view.AbsoluteViewProjection, input.View.AbsoluteViewProjection);
        Assert.Equal(onCamera.CascadeEye, input.Camera.Eye);
        Assert.Equal(onCamera.ForwardRead, input.Camera.Forward);
        Assert.Equal(new Vector3(.25f, -1, .5f), input.Light.Raw);
        Assert.Equal(Vector3.Normalize(input.Light.Raw), input.Light.Normalized);
        Assert.Equal(input.Light.Normalized, input.Light.Selected);
        Assert.Equal(on.Scene.LastShadowPassDiagnostics.Rendered, input.Decision.Rendered);
        Assert.Equal(ShadowInputAvailability.Active, input.Availability);
        Assert.NotEqual(default, input.Fits.Fit0);
        Assert.Equal(view.FrameIndex, input.SceneFrameIndex);
    }

    [Fact]
    public void ObservationUsesPostEnsureSizeDimensions()
    {
        using var f = new ShadowStageProbeFixture();
        f.Scene.Post.RenderWidth = 16;
        f.Scene.Post.RenderHeight = 12;
        var input = f.Render(ShadowStageProbeMode.Counts).Inputs;
        Assert.NotNull(input);
        Assert.Equal(f.Scene.CurrentFrameView.Width, input.Value.View.Width);
        Assert.Equal(f.Scene.CurrentFrameView.Height, input.Value.View.Height);
        Assert.Equal(16, input.Value.View.Width);
        Assert.Equal(12, input.Value.View.Height);
    }

    [Fact]
    public void SnapshotSurvivesScratchReuseAndLateMutation()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        f.Scene.EffectTimeSeconds = 17.25f;
        var saved = f.Render(ShadowStageProbeMode.Counts).Inputs;
        Assert.NotNull(saved);
        var copy = saved.Value;
        f.Scene.EffectTimeSeconds = 92;
        f.Scene.Post.LightDirection = Vector3.UnitX;
        var next = f.Render(ShadowStageProbeMode.Counts).Inputs!.Value;
        Assert.Equal(copy, saved.Value);
        Assert.Equal(17.25f, saved.Value.PrefixEffectTimeSeconds);
        Assert.NotEqual(saved.Value.Light, next.Light);
        Assert.NotEqual(saved.Value.SceneFrameIndex, next.SceneFrameIndex);
    }

    [Fact]
    public void DuplicatePrefixOrFitRemainsInvalidUntilNextRequest()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        var request = new ShadowStageProbeRequest(Guid.NewGuid(), 7, ShadowStageProbeMode.Counts);
        f.Frame.BeginShadowStageCallback(request);
        f.Scene.ConfigureShadowStageProbe(request);
        f.Scene.Begin(); f.Scene.PrepareFrame();
        f.Commands.Begin();
        f.Scene.RenderInternal(f.Commands, 32, 24, f.Target);
        f.Scene.RenderInternal(f.Commands, 32, 24, f.Target);
        f.Commands.End();
        f.Scene.PublishShadowStageProbe(f.Frame);
        var input = Assert.IsType<ShadowStageProbeInputs>(f.Frame.CompletedShadowStageCallback.Snapshot!.Value.Inputs);
        Assert.False(input.Complete);
        Assert.Equal(2, input.PrefixCount);
        Assert.Equal(2, input.ViewCount);
        Assert.Equal(2, input.CascadeObservationCount);
        Assert.True(f.Render(ShadowStageProbeMode.Counts).Inputs!.Value.Complete);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("cancelled")]
    [InlineData("off")]
    public void ReconfiguringTheSameRequestCannotEraseAnEarlierPrefix(string between)
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        var request = new ShadowStageProbeRequest(Guid.NewGuid(), 7, ShadowStageProbeMode.Counts);
        f.Frame.BeginShadowStageCallback(request);
        for (int i = 0; i < 2; i++)
        {
            f.Scene.ConfigureShadowStageProbe(request);
            f.Scene.Begin(); f.Scene.PrepareFrame();
            f.Commands.Begin();
            f.Scene.RenderInternal(f.Commands, 32, 24, f.Target);
            f.Commands.End();
            if (i == 0 && between == "cancelled") f.Scene.CancelShadowStageProbe();
            if (i == 0 && between == "off") f.Scene.ConfigureShadowStageProbe(default);
        }
        f.Scene.PublishShadowStageProbe(f.Frame);
        var input = f.Frame.CompletedShadowStageCallback.Snapshot!.Value.Inputs!.Value;
        Assert.Equal(2, input.PrefixCount);
        Assert.False(input.Complete);
    }

    [Fact]
    public void DisabledThenActiveDoesNotReuseFits()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        Assert.True(f.Render(ShadowStageProbeMode.Counts).Inputs?.Complete);
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var disabled = f.Render(ShadowStageProbeMode.Counts).Inputs!.Value;
        Assert.Equal(ShadowInputAvailability.Disabled, disabled.Availability);
        Assert.True(disabled.Complete);
        Assert.Equal(default, disabled.Fits);
        Assert.Equal(0, disabled.CameraCount);
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        Assert.Equal(ShadowInputAvailability.Active, f.Render(ShadowStageProbeMode.Counts).Inputs!.Value.Availability);
    }

    [Fact]
    public void DegenerateCameraIsDistinctFromDisabled()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        f.Scene.CameraOverride = new InputCountingCamera { Singular = true };
        var input = f.Render(ShadowStageProbeMode.Counts, point: false).Inputs;
        Assert.NotNull(input);
        Assert.Equal(ShadowInputAvailability.Degenerate, input.Value.Availability);
        Assert.True(input.Value.Complete);
        Assert.Equal(default, input.Value.Fits);
        Assert.Equal(0, input.Value.CameraCount);
        Assert.False(input.Value.Decision.Active);
    }
}

internal sealed class InputCountingCamera : IIsoCamera3D
{
    readonly IsoCamera3D _camera = new();
    internal int Reads;
    internal bool Singular;
    internal readonly System.Collections.Generic.List<Vector3> EyeReads = [];
    internal Vector3 ForwardRead;
    internal Vector3 CascadeEye;
    public Matrix4x4 View { get { Reads++; return _camera.View; } }
    public Matrix4x4 Projection { get { Reads++; return _camera.Projection; } }
    public Matrix4x4 ViewProjection { get { Reads++; return Singular ? default : _camera.ViewProjection; } }
    public Vector3 Eye
    {
        get
        {
            Reads++;
            var eye = _camera.Eye + new Vector3(EyeReads.Count * .125f, 0, 0);
            EyeReads.Add(eye);
            if (new System.Diagnostics.StackTrace(1).GetFrame(0)?.GetMethod()?.Name == "ComputeShadowCascades")
                CascadeEye = eye;
            return eye;
        }
    }
    public Vector3 Forward { get { Reads++; return ForwardRead = _camera.Forward; } }
    public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel)
        => _camera.WorldToScreen(world, width, height, out pixel);
}
