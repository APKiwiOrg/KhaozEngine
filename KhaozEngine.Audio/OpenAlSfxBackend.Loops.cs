using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

internal sealed partial class OpenAlSfxBackend : ISfxLoopBackend
{
    readonly OpenAlSfxLoopDevice _loopDevice;
    readonly SfxLoopPool _loops;

    public int LoopCapacity => _loops.Capacity;

    public SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback)
    {
        if (_disposed) return new(SfxLoopStartStatus.Disposed, default);
        if (!TryGetBuffer(playback.Buffer, out _)) return new(SfxLoopStartStatus.NotLoaded, default);
        return _loops.TryStartLoop(playback);
    }

    public bool StopLoop(SfxLoopBackendHandle handle) => _loops.StopLoop(handle);
    public bool SetLoopGain(SfxLoopBackendHandle handle, float gain) => _loops.SetLoopGain(handle, gain);
    public bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position) => _loops.SetLoopPosition(handle, position);
    public bool IsLoopActive(SfxLoopBackendHandle handle) => _loops.IsLoopActive(handle);

    public bool TryUnloadSfx(int bufferHandle)
    {
        if (_disposed) return false;
        if (!TryGetBuffer(bufferHandle, out uint buffer)) return true;

        bool released = _loops.StopBufferLoops(bufferHandle);
        for (int i = 0; i < VoiceCount; i++)
        {
            uint source = _voices[i];
            int bound = 0;
            if (!_native.TryRun("SFX query buffer", () => _commands.GetSourceProperty(source, GetSourceInteger.Buffer, out bound)))
            {
                released = false;
                continue;
            }
            if ((uint)bound != buffer) continue;
            if (!_native.TryRun("SFX unload stop", () => _commands.SourceStop(source)))
            {
                released = false;
                continue;
            }
            released &= _native.TryRun("SFX unload detach", () => _commands.BindBuffer(source, 0));
        }
        // Retain the registration on partial cleanup so an explicit unload can finish it later.
        if (!released || !_native.TryRun("SFX DeleteBuffer", () => _commands.DeleteBuffer(buffer))) return false;
        _buffers[bufferHandle] = 0;
        return true;
    }

    bool TryGetBuffer(int handle, out uint buffer)
    {
        buffer = handle >= 0 && handle < _buffers.Count ? _buffers[handle] : 0;
        return buffer != 0;
    }
}

/// <summary>Native storage for pool-assigned slots, separate from the sixteen one-shot sources.</summary>
internal sealed class OpenAlSfxLoopDevice(
    ISfxAlCommands commands, List<uint> buffers, SfxAlCommandBatch native) : ISfxLoopDevice
{
    readonly uint[] _sources = new uint[SfxLoopPool.StockCapacity];

    public bool HasSources => Array.Exists(_sources, source => source != 0);

    public bool TryStart(int slot, in SfxLoopPlayback playback)
    {
        native.Begin("SFX loop start");
        if (_sources[slot] == 0)
        {
            // Keep any returned name even on error. Only confirmed deletion may release its storage.
            _sources[slot] = commands.GenSource();
            if (!native.Complete("SFX loop source") || _sources[slot] == 0) return false;
        }
        uint source = _sources[slot];
        commands.BindBuffer(source, buffers[playback.Buffer]);
        commands.SetSourceProperty(source, SourceBoolean.Looping, true);
        commands.SetSourceProperty(source, SourceFloat.Gain, playback.Gain);
        commands.SetSourceProperty(source, SourceFloat.Pitch, MathF.Max(0.01f, playback.Pitch));
        commands.SetSourceProperty(source, SourceBoolean.SourceRelative, !playback.Positional);
        commands.SetSourceProperty(source, SourceVector3.Position, playback.Positional ? playback.Position : Vector3.Zero);
        commands.SetSourceProperty(source, SourceFloat.ReferenceDistance, playback.Attenuation.ReferenceDistance);
        commands.SetSourceProperty(source, SourceFloat.RolloffFactor, playback.Attenuation.RolloffFactor);
        commands.SetSourceProperty(source, SourceFloat.MaxDistance, playback.Attenuation.MaxDistance);
        if (!native.Complete("SFX loop configure")) return false;
        commands.SourcePlay(source);
        return native.Complete("SFX loop play");
    }

    public bool TryStop(int slot)
    {
        uint source = _sources[slot];
        if (source == 0) return true;
        native.Begin("SFX loop stop");
        commands.SourceStop(source);
        if (!native.Complete("SFX loop stop")) return false;
        commands.BindBuffer(source, 0);
        return native.Complete("SFX loop detach");
    }

    // As with start and stop, let the pool own exception diagnostics per slot and operation. A healthy
    // peer must not reset a failing loop's episode through the shared native batch helper.
    public bool TrySetGain(int slot, float gain)
    {
        native.Begin("SFX loop gain");
        commands.SetSourceProperty(_sources[slot], SourceFloat.Gain, gain);
        return native.Complete("SFX loop gain");
    }

    public bool TrySetPosition(int slot, Vector3 position)
    {
        native.Begin("SFX loop position");
        commands.SetSourceProperty(_sources[slot], SourceVector3.Position, position);
        return native.Complete("SFX loop position");
    }

    public void Dispose()
    {
        for (int i = 0; i < _sources.Length; i++)
        {
            uint source = _sources[i];
            // Deleting a source also stops and detaches it, even if the pool's final stop failed.
            if (source != 0 && native.TryRun("SFX loop DeleteSource", () => commands.DeleteSource(source)))
                _sources[i] = 0;
        }
    }
}
