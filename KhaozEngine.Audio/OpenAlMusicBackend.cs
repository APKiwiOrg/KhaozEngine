using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Audio.Decoding;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// Cross-platform OpenAL streaming music backend (Silk.NET.OpenAL). Decodes WAV/OGG/MP3 incrementally and
/// keeps a small ring of queued buffers topped up in <see cref="Update"/>. One track at a time; when a
/// track's stream is exhausted it stops and <see cref="IsPlaying"/> goes false (AudioSystem then rotates).
/// </summary>
public sealed unsafe class OpenAlMusicBackend : IMusicBackend
{
    const int NumBuffers = 4;
    const int ChunkSamples = 16384; // per buffer fill (interleaved)
    static readonly string[] Extensions = { ".ogg", ".mp3", ".wav" };

    readonly ILogger _logger;
    readonly OpenAlContext _ctx;
    readonly bool _ownsContext;
    readonly AL _al;
    readonly List<string> _tracks = new();
    readonly short[] _scratch = new short[ChunkSamples];
    readonly uint[] _one = new uint[1];   // reused 1-element scratch for span-less Queue/UnqueueBuffers (zero-alloc steady state)

    uint _source;
    uint[] _buffers = Array.Empty<uint>();
    IPcmDecoder? _decoder;
    BufferFormat _format;
    int _sampleRate;
    bool _streamEnded;
    bool _playing;
    float _volume = 1f;

    public string Name => "OpenAL";
    public int TrackCount => _tracks.Count;
    public bool IsPlaying => _playing;

    /// <summary>
    /// Creates a music backend that owns its own <see cref="OpenAlContext"/> (back-compat for any direct
    /// consumer). Throws if no audio device is available. Prefer the shared-context overload when running
    /// alongside the SFX backend so both live in the single per-process OpenAL context.
    /// </summary>
    public OpenAlMusicBackend(ILogger? logger = null)
        : this(new OpenAlContext(), ownsContext: true, logger)
    {
    }

    /// <summary>
    /// Creates a music backend that borrows a shared <see cref="OpenAlContext"/> (does not dispose it). Used by
    /// <see cref="AudioSystem"/> so music and SFX share the one per-process OpenAL context.
    /// </summary>
    internal OpenAlMusicBackend(OpenAlContext context, ILogger? logger = null)
        : this(context, ownsContext: false, logger)
    {
    }

    OpenAlMusicBackend(OpenAlContext context, bool ownsContext, ILogger? logger)
    {
        _logger = logger ?? Log.For<OpenAlMusicBackend>();
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _ownsContext = ownsContext;
        _al = context.Al;
    }

    public bool TryLoadTrack(string contentDirectory, string trackName)
    {
        foreach (var ext in Extensions)
        {
            string path = Path.Combine(contentDirectory, trackName + ext);
            if (File.Exists(path)) { _tracks.Add(path); return true; }
        }
        _logger.Warn($"OpenAL: no WAV/OGG/MP3 file found for track '{trackName}' in '{contentDirectory}'.");
        return false;
    }

    public bool TryPlayTrack(int trackIndex, float volume)
    {
        if (trackIndex < 0 || trackIndex >= _tracks.Count) return false;
        Stop();
        try
        {
            _decoder = PcmDecoders.Open(_tracks[trackIndex]);
            _format = _decoder.Channels >= 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            _sampleRate = _decoder.SampleRate;
            _streamEnded = false;
            _volume = volume;

            _source = _al.GenSource();
            _buffers = new uint[NumBuffers];
            int queued = 0;
            for (int i = 0; i < NumBuffers; i++)
            {
                _buffers[i] = _al.GenBuffer();
                if (Fill(_buffers[i])) { _one[0] = _buffers[i]; _al.SourceQueueBuffers(_source, _one); queued++; }
            }
            if (queued == 0) { Stop(); return false; }

            _al.SetSourceProperty(_source, SourceFloat.Gain, volume);
            _al.SourcePlay(_source);
            _playing = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("OpenAL: failed to play track " + _tracks[trackIndex], ex);
            Stop();
            return false;
        }
    }

    public void Update()
    {
        if (!_playing) return;

        try
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            for (int i = 0; i < processed; i++)
            {
                _al.SourceUnqueueBuffers(_source, _one);
                if (!_streamEnded && Fill(_one[0])) _al.SourceQueueBuffers(_source, _one);
            }

            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (_streamEnded && queued == 0) { StopCleanFinish(); return; }   // track finished

            // Resume if the source underran while buffers are still queued.
            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if (queued > 0 && state != (int)SourceState.Playing) _al.SourcePlay(_source);
        }
        catch (Exception ex)
        {
            // A corrupt or truncated file makes the decoder throw partway through playback (e.g. an
            // EndOfStreamException from a data chunk that outruns the file). Stop this track cleanly and go
            // quiet, the same way TryPlayTrack guards the initial fill, instead of letting it crash the frame
            // loop. IsPlaying then reads false, so AudioSystem rotates to the next track.
            _logger.Error("OpenAL: streaming refill failed, stopping the current track.", ex);
            Stop();
        }
    }

    // Stops a track that finished cleanly (stream exhausted, nothing left queued). Deliberately outside the
    // refill-failure catch above: that catch is for a decoder/AL failure DURING playback, not for the
    // clean-finish Stop() itself, so a throw here gets its own label instead of being logged as "streaming
    // refill failed". It also calls Stop() at most once - Stop() is guarded by `_source != 0`, so a second
    // call after a completed one is a no-op, but a Stop() that throws PARTWAY through (before `_source = 0`
    // is reached) would otherwise get re-invoked from the shared catch and re-touch already-torn-down OpenAL
    // handles (issue #210).
    void StopCleanFinish()
    {
        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("OpenAL: Stop() failed while finishing a track that reached end of stream.", ex);
        }
    }

    public void Stop()
    {
        if (_source != 0)
        {
            _al.SourceStop(_source);
            _al.SetSourceProperty(_source, SourceInteger.Buffer, 0); // detach all queued buffers
            foreach (var b in _buffers) if (b != 0) _al.DeleteBuffer(b);
            _al.DeleteSource(_source);
            _source = 0;
            _buffers = Array.Empty<uint>();
        }
        _decoder?.Dispose();
        _decoder = null;
        _playing = false;
    }

    public void SetVolume(float volume)
    {
        _volume = volume;
        if (_source != 0) _al.SetSourceProperty(_source, SourceFloat.Gain, volume);
    }

    bool Fill(uint buffer)
    {
        if (_decoder == null) return false;
        int read = _decoder.ReadSamples(_scratch, 0, ChunkSamples);
        if (read <= 0) { _streamEnded = true; return false; }
        fixed (short* p = _scratch)
            _al.BufferData(buffer, _format, p, read * sizeof(short), _sampleRate);
        return true;
    }

    public void Dispose()
    {
        Stop();
        // Only tear down a context we created. A shared context is owned and disposed by AudioSystem.
        if (_ownsContext) _ctx.Dispose();
    }
}
