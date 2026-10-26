using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal sealed partial class FrameDiagnosticsRecorder
{
    readonly Guid _shadowEpoch = Guid.NewGuid();
    readonly ShadowStageProbeMode _shadowMode;
    long _shadowSequence;
    ShadowStageProbeConfiguration? _shadowConfiguration;

    internal ShadowStageProbeRequest NextShadowStageRequest => _phase == Phase.Sampling
        ? new(_shadowEpoch, _shadowSequence, _shadowMode) : default;

    internal static ShadowStageProbeMode ParseShadowStageMode(string? value) => value switch
    {
        null or "" or "off" => ShadowStageProbeMode.Off,
        "counts" => ShadowStageProbeMode.Counts,
        "timings" => ShadowStageProbeMode.Timings,
        _ => throw new ArgumentException("KE_SHADOW_STAGE_MODE must be off, counts or timings."),
    };

    internal static FrameDiagnosticsRecorder? FromSettings(string? path, string? value, string? ringWait = null)
    {
        var mode = ParseShadowStageMode(value);
        bool enabled = ParseRecordingWaitProbe(ringWait);
        if (string.IsNullOrEmpty(path))
        {
            if (mode != ShadowStageProbeMode.Off)
                throw new ArgumentException("KE_SHADOW_STAGE_MODE requires a usable KE_FRAME_DIAGNOSTICS_PATH recorder.");
            return null;
        }
        return new FrameDiagnosticsRecorder(path, System.Diagnostics.Stopwatch.Frequency, mode: mode, ringWaitProbe: enabled);
    }

    ShadowStageProbeRow ShadowRow(in ShadowStageCallbackResult completed, bool suppressed)
    {
        var request = NextShadowStageRequest;
        var status = ValidateShadowRow(request, completed, suppressed);
        return new(request, status, completed);
    }

    ShadowStageProbeStatus ValidateShadowRow(ShadowStageProbeRequest expected,
        in ShadowStageCallbackResult completed, bool suppressed)
    {
        if (_shadowMode == ShadowStageProbeMode.Off) return ShadowStageProbeStatus.Off;
        if (suppressed) return ShadowStageProbeStatus.NotRendered;
        if (!completed.Completed) return ShadowStageProbeStatus.IncompleteCallback;
        if (completed.Request.Epoch != expected.Epoch || completed.Request.Sequence != expected.Sequence)
            return ShadowStageProbeStatus.IdentityMismatch;
        if (completed.Request.Mode != _shadowMode) return ShadowStageProbeStatus.ModeMismatch;
        if (completed.ProducerCount > 1) return ShadowStageProbeStatus.DuplicateProducer;
        if (completed.ProducerCount != 1 || completed.Snapshot is not { } snapshot)
            return ShadowStageProbeStatus.MissingProducer;
        if (snapshot.Request.Epoch != expected.Epoch || snapshot.Request.Sequence != expected.Sequence)
            return ShadowStageProbeStatus.IdentityMismatch;
        if (snapshot.Request.Mode != _shadowMode || !snapshot.Counters.HasValue
            || (_shadowMode == ShadowStageProbeMode.Timings) != snapshot.Timings.HasValue)
            return ShadowStageProbeStatus.ModeMismatch;
        if (snapshot.Status != ShadowStageProbeStatus.Valid) return snapshot.Status;
        if (snapshot.Timings is { } timings && timings.Frequency != _frequency)
            return ShadowStageProbeStatus.InvalidClock;
        if (_shadowConfiguration is { } configuration && snapshot.Configuration != configuration)
            return ShadowStageProbeStatus.ConfigurationChanged;
        _shadowConfiguration ??= snapshot.Configuration;
        return ShadowStageProbeStatus.Valid;
    }
}
