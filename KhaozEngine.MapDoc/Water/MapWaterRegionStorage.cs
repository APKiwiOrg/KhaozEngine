using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Keeps accumulated regions in the body collector until its emitted inventory settles.</summary>
internal sealed class MapWaterRegionStorage
{
    readonly MapPreparationScope? _owner = MapSceneAdmission.Current?.PreparationScope;

    internal void Reserve(long bytes)
    {
        if (_owner is null) return;
        var envelope = MapSceneAdmission.Current?.PreparationScope;
        long transfer = envelope is not null && envelope != _owner ? Math.Min(bytes, envelope.Bytes) : 0;
        // Move the escaping part of an admitted footprint envelope without releasing or charging it again.
        _owner.Reserve(bytes - transfer);
        if (transfer != 0) _owner.AdoptWorkspace(envelope!.WorkspaceOwner, transfer);
    }

    internal void Polygons(List<IReadOnlyList<MapWideXz>> rows, IEnumerable<IReadOnlyList<MapWideXz>> polygons)
    {
        foreach (var polygon in polygons) Append(rows, polygon, checked(128L * polygon.Count));
    }

    internal void Append<T>(List<T> rows, T value, long payloadBytes = 0)
    {
        int previous = rows.Capacity;
        int capacity = rows.Count < previous ? previous : checked(Math.Max(4, previous * 2));
        long backing = capacity == previous ? 0 : checked((long)capacity * Unsafe.SizeOf<T>());
        Reserve(checked(payloadBytes + backing));
        if (backing != 0)
        {
            rows.Capacity = capacity;
            _owner?.Release(checked((long)previous * Unsafe.SizeOf<T>()));
        }
        rows.Add(value);
    }
}
