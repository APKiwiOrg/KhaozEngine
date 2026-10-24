using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Before-work storage admission for a preparation recorder, including replacement and snapshot overlap.</summary>
internal sealed class MapRecordingAdmission(MapPreparationScope scope)
{
    long _resourceBytes;
    internal static long DrawBytes => Unsafe.SizeOf<MapDrawRecord>();
    internal void Resource(Dictionary<MapResourceReference, MapPreparedResource> resources)
    {
        MapPreparationCollections.Dictionary(resources, scope.WorkspaceOwner, ref _resourceBytes, 128);
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
