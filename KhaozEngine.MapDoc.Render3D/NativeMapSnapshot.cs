using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public static class NativeMapSnapshot
{
    public static MapSnapshotResult Capture(MapSnapshotRequest request)
    {
        return Capture(request, null);
    }
    /// <summary>Capture with a synchronous diagnostic callback after each actual submission, before rendering.
    /// The scene belongs to this call and must not be retained or disposed by the callback.</summary>
    public static MapSnapshotResult Capture(MapSnapshotRequest request, Action<Scene3D>? afterSubmit)
    {
        MapSnapshotResult? result = null;
        CaptureSequence(request, 1, (_, frame) => result = frame, afterSubmit);
        return result!;
    }
    public static void CaptureSequence(MapSnapshotRequest request, int count, Action<int, MapSnapshotResult> onFrame) => CaptureSequence(request, count, onFrame, null);
    static void CaptureSequence(MapSnapshotRequest request, int count, Action<int, MapSnapshotResult> onFrame, Action<Scene3D>? afterSubmit)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        ArgumentNullException.ThrowIfNull(onFrame);
        var prepared = MapCapturePreparation.Prepare(request);
        if (prepared.FocusSource == MapCaptureFocusSource.Unresolved || prepared.Witness.AcquiredScopes.Count == 0 ||
            request.Requirement == MapCaptureRequirement.CompleteScope && prepared.Status != MapSceneStatus.CompleteForScope)
        { onFrame(0, prepared); return; }
        MapSnapshotResult Refusal(MapSceneStatus status, string code) => prepared with
        {
            Status = status,
            Findings = [code],
            Witness = prepared.Witness with { Scene = prepared.Witness.Scene with { CompleteForScope = false, WholeWorldComplete = false } }
        };
        var echo = prepared.Echo;
        var proofs = echo.Source.RenderExtents ?? MapRenderExtentValidator.Validate(echo.Closure, echo.Source.RequiredAssetIds, new(new())).Proofs;
        if (proofs is null)
        { onFrame(0, Refusal(MapSceneStatus.Missing, "closure.unavailable")); return; }
        var source = echo.Source.WithRenderExtents(proofs).Source;
        if (source is null) { onFrame(0, Refusal(MapSceneStatus.Stale, "closure.stale")); return; }
        var state = new MapRenderSourceState(source, echo.Closure, proofs, echo.Policy.Render, prepared.Witness.AcquiredScopes);
        NativeMapView? view = null;
        var frameState = new MapFrameState(echo.Scope.Surfaces.Frame, echo.Observer, echo.RoofMode, echo.TimeSeconds, echo.WindSeed, echo.Focus!.Value);
        MapRenderReadiness readiness = prepared.Readiness;
        try
        {
            Render3DSnapshot.CaptureSequence(echo.Framing.Width, echo.Framing.Height, scene =>
            {
                Configure(scene, echo);
                var sink = new MapScene3DSink(scene) { Restriction = new(echo.Scope.DrawBounds) };
                view = new(scene, new(state, sink, new(MaxActiveWindows: 256)));
                readiness = view.SettleForCapture(echo.Policy.MaxSettleOperations);
                if (readiness.Pending != 0 || readiness.Transition.Status != MapSceneStatus.CompleteForScope) throw new CaptureNotReady();
            }, (scene, _) => { view!.Draw(frameState); afterSubmit?.Invoke(scene); }, checked(echo.Policy.WarmupFrames + count), (index, image) =>
            {
                var witness = prepared.Witness with
                {
                    Scene = readiness.Transition.AcceptedWitness! with
                    {
                        CompleteForScope = !prepared.Witness.Capped,
                        WholeWorldComplete = false
                    }
                };
                onFrame(index - echo.Policy.WarmupFrames, prepared with { Image = image, Backend = image.Backend.ToString(), Witness = witness, Readiness = readiness });
            }, echo.Policy.WarmupFrames, echo.Policy.Lighting.Shadows);
        }
        catch (CaptureNotReady)
        {
            onFrame(0, Refusal(readiness.Transition.Status == MapSceneStatus.CompleteForScope ? MapSceneStatus.Incomplete : readiness.Transition.Status,
            "resources.notReady") with
            { Readiness = readiness });
        }
        finally { view?.Dispose(); }
    }
    internal static void Configure(Scene3D scene, MapSnapshotRequest request)
    {
        scene.CameraOverride = request.Framing.Camera;
        scene.Post.LightDirection = -request.Policy.Lighting.DirectionToLight;
        scene.Post.LightColor = request.Policy.Lighting.SunColour.ScaleRgb(request.Policy.Lighting.SunIntensity);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = request.Policy.Aa switch
        {
            MapCaptureAa.Quality => TemporalUpscale.Quality,
            MapCaptureAa.Balanced => TemporalUpscale.Balanced,
            _ => TemporalUpscale.Native
        };
        scene.SetCaptureShadowDomain(ShadowCaptureDomain.Compute(request.Framing.ViewProjection, request.Policy.Lighting.DirectionToLight,
            request.Policy.Lighting.Shadows, request.Policy.Lighting.MaxCasterDistance));
        scene.CameraCut();
    }
    sealed class CaptureNotReady : Exception;
}
