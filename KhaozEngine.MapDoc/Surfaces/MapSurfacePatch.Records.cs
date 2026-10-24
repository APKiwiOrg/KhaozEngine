using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

public sealed partial class MapSurfacePatch
{
    public List<MapTopologyRecord> Records { get; } = new();

    void CloneRecordsTo(MapSurfacePatch target)
    {
        target.Records.EnsureCapacity(checked((int)Scene.MapSceneAdmission.CloneCapacity(Records.Count, true)));
        foreach (MapTopologyRecord record in Records)
            target.Records.Add(record switch
            {
                MapSurfaceSeam seam => seam with { Pairs = seam.Pairs.ToArray() },
                MapBoundaryChain chain => chain with { Vertices = chain.Vertices.ToArray() },
                MapWallStrip strip => strip,
                MapCavePortal portal => portal with { Interval = portal.Interval.ToArray() },
                MapHorizontalOpening opening => opening with { SlotCells = opening.SlotCells.ToArray() },
                MapVerticalLink link => link with
                {
                    Openings = link.Openings.ToArray(),
                    Portals = link.Portals.ToArray(),
                    GeometryOwners = link.GeometryOwners.ToArray(),
                },
                MapSpaceDoc space => space with
                {
                    DomainTags = space.DomainTags.ToArray(),
                    Walls = space.Walls.ToArray(),
                    Portals = space.Portals.ToArray(),
                    Links = space.Links.ToArray(),
                },
                MapPrismDomain prism => prism with
                {
                    Footprint = prism.Footprint with
                    {
                        Rects = prism.Footprint.Rects.ToArray(),
                        Rings = prism.Footprint.Rings.Select(r => r with { Vertices = r.Vertices.ToArray() }).ToArray(),
                    },
                },
                MapSpaceFootprint footprint => footprint with { SlotCells = footprint.SlotCells.ToArray() },
                _ => throw new MapDocumentException("unknown topology record type"),
            });
    }
}
