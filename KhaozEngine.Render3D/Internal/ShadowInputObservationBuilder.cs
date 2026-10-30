using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D.Internal;

// One request owns the multiplicities. Finish never resets them, including after a duplicate prefix.
internal struct ShadowInputObservationBuilder
{
    ShadowStageProbeInputs _value;

    internal void Reset(ShadowStageProbeRequest request)
    {
        // Off and cancellation cannot make an earlier attempt with this identity disappear.
        if (request.Epoch == Guid.Empty || request.Sequence <= 0
            || request.Mode is not (ShadowStageProbeMode.Counts or ShadowStageProbeMode.Timings)) return;
        if (_value.Request.Epoch == request.Epoch && _value.Request.Sequence == request.Sequence) return;
        _value = default(ShadowStageProbeInputs) with { Request = request };
    }

    internal void BeginPrefix(long sceneFrameIndex, float effectTimeSeconds)
    {
        if (_value.PrefixCount == 0)
            _value = _value with { SceneFrameIndex = sceneFrameIndex, PrefixEffectTimeSeconds = effectTimeSeconds };
        _value = _value with { PrefixCount = _value.PrefixCount + 1 };
    }

    internal void ObserveView(in FrameView view)
    {
        if (_value.ViewCount == 0)
            _value = _value with
            {
                View = new(view.View, view.Projection, view.ViewProjection,
                view.AbsoluteViewProjection, view.RenderOrigin, view.Width, view.Height, view.JitterPixels)
            };
        _value = _value with { ViewCount = _value.ViewCount + 1 };
    }

    internal void ObserveCamera(Vector3 eye, Vector3 forward)
    {
        if (_value.CameraCount == 0) _value = _value with { Camera = new(eye, forward) };
        _value = _value with { CameraCount = _value.CameraCount + 1 };
    }

    internal void ObserveLight(Vector3 raw, Vector3 normalized, Vector3 selected)
    {
        if (_value.LightCount == 0) _value = _value with { Light = new(raw, normalized, selected) };
        _value = _value with { LightCount = _value.LightCount + 1 };
    }

    internal void ObserveFits(int count, int resolution, ReadOnlySpan<float> splits, ReadOnlySpan<Matrix4x4> absoluteFits)
    {
        if (_value.CascadeObservationCount == 0 && count is >= 1 and <= ShadowSettings.MaxCascades
            && splits.Length >= count && absoluteFits.Length >= count)
        {
            _value = _value with
            {
                Availability = ShadowInputAvailability.Active,
                Fits = new(count, resolution, new(splits[0], count > 1 ? splits[1] : 0,
                    count > 2 ? splits[2] : 0, count > 3 ? splits[3] : 0),
                    absoluteFits[0], count > 1 ? absoluteFits[1] : default,
                    count > 2 ? absoluteFits[2] : default, count > 3 ? absoluteFits[3] : default),
            };
        }
        _value = _value with { CascadeObservationCount = _value.CascadeObservationCount + 1 };
    }

    internal void ObserveUnavailable(ShadowInputAvailability reason)
    {
        if (_value.CascadeObservationCount == 0) _value = _value with { Availability = reason };
        _value = _value with { CascadeObservationCount = _value.CascadeObservationCount + 1 };
    }

    internal void ObserveDecision(in ShadowPassDiagnostics decision)
    {
        if (_value.DecisionCount == 0)
            _value = _value with
            {
                Decision = new(decision.Active, decision.Rendered, decision.Skipped,
                decision.HadPrevious, decision.AnySkinnedCaster, decision.SkinnedCastersCleared,
                decision.ResolutionChanged, decision.LightMatrixChanged, decision.CasterDataChanged,
                decision.SkinnedCasterCount, decision.CascadeCount)
            };
        _value = _value with { DecisionCount = _value.DecisionCount + 1 };
    }

    internal readonly ShadowStageProbeInputs Finish() => _value;
}
