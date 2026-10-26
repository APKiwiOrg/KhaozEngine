using System;
using System.Numerics;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>Device operations for slots exclusively owned by a protected loop pool.</summary>
internal interface ISfxLoopDevice : IDisposable
{
    bool TryStart(int slot, in SfxLoopPlayback playback);
    bool TryStop(int slot);
    bool TrySetGain(int slot, float gain);
    bool TrySetPosition(int slot, Vector3 position);
}

/// <summary>The sole admission and ownership authority for a bounded set of protected loops.</summary>
internal sealed class SfxLoopPool : IDisposable
{
    public const int StockCapacity = 4;

    enum SlotState { Free, Reserved, Active, Quarantined, Retired }

    struct Slot
    {
        public SlotState State;
        public ulong Generation;
        public int Buffer;
        public bool Positional;
    }

    readonly Slot[] _slots;
    readonly ISfxLoopDevice _device;
    readonly ILogger? _logger;
    bool _disposed;

    public SfxLoopPool(int capacity, ISfxLoopDevice device, ILogger? logger = null, ulong initialGeneration = 0)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        ArgumentNullException.ThrowIfNull(device);
        _slots = new Slot[capacity];
        _device = device;
        _logger = logger;
        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Generation = initialGeneration;
    }

    public int Capacity => _slots.Length;

    public SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback)
    {
        if (_disposed) return new(SfxLoopStartStatus.Disposed, default);
        for (int i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.State != SlotState.Free) continue;
            if (slot.Generation == ulong.MaxValue)
            {
                slot.State = SlotState.Retired;
                continue;
            }

            slot.Generation++;
            slot.State = SlotState.Reserved;
            // A partial start may bind the buffer even when no identity can be published.
            slot.Buffer = playback.Buffer;
            slot.Positional = playback.Positional;
            if (TryStartSlot(i, playback))
            {
                slot.State = SlotState.Active;
                return new(SfxLoopStartStatus.Started, new(i, slot.Generation));
            }

            slot.State = SlotState.Quarantined;
            TryStopSlot(i);
            return default;
        }
        return new(SfxLoopStartStatus.Capacity, default);
    }

    public bool StopLoop(SfxLoopBackendHandle handle) => IsLoopActive(handle) && TryStopSlot(handle.Slot);

    public bool SetLoopGain(SfxLoopBackendHandle handle, float gain)
    {
        if (!IsLoopActive(handle)) return false;
        try
        {
            if (_device.TrySetGain(handle.Slot, gain)) return true;
        }
        catch (Exception exception)
        {
            return ReportFailure("gain", handle.Slot, exception);
        }
        return ReportFailure("gain", handle.Slot);
    }

    public bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position)
    {
        if (!IsLoopActive(handle) || !_slots[handle.Slot].Positional) return false;
        try
        {
            if (_device.TrySetPosition(handle.Slot, position)) return true;
        }
        catch (Exception exception)
        {
            return ReportFailure("position", handle.Slot, exception);
        }
        return ReportFailure("position", handle.Slot);
    }

    public bool IsLoopActive(SfxLoopBackendHandle handle) =>
        !_disposed && handle.Slot >= 0 && handle.Slot < _slots.Length && handle.Generation != 0 &&
        _slots[handle.Slot].State == SlotState.Active && _slots[handle.Slot].Generation == handle.Generation;

    public bool StopBufferLoops(int bufferHandle)
    {
        if (_disposed) return false;
        bool stopped = true;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (NeedsCleanup(_slots[i].State) && _slots[i].Buffer == bufferHandle)
                stopped &= TryStopSlot(i);
        }
        return stopped;
    }

    public bool StopAllLoops()
    {
        if (_disposed) return false;
        bool stopped = true;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (NeedsCleanup(_slots[i].State)) stopped &= TryStopSlot(i);
        }
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (NeedsCleanup(_slots[i].State)) TryStopSlot(i);
            _slots[i].State = SlotState.Retired;
        }
        try
        {
            _device.Dispose();
        }
        catch (Exception exception)
        {
            ReportFailure("dispose", -1, exception);
        }
    }

    bool TryStartSlot(int index, in SfxLoopPlayback playback)
    {
        try
        {
            if (_device.TryStart(index, playback)) return true;
        }
        catch (Exception exception)
        {
            return ReportFailure("start", index, exception);
        }
        return ReportFailure("start", index);
    }

    bool TryStopSlot(int index)
    {
        try
        {
            if (_device.TryStop(index))
            {
                _slots[index].State = SlotState.Free;
                return true;
            }
        }
        catch (Exception exception)
        {
            return ReportFailure("stop", index, exception);
        }
        return ReportFailure("stop", index);
    }

    bool ReportFailure(string operation, int slot, Exception? exception = null)
    {
        _logger?.Warn($"SFX loop {operation} failed for slot {slot}.", exception);
        return false;
    }

    static bool NeedsCleanup(SlotState state) =>
        state is SlotState.Active or SlotState.Quarantined or SlotState.Reserved;
}
