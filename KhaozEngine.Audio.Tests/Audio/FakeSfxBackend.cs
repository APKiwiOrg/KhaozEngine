using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Audio;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="ISfxBackend"/> recording calls for headless AudioSystem SFX tests.</summary>
internal sealed class FakeSfxBackend : ISfxBackend
{
    public readonly record struct PlayCall(
        int Handle,
        float Gain,
        float Pitch,
        bool Positional,
        Vector3 Position,
        SfxAttenuation? Attenuation = null);
    public readonly record struct ListenerCall(Vector3 Position, Vector3 Forward, Vector3 Up);

    public List<string> LoadedPaths { get; } = new();
    public List<int> Unloaded { get; } = new();
    public List<PlayCall> Plays { get; } = new();
    public List<ListenerCall> Listeners { get; } = new();
    public int StopAllCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Paths whose load should return -1 (failure).</summary>
    public HashSet<string> FailPaths { get; } = new();

    private int _nextHandle;

    public string Name => "Fake";

    public int Load(string path)
    {
        LoadedPaths.Add(path);
        if (FailPaths.Contains(path)) return -1;
        return _nextHandle++;
    }

    public void Unload(int handle) => Unloaded.Add(handle);

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position)
        => Plays.Add(new PlayCall(handle, gain, pitch, positional, position));

    /// <summary>Records the stated priority alongside the play, index-aligned to <see cref="Plays"/>. Kept as a
    /// parallel list rather than a field on <see cref="PlayCall"/> so every existing gain / position assertion
    /// keeps reading exactly what it read before priorities existed (#114).</summary>
    public List<SfxPriority> PlayPriorities { get; } = new();

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position, SfxPriority priority)
    {
        PlayPriorities.Add(priority);
        Play(handle, gain, pitch, positional, position);
    }

    public void Play(
        int handle,
        float gain,
        float pitch,
        bool positional,
        Vector3 position,
        SfxPriority priority,
        SfxAttenuation attenuation)
    {
        PlayPriorities.Add(priority);
        Plays.Add(new PlayCall(handle, gain, pitch, positional, position, attenuation));
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
        => Listeners.Add(new ListenerCall(position, forward, up));

    public void StopAll() => StopAllCount++;

    public void Dispose() => Disposed = true;
}
