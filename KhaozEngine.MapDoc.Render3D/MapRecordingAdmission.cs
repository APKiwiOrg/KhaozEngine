using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Before-work storage admission for a preparation recorder, including replacement and snapshot overlap.</summary>
internal sealed class MapRecordingAdmission(MapPreparationScope scope)
{
    internal static long DrawBytes => Unsafe.SizeOf<MapDrawRecord>();
    internal void Resource(Dictionary<MapResourceReference, MapPreparedResource> resources)
    {
        int old = resources.EnsureCapacity(0);
        if (resources.Count < old) return;
        int requested = checked(Math.Max(3, old * 2));
        // Dictionary prime rounding stays within twice the requested capacity.
        long envelope = checked(256L * requested);
        scope.Reserve(envelope);
        int capacity;
        try { capacity = resources.EnsureCapacity(requested); }
        catch { scope.Release(envelope); throw; }
        scope.Release(checked(envelope + 128L * old - 128L * capacity));
    }
    internal void Draw(List<MapDrawRecord> draws)
    {
        int old = draws.Capacity;
        if (draws.Count < old) return;
        int capacity = checked(Math.Max(4, old * 2));
        long bytes = checked(DrawBytes * capacity);
        scope.Reserve(bytes);
        try { draws.Capacity = capacity; }
        catch { scope.Release(bytes); throw; }
        scope.Release(checked(DrawBytes * old));
    }
    internal void Snapshot(int resources, int draws) =>
        scope.Reserve(checked(2 * (IntPtr.Size * (long)resources + DrawBytes * draws)));
}
