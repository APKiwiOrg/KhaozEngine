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
/// <see cref="Play"/> prefers a genuinely idle source and only falls back to the round-robin
/// <see cref="SfxVoicePool"/> when all are busy.
/// </summary>
internal sealed unsafe class OpenAlSfxBackend : ISfxBackend
{
    const int VoiceCount = 16;
    // Sane positional-attenuation defaults applied per source at play time (Silk's AL surface has no
    // SetDistanceModel; the inverse-distance default model uses these per-source distances).
    const float ReferenceDistance = 1f;
    const float RolloffFactor = 1f;
    const float MaxDistance = 50f;

    readonly ILogger _logger;
    readonly AlErrorLog _errors;
    readonly AL _al;
    readonly uint[] _voices = new uint[VoiceCount];
    readonly SfxVoicePool _pool = new(VoiceCount);
    readonly List<uint> _buffers = new();
    bool _disposed;

    public string Name => "OpenAL";

    public OpenAlSfxBackend(OpenAlContext context, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<OpenAlSfxBackend>();
        _errors = new AlErrorLog(_logger);
        _al = context.Al;
        for (int i = 0; i < VoiceCount; i++) _voices[i] = _al.GenSource();
    }

    public int Load(string path)
    {
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

            uint buffer = _al.GenBuffer();
            fixed (short* p = data)
                _al.BufferData(buffer, format, p, total * sizeof(short), decoder.SampleRate);
            _errors.Check("SFX BufferData", _al.GetError());

            // Take a slot an Unload freed before growing the list, so a zone-scoped load/unload cycle reuses
            // handles instead of walking the index up for the whole session.
            int slot = _buffers.IndexOf(0);
            if (slot >= 0) { _buffers[slot] = buffer; return slot; }
            _buffers.Add(buffer);
            return _buffers.Count - 1;
        }
        catch (Exception ex)
        {
            _logger.Error("OpenAL: failed to load SFX " + path, ex);
            return -1;
        }
    }

    public void Unload(int handle)
    {
        if (handle < 0 || handle >= _buffers.Count) return;
        uint buffer = _buffers[handle];
        if (buffer == 0) return;   // already released
        // AL refuses to delete a buffer still attached to a source, so detach it from every voice holding it
        // before freeing. Zeroing the slot leaves it for the next Load rather than stranding the index.
        for (int i = 0; i < VoiceCount; i++)
        {
            _al.GetSourceProperty(_voices[i], GetSourceInteger.Buffer, out int bound);
            if ((uint)bound != buffer) continue;
            _al.SourceStop(_voices[i]);
            _al.SetSourceProperty(_voices[i], SourceInteger.Buffer, 0);
        }
        _al.DeleteBuffer(buffer);
        _errors.Check("SFX DeleteBuffer", _al.GetError());
        _buffers[handle] = 0;
    }

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position)
    {
        if (handle < 0 || handle >= _buffers.Count) return;
        uint bufferId = _buffers[handle];
        if (bufferId == 0) return;   // released by Unload; the handle is dead

        uint source = PickVoice();

        _al.SourceStop(source);
        _al.SetSourceProperty(source, SourceInteger.Buffer, bufferId);
        _al.SetSourceProperty(source, SourceFloat.Gain, gain);
        _al.SetSourceProperty(source, SourceFloat.Pitch, MathF.Max(0.01f, pitch));

        if (positional)
        {
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, false);
            _al.SetSourceProperty(source, SourceVector3.Position, position);
            _al.SetSourceProperty(source, SourceFloat.ReferenceDistance, ReferenceDistance);
            _al.SetSourceProperty(source, SourceFloat.RolloffFactor, RolloffFactor);
            _al.SetSourceProperty(source, SourceFloat.MaxDistance, MaxDistance);
        }
        else
        {
            // Relative to the listener at the origin: heard at full gain regardless of listener pose.
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(source, SourceVector3.Position, Vector3.Zero);
        }

        _al.SourcePlay(source);
        _errors.Check("SFX SourcePlay", _al.GetError());
    }

    // Prefer a genuinely idle source; only steal in round-robin rotation when every voice is busy.
    uint PickVoice()
    {
        for (int i = 0; i < VoiceCount; i++)
        {
            _al.GetSourceProperty(_voices[i], GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) return _voices[i];
        }
        return _voices[_pool.Next()];
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        _al.SetListenerProperty(ListenerVector3.Position, position);
        float* orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
    }

    public void StopAll()
    {
        for (int i = 0; i < VoiceCount; i++) _al.SourceStop(_voices[i]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < VoiceCount; i++)
        {
            if (_voices[i] != 0) { _al.SourceStop(_voices[i]); _al.DeleteSource(_voices[i]); }
        }
        foreach (uint b in _buffers) if (b != 0) _al.DeleteBuffer(b);
        _buffers.Clear();
        // Does NOT dispose the shared context: AudioSystem owns it.
    }
}
