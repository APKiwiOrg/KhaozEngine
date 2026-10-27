using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>Consumer identities and live gain mixing for explicitly controlled SFX loops.</summary>
public sealed partial class AudioSystem
{
    sealed class LoopMix
    {
        public string? Bus;
        public float Volume;
        public Vector3? Position;
        public float DesiredGain;
        public float AppliedGain;
        public bool GainDirty;
    }

    readonly object _loopOwner = new();
    readonly Dictionary<SfxLoopBackendHandle, LoopMix> _loopMix = new();
    readonly List<SfxLoopBackendHandle> _endedLoops = new();

    /// <summary>The backend's fixed protected-loop capacity, or zero without the optional capability.</summary>
    public int LoopCapacity
    {
        get
        {
            EnsureOwningThread();
            return _disposed ? 0 : (_sfxBackend as ISfxLoopBackend)?.LoopCapacity ?? 0;
        }
    }

    /// <summary>
    /// Starts a protected loop using the registered SFX buffer. Admission belongs entirely to the backend.
    /// Finite volume is clamped to 0..1. Pitch must be finite and positive, and positions must be finite.
    /// </summary>
    public SfxLoopStartResult TryPlayLoop(in SfxLoopRequest request)
    {
        EnsureOwningThread();
        if (_disposed) return new(SfxLoopStartStatus.Disposed, default);
        if (string.IsNullOrWhiteSpace(request.Key)) throw new ArgumentException("An SFX key is required.", nameof(request));
        ValidateLoopVolume(request.Volume);
        if (!float.IsFinite(request.Pitch) || request.Pitch <= 0f) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Position is Vector3 position) ValidateLoopPosition(position);
        if (!_sfx.TryGetValue(request.Key, out int buffer)) return new(SfxLoopStartStatus.NotLoaded, default);
        if (_sfxBackend is not ISfxLoopBackend backend) return new(SfxLoopStartStatus.Unsupported, default);

