using System.Numerics;

namespace KhaozEngine.Audio;

public sealed partial class NullSfxBackend : ISfxLoopBackend
{
    readonly SfxLoopPool _loops = new(SfxLoopPool.StockCapacity, new NullSfxLoopDevice());

    public int LoopCapacity => _loops.Capacity;

    public SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback)
    {
        if (_disposed) return new(SfxLoopStartStatus.Disposed, default);
        if (!_loaded.Contains(playback.Buffer)) return new(SfxLoopStartStatus.NotLoaded, default);
        return _loops.TryStartLoop(playback);
    }

    public bool StopLoop(SfxLoopBackendHandle handle) => _loops.StopLoop(handle);
    public bool SetLoopGain(SfxLoopBackendHandle handle, float gain) => _loops.SetLoopGain(handle, gain);
    public bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position) => _loops.SetLoopPosition(handle, position);
    public bool IsLoopActive(SfxLoopBackendHandle handle) => _loops.IsLoopActive(handle);

    public void Unload(int handle) => TryUnloadSfx(handle);

    public bool TryUnloadSfx(int bufferHandle)
    {
        if (_disposed) return false;
        if (!_loaded.Contains(bufferHandle)) return true;
        if (!_loops.StopBufferLoops(bufferHandle)) return false;
        _loaded.Remove(bufferHandle);
        return true;
    }
}

internal sealed class NullSfxLoopDevice : ISfxLoopDevice
{
    public bool TryStart(int slot, in SfxLoopPlayback playback) => true;
    public bool TryStop(int slot) => true;
    public bool TrySetGain(int slot, float gain) => true;
    public bool TrySetPosition(int slot, Vector3 position) => true;
    public void Dispose() { }
}
