using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Audio.Decoding;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// OpenAL one-shot SFX backend (Silk.NET.OpenAL) sharing the per-process <see cref="OpenAlContext"/> with the
/// music backend. Whole-file decodes short sounds into single buffers and plays them on a fixed pool of
/// sources, optionally positioned in 3D relative to a listener. Voices are reclaimed lazily: each
/// <see cref="Play(int, float, float, bool, Vector3, SfxPriority)"/> prefers a genuinely idle source, and when
/// all are busy it steals the LOWEST-priority voice still playing (<see cref="SfxVoicePool.Steal"/>), with the
/// round-robin rotation as the tie-break.
/// </summary>
internal sealed partial class OpenAlSfxBackend : ISfxBackend
{
    const int VoiceCount = 16;
    readonly ISfxAlCommands _commands;
    readonly SfxAlCommandBatch _native;
    readonly uint[] _voices = new uint[VoiceCount];
    // What each voice is currently playing, by voice index. Only read when every voice is busy, so a stale
    // entry left behind by a finished sound is never consulted: an idle voice is always preferred first.
    readonly SfxPriority[] _voicePriorities = new SfxPriority[VoiceCount];
    readonly SfxVoicePool _pool = new(VoiceCount);
    readonly List<uint> _buffers = new();
    bool _disposed;

    public string Name => "OpenAL";

    public OpenAlSfxBackend(OpenAlContext context, ILogger? logger = null)
        : this(new SilkSfxAlCommands(context.Al), logger) { }

    internal OpenAlSfxBackend(ISfxAlCommands commands, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        logger ??= Log.For<OpenAlSfxBackend>();
        _native = new SfxAlCommandBatch(commands, logger);
        _loopDevice = new OpenAlSfxLoopDevice(commands, _buffers, _native);
        _loops = new SfxLoopPool(SfxLoopPool.StockCapacity, _loopDevice, logger);
        for (int i = 0; i < VoiceCount; i++) _voices[i] = _commands.GenSource();
    }

    public int Load(string path)
    {
        if (_disposed) return -1;
        try
        {
            using var decoder = PcmDecoders.Open(path);
            var format = decoder.Channels >= 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;

            // Whole-file decode: SFX are short. Grow a single PCM array until the stream is exhausted.
            short[] data = new short[16384];
            int total = 0;
            while (true)
            {
                if (total == data.Length) Array.Resize(ref data, data.Length * 2);
                int read = decoder.ReadSamples(data, total, data.Length - total);
                if (read <= 0) break;
                total += read;
            }

            uint buffer = _commands.GenBuffer();
            _commands.BufferData(buffer, format, data.AsSpan(0, total), decoder.SampleRate);
            _native.Check("SFX BufferData", _commands.GetError());

            // Take a slot an Unload freed before growing the list, so a zone-scoped load/unload cycle reuses
            // handles instead of walking the index up for the whole session.
            int slot = _buffers.IndexOf(0);
            if (slot >= 0) { _buffers[slot] = buffer; return slot; }
            _buffers.Add(buffer);
            return _buffers.Count - 1;
        }
        catch (Exception ex)
        {
            _native.ReportException("load SFX " + path, ex);
            return -1;
        }
    }

    public void Unload(int handle) => TryUnloadSfx(handle);

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position)
        => Play(handle, gain, pitch, positional, position, SfxPriority.Normal);

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position, SfxPriority priority)
        => Play(handle, gain, pitch, positional, position, priority, SfxAttenuation.Default);

    public void Play(
        int handle,
        float gain,
        float pitch,
        bool positional,
        Vector3 position,
        SfxPriority priority,
        SfxAttenuation attenuation)
    {
        if (_disposed || handle < 0 || handle >= _buffers.Count) return;
        uint bufferId = _buffers[handle];
        if (bufferId == 0) return;   // released by Unload; the handle is dead

        int voice = PickVoice();
        _voicePriorities[voice] = priority;
        uint source = _voices[voice];

        _commands.SourceStop(source);
        _commands.BindBuffer(source, bufferId);
        _commands.SetSourceProperty(source, SourceFloat.Gain, gain);
        _commands.SetSourceProperty(source, SourceFloat.Pitch, MathF.Max(0.01f, pitch));

        if (positional)
        {
            _commands.SetSourceProperty(source, SourceBoolean.SourceRelative, false);
            _commands.SetSourceProperty(source, SourceVector3.Position, position);
            _commands.SetSourceProperty(source, SourceFloat.ReferenceDistance, attenuation.ReferenceDistance);
            _commands.SetSourceProperty(source, SourceFloat.RolloffFactor, attenuation.RolloffFactor);
            _commands.SetSourceProperty(source, SourceFloat.MaxDistance, attenuation.MaxDistance);
        }
        else
        {
            // Relative to the listener at the origin: heard at full gain regardless of listener pose.
            _commands.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
            _commands.SetSourceProperty(source, SourceVector3.Position, Vector3.Zero);
        }

        _commands.SourcePlay(source);
        _native.Check("SFX SourcePlay", _commands.GetError());
    }

    // Prefer a genuinely idle source. When every voice is busy, steal the LOWEST-priority one still playing
    // rather than whatever the rotation landed on, so a barrage of footsteps cannot cut a boss cue mid-play
    // (issue #114). Rotation survives as the tie-break inside SfxVoicePool.Steal, so equal-priority voices are
    // still taken oldest-first, which is the pre-priority behaviour for a game that never states one.
    int PickVoice()
    {
        for (int i = 0; i < VoiceCount; i++)
        {
            _commands.GetSourceProperty(_voices[i], GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) return i;
        }
        return _pool.Steal(_voicePriorities);
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        if (!_disposed) _commands.SetListener(position, forward, up);
    }

    public void StopAll()
    {
        if (_disposed) return;
        _loops.StopAllLoops();
        for (int i = 0; i < VoiceCount; i++)
        {
            uint source = _voices[i];
            _native.TryRun("SFX stop all", () => _commands.SourceStop(source));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loops.Dispose();
        for (int i = 0; i < VoiceCount; i++)
        {
            uint source = _voices[i];
            if (source == 0) continue;
            _native.TryRun("SFX dispose stop", () => _commands.SourceStop(source));
            if (_native.TryRun("SFX DeleteSource", () => _commands.DeleteSource(source))) _voices[i] = 0;
        }
        // A failed source deletion can leave a buffer attached. Keep buffers for context teardown rather
        // than issuing unsafe deletes or allocating replacement sources after this terminal failure.
        if (!_loopDevice.HasSources && !Array.Exists(_voices, source => source != 0))
        {
            foreach (uint buffer in _buffers)
                if (buffer != 0) _native.TryRun("SFX dispose buffer", () => _commands.DeleteBuffer(buffer));
            _buffers.Clear();
        }
        // Does NOT dispose the shared context: AudioSystem owns it.
    }
}
