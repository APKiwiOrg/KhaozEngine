using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>One scene's immutable optical fields. Publication follows upload, invalidation precedes retirement,
/// and the supplied queue owns the point at which a texture can physically die.</summary>
internal sealed class WaterOpticsResources : IDisposable
{
    readonly IGpuDevice _device;
    readonly GpuRetireQueue _retired;
    readonly Guid _owner = Guid.NewGuid();
    readonly MeshSlotMap _slots = new();
    readonly List<Entry?> _entries = new();
    bool _disposed;
    int _liveFields;
    long _liveBytes, _retiredBytes, _uploadCount;

    internal WaterOpticsResources(IGpuDevice device, GpuRetireQueue retired)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(retired);
        _device = device;
        _retired = retired;
    }

    internal WaterOpticsStats Stats => new(_liveFields, _liveBytes, _retiredBytes, _uploadCount);

    internal WaterOpticsHandle Load(WaterOpticsField field)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(field);
        long liveBytes = checked(_liveBytes + field.SampleBytes);
        long uploads = checked(_uploadCount + 1);
        int liveFields = checked(_liveFields + 1);
        _entries.EnsureCapacity(checked(_slots.SlotCount + 1));
        byte[] bytes = MemoryMarshal.AsBytes(field.RestDepths).ToArray();
        IGpuTexture texture = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            (uint)field.Width, (uint)field.Height, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
        var entry = new Entry(this, field, texture);
        int index = -1, generation = 0;
        try
        {
            _device.UpdateTexture(texture, bytes, 0, 0, (uint)field.Width, (uint)field.Height);
            _uploadCount = uploads;
            index = _slots.Alloc(out generation);
            entry.Handle = new WaterOpticsHandle(_owner, index, generation);
            if (index == _entries.Count) _entries.Add(entry);
            else _entries[index] = entry;
            _liveFields = liveFields;
            _liveBytes = liveBytes;
            return entry.Handle;
        }
        catch
        {
            if (index >= 0) _slots.Free(index, generation);
            // Even a throwing upload may have submitted a staging copy. Keep the unpublished texture alive
            // behind the same completion proof as a released live field, without consuming a slot.
            Retire(entry);
            throw;
        }
    }

    internal Entry Resolve(WaterOpticsHandle handle)
    {
        if (_disposed || !handle.IsValid || handle.Owner != _owner || !_slots.IsValid(handle.Index, handle.Generation))
            throw new ArgumentException("The optical field handle is foreign, stale or invalid.", nameof(handle));
        return _entries[handle.Index]!;
    }

    internal void Unload(WaterOpticsHandle handle)
    {
        if (handle == default) return;
        Entry entry = Resolve(handle);
        _slots.Free(handle.Index, handle.Generation);
        _entries[handle.Index] = null;
        _liveFields--;
        _liveBytes -= entry.Field.SampleBytes;
        Retire(entry);
    }

    void Retire(Entry entry)
    {
        _retiredBytes = checked(_retiredBytes + entry.Field.SampleBytes);
        _retired.Retire(entry);
    }

    /// <summary>Invalidates all live fields. The caller must flush its queue during teardown after GPU completion.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i] is { } entry) Unload(entry.Handle);
        _disposed = true;
    }

    /// <summary>The retained CPU payload, texture and identity consumed by water preparation. The retire queue
    /// retains this entire entry until disposal, including the source field.</summary>
    internal sealed class Entry : IDisposable
    {
        readonly WaterOpticsResources _owner;
        bool _disposed;

        internal Entry(WaterOpticsResources owner, WaterOpticsField field, IGpuTexture texture)
        {
            _owner = owner;
            Field = field;
            Texture = texture;
        }

        internal WaterOpticsField Field { get; }
        internal IGpuTexture Texture { get; }
        internal WaterOpticsHandle Handle { get; set; }

        void IDisposable.Dispose()
        {
            if (_disposed) return;
            Texture.Dispose();
            _disposed = true;
            _owner._retiredBytes -= Field.SampleBytes;
        }
    }
}
