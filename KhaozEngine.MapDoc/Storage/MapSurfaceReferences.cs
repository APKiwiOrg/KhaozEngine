using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Forward anchors and geometry incidence are separate from non-spatial record references.</summary>
internal static class MapSurfaceReferences
{
    internal static IEnumerable<MapPatchKey> Patches(MapSurfacePatch patch) => patch.CornerDependencies.Select(d => d.Owner.Patch)
        .Concat(patch.Records.SelectMany(Anchors)).Distinct();
    static IEnumerable<MapPatchKey> Anchors(MapTopologyRecord record)
    {
        foreach (MapRecordRef reference in Records(record)) yield return reference.Anchor;
        switch (record)
        {
            case MapSurfaceSeam seam: yield return seam.First.Patch; yield return seam.Second.Patch; break;
            case MapBoundaryChain { SourcePatch: { } key }: yield return key; break;
            case MapHorizontalOpening opening: yield return opening.Patch; break;
            case MapSpaceFootprint footprint: yield return footprint.Lattice; break;
        }
    }
    internal static IEnumerable<MapRecordRef> Records(MapTopologyRecord record) => record switch
    {
        MapWallStrip strip => new[] { strip.LowerChain, strip.UpperChain },
        MapCavePortal portal => new[] { portal.FromSpace, portal.ToSpace, portal.BandBottom }.Concat(portal.BandTop is { } top ? new[] { top } : Array.Empty<MapRecordRef>()),
        MapVerticalLink link => new[] { link.UpperSpace, link.LowerSpace }.Concat(link.Openings).Concat(link.Portals).Concat(link.GeometryOwners),
        MapSpaceDoc space => (space.Parent is { } parent ? new[] { parent } : Array.Empty<MapRecordRef>())
            .Concat(space.AliasOf is { } alias ? new[] { alias } : Array.Empty<MapRecordRef>())
            .Concat(space.Walls.Select(w => w.Record)).Concat(space.Portals.Select(p => p.Record)).Concat(space.Links),
        MapSpaceFootprint footprint => new[] { footprint.Space }.Concat(new[] { footprint.Lower.Opening, footprint.Upper.Opening }.OfType<MapRecordRef>()),
        _ => Array.Empty<MapRecordRef>(),
    };
    internal static IReadOnlyList<string> Spaces(MapSurfaceSet set, MapSurfacePatch patch, IReadOnlyList<MapRecordRef> incident)
    {
        var records = patch.Records.Concat(incident.Select(r => set.TryGetRecord(r, out MapTopologyRecord? record) ? record : null).OfType<MapTopologyRecord>());
        // Root span metadata can change without a payload edit. Combine it at query time, not in carried pages.
        return Array.AsReadOnly(records.SelectMany(SpaceIds).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
    static IEnumerable<string> SpaceIds(MapTopologyRecord r) => r switch
    {
        MapSpaceDoc space => new[] { space.Id },
        MapSpaceFootprint footprint => new[] { footprint.Space.Id },
        MapCavePortal portal => new[] { portal.FromSpace.Id, portal.ToSpace.Id },
        MapVerticalLink link => new[] { link.UpperSpace.Id, link.LowerSpace.Id },
        _ => Array.Empty<string>(),
    };
}
