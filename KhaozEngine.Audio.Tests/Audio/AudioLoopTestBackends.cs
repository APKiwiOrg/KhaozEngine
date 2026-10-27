using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Tests.Audio;

internal sealed class RecordingLoopBackend : ISfxBackend, ISfxLoopBackend
{
    readonly SfxLoopPool _pool;
    int _nextBuffer;

    public RecordingLoopBackend(ILogger? logger = null) => _pool = new(4, Device, logger);

    public ScriptedLoopDevice Device { get; } = new();
    public HashSet<int> LoadedBuffers { get; } = new();
    public List<float> OneShotGains { get; } = new();
    public List<string>? DisposalOrder { get; init; }
    public Exception? DisposeFailure { get; set; }
    public Action? OnDispose { get; set; }
    public int DisposeCount { get; private set; }
    public int IdentityQueries { get; private set; }
    public List<(string Operation, SfxLoopBackendHandle Handle)> CompanionCalls { get; } = new();
    public Action<string, SfxLoopBackendHandle>? BeforeLoopCall { get; set; }
    public string Name => "recording-loop";
    public int LoopCapacity => _pool.Capacity;

    public int Load(string path)
    {
        int buffer = _nextBuffer++;
        LoadedBuffers.Add(buffer);
        return buffer;
    }

    public void Unload(int handle) => TryUnloadSfx(handle);
    public bool TryUnloadSfx(int bufferHandle)
    {
        if (!LoadedBuffers.Contains(bufferHandle) || !_pool.StopBufferLoops(bufferHandle)) return false;
        return LoadedBuffers.Remove(bufferHandle);
    }

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position) => OneShotGains.Add(gain);
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up) { }
    public void StopAll() => _pool.StopAllLoops();
    public SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback)
    {
        OnLoopCall("start", default);
        return LoadedBuffers.Contains(playback.Buffer) ? _pool.TryStartLoop(playback) : new(SfxLoopStartStatus.NotLoaded, default);
    }
    public bool StopLoop(SfxLoopBackendHandle handle)
    {
        OnLoopCall("stop", handle);
        return _pool.StopLoop(handle);
    }
    public bool SetLoopGain(SfxLoopBackendHandle handle, float gain)
    {
        OnLoopCall("gain", handle);
        return _pool.SetLoopGain(handle, gain);
    }
    public bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position)
    {
        OnLoopCall("position", handle);
        return _pool.SetLoopPosition(handle, position);
    }
    public bool IsLoopActive(SfxLoopBackendHandle handle)
    {
        IdentityQueries++;
        OnLoopCall("query", handle);
        return _pool.IsLoopActive(handle);
    }

    void OnLoopCall(string operation, SfxLoopBackendHandle handle)
    {
        CompanionCalls.Add((operation, handle));
        BeforeLoopCall?.Invoke(operation, handle);
    }

    public void Dispose()
    {
        DisposeCount++;
        DisposalOrder?.Add("sfx");
        OnDispose?.Invoke();
        _pool.Dispose();
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}

internal sealed class DisposalMusicBackend : IMusicBackend
{
    public List<string> DisposalOrder { get; init; } = new();
    public Exception? DisposeFailure { get; init; }
    public int DisposeCount { get; private set; }
    public string Name => "disposal-music";
    public int TrackCount => 0;
    public bool IsPlaying => false;
    public bool TryLoadTrack(string contentDirectory, string trackName) => false;
    public bool TryPlayTrack(int trackIndex, float volume) => false;
    public void Stop() { }
    public void SetVolume(float volume) { }
    public void Update() { }
    public void Dispose()
    {
        DisposeCount++;
        DisposalOrder.Add("music");
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}

internal sealed class AudioLoopLogger : ILogger
{
    public List<Exception?> Warnings { get; } = new();
    public bool ThrowOnWarning { get; init; }
    public bool ThrowOnDebug { get; init; }
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
    public string Category => "audio-loop-test";
    public bool IsEnabled(LogLevel level) => true;
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        Entries.Add((level, message, exception));
        if (level == LogLevel.Debug && ThrowOnDebug) throw new InvalidOperationException("scripted debug diagnostics failure");
        if (level != LogLevel.Warn) return;
        Warnings.Add(exception);
        if (ThrowOnWarning) throw new InvalidOperationException("scripted diagnostics failure");
    }
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
    public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
