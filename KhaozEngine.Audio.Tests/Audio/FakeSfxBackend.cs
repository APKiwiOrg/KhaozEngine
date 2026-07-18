using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Audio;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="ISfxBackend"/> recording calls for headless AudioSystem SFX tests.</summary>
internal sealed class FakeSfxBackend : ISfxBackend
{
    public readonly record struct PlayCall(int Handle, float Gain, float Pitch, bool Positional, Vector3 Position);
    public readonly record struct ListenerCall(Vector3 Position, Vector3 Forward, Vector3 Up);

    public List<string> LoadedPaths { get; } = new();
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

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position)
        => Plays.Add(new PlayCall(handle, gain, pitch, positional, position));

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
        => Listeners.Add(new ListenerCall(position, forward, up));

    public void StopAll() => StopAllCount++;

    public void Dispose() => Disposed = true;
}
