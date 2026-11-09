using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Index backing capacity is owned by a newly constructed source, not by its pinned producer.</summary>
internal static class MapSurfaceSourcePreparation
{
    internal static long DictionaryBytes<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values) where TKey : notnull =>
        checked((Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>() + 24L) * ((Dictionary<TKey, TValue>)values).EnsureCapacity(0));
    internal static void BeforeDictionary<TKey, TValue>(int count) => MapSceneAdmission.Current?.ReserveBytes(checked(
        (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>() + 24L) * (count == 0 ? 0 : 4L * count + 3)));
    internal static long Vertices(MapTopologyRecord record) => record switch
    {
        MapPrismDomain prism => checked(4L * prism.Footprint.Rects.Count + RingVertices(prism)),
        MapSpaceFootprint footprint => 4L * footprint.SlotCells.Count,
        MapHorizontalOpening opening => 4L * opening.SlotCells.Count,
        MapBoundaryChain chain => chain.Vertices.Count,
        MapCavePortal portal => portal.Interval.Count,
        MapSurfaceSeam => 4,
        _ => 0,
    };
    static long RingVertices(MapPrismDomain prism)
    {
        long count = 0;
        foreach (var ring in prism.Footprint.Rings) count = checked(count + ring.Vertices.Count);
        return count;
    }
}
