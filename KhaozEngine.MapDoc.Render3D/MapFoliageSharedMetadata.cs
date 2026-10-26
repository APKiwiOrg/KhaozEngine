using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>One reference-counted metadata inventory. It contains only strings and immutable generation policies.</summary>
internal sealed class MapFoliageSharedMetadata
{
    readonly object _gate = new();
    readonly MapFoliageRetainedInventory _inventory = new();
    Dictionary<object, int>? _references;
    long _directBytes;
    bool _disposed;
    internal bool Disposed { get { lock (_gate) return _disposed; } }
    internal long RetainedBytes { get { lock (_gate) return checked(_inventory.Bytes + _directBytes); } }
    internal void Add(object[] references, MapSceneAdmission? admission, Func<long, IDisposable>? retain, Action<long>? beforeGrowth = null)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MapFoliageSharedMetadata));
            int additions = 0; long objects = 0;
            foreach (var value in references)
                if (_references is null || !_references.ContainsKey(value)) { additions++; objects = checked(objects + Size(value)); }
            int count = _references?.Count ?? 0, capacity = _references?.EnsureCapacity(0) ?? 0;
            int required = checked(count + additions);
            bool resize = required > capacity || 2L * required < capacity;
            long oldArrays = capacity == 0 ? 0 : Arrays(capacity);
            long upperArrays = resize ? Arrays(Math.Max(3, checked(required * 2))) : 0;
            long old = _inventory.Bytes;
            // Registry 56, gate 24, retained-inventory 48, its CPU list 32 and dictionary 80.
            long controls = _references is null ? 240 : 0;
            long growth = checked(objects + controls + upperArrays);
            beforeGrowth?.Invoke(growth);
            _inventory.Grow(growth, admission, retain);
            _references ??= new(ReferenceEqualityComparer.Instance);
            if (resize)
            {
                if (required > capacity) _references.EnsureCapacity(required);
                else _references.TrimExcess(required);
            }
            foreach (var value in references)
                _references[value] = _references.TryGetValue(value, out int owners) ? checked(owners + 1) : 1;
            long actualArrays = resize ? Arrays(_references.EnsureCapacity(0)) : oldArrays;
            _inventory.ReduceTo(checked(old + objects + controls + actualArrays - oldArrays));
        }
    }
    internal void AddDirect(long bytes) { lock (_gate) _directBytes = checked(_directBytes + bytes); }
    // Removing references does not return credit while the releasing payload is still on its caller's stack.
    internal long RemoveReferences(object[] references)
    {
        lock (_gate)
        {
            long released = 0;
            foreach (var value in references)
            {
                int owners = _references![value];
                if (owners == 1) { _references.Remove(value); released = checked(released + Size(value)); }
                else _references[value] = owners - 1;
            }
            return released;
        }
    }
    internal void SettleRelease(long objects, long direct)
    {
        lock (_gate)
        {
            _directBytes -= direct;
            _inventory.ReduceTo(_inventory.Bytes - objects);
            if (_references!.Count != 0) return;
            _references = null; _disposed = true; _inventory.Dispose();
        }
    }
    internal static long ObjectBytes(object[] references)
    { long bytes = 0; foreach (var value in references) bytes = checked(bytes + Size(value)); return bytes; }
    static long Size(object value) => value switch
    {
        string text => MapFoliageInventory.Text(text),
        MapFoliagePolicy => 32,
        _ => throw new InvalidOperationException("Unexpected foliage metadata object.")
    };
    static long Arrays(int capacity) => checked(48L + 28L * capacity);
}
