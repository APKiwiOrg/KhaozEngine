using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace KhaozEngine.Windowing;

// Private archive values. No scene scratch, camera or mutable collection escapes here.
public enum ShadowInputAvailability { Missing, Active, Disabled, Degenerate }

public readonly record struct ShadowInputView(Matrix4x4 View, Matrix4x4 Projection,
    Matrix4x4 ViewProjection, Matrix4x4 AbsoluteViewProjection, Vector3 RenderOrigin,
    int Width, int Height, Vector2 JitterPixels);

public readonly record struct ShadowInputCamera(Vector3 Eye, Vector3 Forward);
public readonly record struct ShadowInputLight(Vector3 Raw, Vector3 Normalized, Vector3 Selected);
public readonly record struct ShadowInputFits(int Count, int Resolution, Vector4 Splits,
    Matrix4x4 Fit0, Matrix4x4 Fit1, Matrix4x4 Fit2, Matrix4x4 Fit3);
public readonly record struct ShadowInputDecision(bool Active, bool Rendered, bool Skipped,
    bool HadPrevious, bool AnySkinnedCaster, bool SkinnedCastersCleared, bool ResolutionChanged,
    bool LightMatrixChanged, bool CasterDataChanged, int SkinnedCasterCount, int CascadeCount);

[JsonConverter(typeof(FrameDiagnosticsInputSerialization))]
public readonly record struct ShadowStageProbeInputs(ShadowStageProbeRequest Request, long SceneFrameIndex,
    int PrefixCount, int ViewCount, int CascadeObservationCount, int CameraCount, int LightCount,
    int DecisionCount, ShadowInputAvailability Availability, float PrefixEffectTimeSeconds,
    ShadowInputView View, ShadowInputCamera Camera, ShadowInputLight Light, ShadowInputFits Fits,
    ShadowInputDecision Decision)
{
    public bool Complete => Request.Epoch != Guid.Empty && Request.Sequence > 0
        && Request.Mode is ShadowStageProbeMode.Counts or ShadowStageProbeMode.Timings
        && SceneFrameIndex > 0 && View.Width > 0 && View.Height > 0
        && PrefixCount == 1 && ViewCount == 1 && CascadeObservationCount == 1
        && DecisionCount == 1 && (Availability == ShadowInputAvailability.Active
            ? CameraCount == 1 && LightCount == 1 && Fits.Count is >= 1 and <= 4
                && Fits.Resolution > 0 && Decision.Active && Decision.CascadeCount == Fits.Count
                && Decision.Rendered != Decision.Skipped
            : Availability is ShadowInputAvailability.Disabled or ShadowInputAvailability.Degenerate
                && CameraCount == 0 && LightCount == 0 && Fits == default && Camera == default
                && Light == default && Decision == default);
}
