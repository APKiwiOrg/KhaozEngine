using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Required members inside a child payload that <see cref="MapPrefabReferenceWalker"/> dereferences: topology
/// record references and lists, a surface's indoor span and a water body's covers. It mirrors the walker's typed
/// positions. <see cref="MapPrefabRequiredMembers.Child"/> calls it once the payload itself is known nonnull, before
/// override reconciliation, the child set and asset validation walk the child. Surface patch internals are left to
/// the patch codec, which refuses its own null members, and a prefab surface child carries no records.</summary>
internal static class MapPrefabReferenceMembers
{
    internal static void Child(MapPrefabChild child)
    {
        MapPrefabChildKey key = child.Key;
        switch (child)
        {
            case MapPrefabSurfaceChild { Surface.IndoorSpan: { } span }:
                Member(span.ParentSpace, "$.surface.indoorSpan.parentSpace", key);
                Member(span.DomainTags, "$.surface.indoorSpan.domainTags", key);
                break;
            case MapPrefabWaterChild water: Elements(water.Value.Covers, "$.value.covers", key); break;
            case MapPrefabRecordChild record: Record(record.Value, key); break;
        }
    }

    static void Record(MapTopologyRecord record, MapPrefabChildKey key)
    {
        switch (record)
        {
            case MapSurfaceSeam seam:
                Member(seam.First, "$.value.first", key);
                Member(seam.Second, "$.value.second", key);
                Member(seam.Pairs, "$.value.pairs", key);
                break;
            case MapBoundaryChain chain: Elements(chain.Vertices, "$.value.vertices", key); break;
            case MapWallStrip strip:
                Member(strip.LowerChain, "$.value.lowerChain", key);
                Member(strip.UpperChain, "$.value.upperChain", key);
                break;
            case MapCavePortal portal:
                Member(portal.FromSpace, "$.value.fromSpace", key);
                Member(portal.ToSpace, "$.value.toSpace", key);
                Member(portal.Interval, "$.value.interval", key);
                Member(portal.BandBottom, "$.value.bandBottom", key);
                break;
            case MapVerticalLink link:
                Member(link.UpperSpace, "$.value.upperSpace", key);
                Member(link.LowerSpace, "$.value.lowerSpace", key);
                Elements(link.Openings, "$.value.openings", key);
                Elements(link.Portals, "$.value.portals", key);
                Elements(link.GeometryOwners, "$.value.geometryOwners", key);
                break;
            case MapSpaceDoc space:
                Member(space.DomainTags, "$.value.domainTags", key);
                Boundaries(space.Walls, "$.value.walls", key);
                Boundaries(space.Portals, "$.value.portals", key);
                Elements(space.Links, "$.value.links", key);
                break;
            case MapPrismDomain prism:
                Member(prism.Space, "$.value.space", key);
                Member(prism.Parent, "$.value.parent", key);
                Member(prism.Footprint, "$.value.footprint", key);
                Member(prism.Footprint.Rects, "$.value.footprint.rects", key);
                Elements(prism.Footprint.Rings, "$.value.footprint.rings", key);
                for (int index = 0; index < prism.Footprint.Rings.Count; index++)
                    Member(prism.Footprint.Rings[index].Vertices, $"$.value.footprint.rings[{index}].vertices", key, index);
                break;
            case MapSpaceFootprint footprint:
                Member(footprint.Space, "$.value.space", key);
                Member(footprint.SlotCells, "$.value.slotCells", key);
                Member(footprint.Lower, "$.value.lower", key);
                Member(footprint.Upper, "$.value.upper", key);
                break;
        }
    }

    static void Boundaries(IReadOnlyList<MapBoundaryRef> values, string path, MapPrefabChildKey key)
    {
        Elements(values, path, key);
        for (int index = 0; index < values.Count; index++) Member(values[index].Record, $"{path}[{index}].record", key, index);
    }

    static void Elements<T>(IReadOnlyList<T> values, string path, MapPrefabChildKey key) where T : class
    {
        Member(values, path, key);
        for (int index = 0; index < values.Count; index++) Member(values[index], $"{path}[{index}]", key, index);
    }

    static void Member([NotNull] object? value, string path, MapPrefabChildKey key, int? index = null) =>
        MapPrefabRequiredMembers.Require(value, path, "prefab child", child: key, index: index);
}
