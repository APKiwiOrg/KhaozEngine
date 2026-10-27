using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>
/// No-op SFX backend used when no audio device / OpenAL implementation is available (headless servers, CI,
/// machines without sound). <see cref="Load"/> returns an incrementing non-negative handle so callers see
/// "valid" handles and their name maps stay populated; everything else is silent. Keeps the SFX API usable
/// without throwing.
/// </summary>
public sealed partial class NullSfxBackend : ISfxBackend
{
    int _nextHandle;
    readonly HashSet<int> _loaded = new();
    bool _disposed;

    public string Name => "Null (no audio)";

    public int Load(string path)
    {
        if (_disposed) return -1;
        int handle = _nextHandle++;
        _loaded.Add(handle);
        return handle;
    }

    public void Play(int handle, float gain, float pitch, bool positional, Vector3 position) { }

    public void Play(
        int handle,
        float gain,
        float pitch,
        bool positional,
        Vector3 position,
        SfxPriority priority,
        SfxAttenuation attenuation)
    { }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up) { }

    public void StopAll() => _loops.StopAllLoops();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loops.Dispose();
        _loaded.Clear();
    }
}
