namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Per-list, per-ring-slot footprints. GPU completion timing never changes which storage a slot uses.</summary>
internal sealed class MetalRecordingReads
{
    readonly MetalCpuReadProtection _protection;
    readonly MetalRecordedReads[] _slots;
    internal MetalRecordedReads Current { get; private set; }

    internal MetalRecordingReads(MetalCpuReadProtection protection, int framesInFlight)
    {
        _protection = protection;
        _slots = new MetalRecordedReads[framesInFlight];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new();
        Current = _slots[0];
    }

    // BeginRecording has already waited for the ring slot. Retire its old protection under the same lock
    // CPU writers use, before clearing the footprint. An unfinished prefix still quarantines the slot.
    internal void Begin(int segment)
    {
        lock (_protection.Gate)
        {
            _protection.RequireSlotAvailable(segment);
            Current = _slots[segment];
            Current.Clear();
        }
    }
}