        ReconcileLoopMix();
        float volume = Math.Clamp(request.Volume, 0f, 1f);
        float gain = LoopGain(request.Bus, volume);
        var playback = new SfxLoopPlayback(buffer, gain, request.Pitch, request.Position.HasValue,
            request.Position.GetValueOrDefault(), ResolveBusAttenuation(request.Bus));
        try
        {
            var result = backend.TryStartLoop(playback);
            if (result.Status != SfxLoopStartStatus.Started) return new(result.Status, default);
            _loopMix.Add(result.Handle, new LoopMix
            {
                Bus = request.Bus,
                Volume = volume,
                Position = request.Position,
                DesiredGain = gain,
                AppliedGain = gain
            });
            return new(SfxLoopStartStatus.Started, new(_loopOwner, result.Handle));
        }
        catch (Exception exception)
        {
            WarnLoopFailure("starting an SFX loop failed.", exception);
            return default;
        }
    }

    /// <summary>Stops this system's live identity. Failed cleanup can be retried explicitly.</summary>
    public bool StopLoop(SfxLoopHandle handle)
    {
        EnsureOwningThread();
        if (_disposed || !TryGetLoop(handle, out _)) return false;
        try
        {
            if (!((ISfxLoopBackend)_sfxBackend).StopLoop(handle.BackendHandle)) return false;
            _loopMix.Remove(handle.BackendHandle);
            return true;
        }
        catch (Exception exception)
        {
            WarnLoopFailure("stopping an SFX loop failed.", exception);
            return false;
        }
    }

    /// <summary>
    /// Changes local volume without replaying. A failed gain write keeps the desired volume for the next update.
    /// Non-finite volume is rejected and finite volume is clamped to 0..1.
    /// </summary>
    public bool SetLoopVolume(SfxLoopHandle handle, float volume)
    {
        EnsureOwningThread();
        if (_disposed) return false;
        ValidateLoopVolume(volume);
        if (!TryGetLoop(handle, out var mix)) return false;
        mix.Volume = Math.Clamp(volume, 0f, 1f);
        mix.DesiredGain = LoopGain(mix.Bus, mix.Volume);
        return ApplyLoopGain(handle.BackendHandle, mix);
    }

    /// <summary>Moves a positional loop. Flat, foreign and expired identities return false.</summary>
    public bool SetLoopPosition(SfxLoopHandle handle, Vector3 position)
    {
        EnsureOwningThread();
        if (_disposed) return false;
        ValidateLoopPosition(position);
        if (!TryGetLoop(handle, out var mix) || !mix.Position.HasValue) return false;
        try
        {
            if (!((ISfxLoopBackend)_sfxBackend).SetLoopPosition(handle.BackendHandle, position)) return false;
            mix.Position = position;
            return true;
        }
        catch (Exception exception)
        {
            WarnLoopFailure("moving an SFX loop failed.", exception);
            return false;
        }
    }

    /// <summary>Queries this system's identity against backend ownership.</summary>
    public bool IsLoopActive(SfxLoopHandle handle)
    {
        EnsureOwningThread();
        return !_disposed && TryGetLoop(handle, out _);
    }

    /// <summary>Stops all SFX, including protected loops, without affecting music.</summary>
    public void StopAllSfx()
    {
        EnsureOwningThread();
        if (_disposed) return;
        try { _sfxBackend.StopAll(); }
        finally { ReconcileLoopMix(); }
    }

    bool TryGetLoop(SfxLoopHandle handle, out LoopMix mix)
    {
        mix = null!;
        if (!ReferenceEquals(handle.Owner, _loopOwner) || !_loopMix.TryGetValue(handle.BackendHandle, out var found)) return false;
        if (!TryQueryLoop(handle.BackendHandle, out bool active)) return false;
        if (!active)
        {
            _loopMix.Remove(handle.BackendHandle);
            return false;
        }
        mix = found;
        return true;
    }

    bool TryQueryLoop(SfxLoopBackendHandle handle, out bool active)
    {
        active = false;
        try
        {
            active = ((ISfxLoopBackend)_sfxBackend).IsLoopActive(handle);
            return true;
        }
        catch (Exception exception)
        {
            WarnLoopFailure("querying an SFX loop failed.", exception);
            return false;
        }
    }

    void RefreshLoopMix()
    {
        if (_disposed) return;
        ReconcileLoopMix();
        foreach (var (handle, mix) in _loopMix)
        {
            mix.DesiredGain = LoopGain(mix.Bus, mix.Volume);
            if (TryQueryLoop(handle, out bool active) && active) ApplyLoopGain(handle, mix);
        }
    }

    void UpdateLoopMix()
    {
        if (_disposed) return;
        ReconcileLoopMix();
        foreach (var (handle, mix) in _loopMix)
            if (TryQueryLoop(handle, out bool active) && active) ApplyLoopGain(handle, mix);
    }

    // A failed query retains the mixer record. Only confirmed ended identities are forgotten.
    void ReconcileLoopMix()
    {
        if (_disposed) return;
        foreach (var handle in _loopMix.Keys)
            if (TryQueryLoop(handle, out bool active) && !active) _endedLoops.Add(handle);
        foreach (var handle in _endedLoops) _loopMix.Remove(handle);
        _endedLoops.Clear();
    }

    bool ApplyLoopGain(SfxLoopBackendHandle handle, LoopMix mix)
    {
        if (!mix.GainDirty && mix.DesiredGain == mix.AppliedGain) return true;
        // A failed backend write may have changed the device before reporting failure.
        mix.GainDirty = true;
        try
        {
            if (!((ISfxLoopBackend)_sfxBackend).SetLoopGain(handle, mix.DesiredGain)) return false;
            mix.AppliedGain = mix.DesiredGain;
            mix.GainDirty = false;
            return true;
        }
        catch (Exception exception)
        {
            WarnLoopFailure("applying SFX loop gain failed.", exception);
            return false;
        }
    }

    float LoopGain(string? bus, float volume)
    {
        float gain = _masterVolume * _sfxVolume * GetBusVolume(bus!) * volume;
        return float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 0f;
    }

    static void ValidateLoopVolume(float volume)
    {
        if (!float.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
    }

    static void ValidateLoopPosition(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentOutOfRangeException(nameof(position));
    }

    void WarnLoopFailure(string message, Exception exception)
    {
        try { _logger.Warn(message, exception); }
        catch { /* Diagnostics must not interrupt loop ownership or gain recovery. */ }
    }
}
