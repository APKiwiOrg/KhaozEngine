using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Admits collection copies and replacement arrays while their previous backing storage is still live.</summary>
internal static class MapPreparationCollections
{
    internal static HashSet<T> CopySet<T>(HashSet<T> source, MapPreparationReservation workspace,
        out long bytes, long entryBytes)
    {
        long envelope = checked(source.EnsureCapacity(0) * entryBytes);
        workspace.Reserve(envelope);
        HashSet<T> result;
        try { result = new(source, source.Comparer); }
        catch { workspace.Release(envelope); throw; }
        bytes = checked(result.EnsureCapacity(0) * entryBytes);
        workspace.Release(envelope - bytes);
        return result;
    }

    internal static void Set<T>(HashSet<T> entries, MapPreparationReservation workspace,
        ref long bytes, long entryBytes)
    {
        int capacity = entries.EnsureCapacity(0);
        if (entries.Count < capacity) return;
        int requested = checked(System.Math.Max(3, capacity * 2));
        long envelope = checked(2L * requested * entryBytes);
        workspace.Reserve(envelope);
        int next;
        try { next = entries.EnsureCapacity(requested); }
        catch { workspace.Release(envelope); throw; }
        long retained = checked(next * entryBytes);
        workspace.Release(checked(envelope + bytes - retained)); bytes = retained;
    }

    internal static void Dictionary<TKey, TValue>(Dictionary<TKey, TValue> entries, MapPreparationReservation workspace,
        ref long bytes, long entryBytes = 0) where TKey : notnull
    {
        int capacity = entries.EnsureCapacity(0);
        if (entries.Count < capacity) return;
        int requested = checked(Math.Max(3, capacity * 2));
        if (entryBytes == 0) entryBytes = Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>() + 24L;
        // Prime rounding stays within twice the requested capacity.
        long envelope = checked(2L * requested * entryBytes);
        workspace.Reserve(envelope);
        int next;
        try { next = entries.EnsureCapacity(requested); }
        catch { workspace.Release(envelope); throw; }
        long retained = checked(next * entryBytes);
        workspace.Release(checked(envelope + bytes - retained)); bytes = retained;
    }
}
