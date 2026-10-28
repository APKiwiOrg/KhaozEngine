using System.Numerics;

namespace KhaozEngine.Audio;

/// <summary>
/// Optional protected-loop capability, separate from the existing one-shot backend contract.
/// Implementations are driven on the owning audio thread and need not be thread-safe.
/// </summary>
public interface ISfxLoopBackend
{
    /// <summary>The fixed number of protected loop slots.</summary>
    int LoopCapacity { get; }

    /// <summary>Attempts admission without stealing another loop or falling back to a one-shot.</summary>
    SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback);

    /// <summary>Stops this identity. Failed cleanup retains ownership for an explicit retry.</summary>
    bool StopLoop(SfxLoopBackendHandle handle);

    /// <summary>Updates gain without restarting playback.</summary>
    bool SetLoopGain(SfxLoopBackendHandle handle, float gain);

    /// <summary>Updates a positional loop. Flat loops refuse position changes.</summary>
    bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position);

    /// <summary>Reports whether this identity still owns an admitted loop.</summary>
    bool IsLoopActive(SfxLoopBackendHandle handle);

    /// <summary>Unloads a buffer only after its loops are confirmed stopped and detached.</summary>
    bool TryUnloadSfx(int bufferHandle);
}
