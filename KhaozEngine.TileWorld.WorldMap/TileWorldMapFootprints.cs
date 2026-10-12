using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.WorldMap;

namespace KhaozEngine.TileWorld.WorldMap;

/// <summary>Collects authored object footprints after plane and map-area filtering.</summary>
internal sealed class TileWorldMapFootprints
{
    readonly TileWorldDocument _document;
    readonly TileWorldCatalogs _catalogs;
    readonly int _plane;
    readonly float _tileSize;
    readonly int _maximumSize;

    internal TileWorldMapFootprints(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, float tileSize)
    {
        _document = document;
        _catalogs = catalogs;
        _plane = plane;
        _tileSize = tileSize;
        foreach (TileObjectArchetype archetype in catalogs.Archetypes.Values)
            _maximumSize = Math.Max(_maximumSize, Math.Max(archetype.SizeX, archetype.SizeZ));
    }

    internal void Collect(MapRect area, List<WorldMapFootprint> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        TileRect tiles = TileWorldMapSource.TilesCovering(area, _tileSize);
        if (tiles.IsEmpty) return;
        foreach (TileObject obj in _document.ObjectsIn(tiles.Expand(_maximumSize)))
        {
            TileObjectArchetype? archetype = _catalogs.Archetype(obj.ArchetypeId);
            if (archetype is null || (obj.Plane != _plane && !archetype.IsRoof)) continue;
            MapRect footprint = TileWorldMapSource.MapRectangle(
                TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation), _tileSize);
            if (!footprint.Intersects(area)) continue;
            into.Add(new WorldMapFootprint(obj.Id, obj.ArchetypeId, footprint.Center,
                new Vector2(archetype.SizeX, archetype.SizeZ) * (_tileSize * 0.5f),
                obj.Rotation * MathF.PI / 2f, CombineTags(archetype.Tags, obj.Tags), archetype.IsRoof,
                Collision(archetype.CollisionKind)));
        }
    }

    static IReadOnlyList<string> CombineTags(List<string>? archetype, List<string>? obj)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (archetype is not null)
            foreach (string tag in archetype)
                if (seen.Add(tag)) tags.Add(tag);
        if (obj is not null)
            foreach (string tag in obj)
                if (seen.Add(tag)) tags.Add(tag);
        return tags.ToArray();
    }

    static WorldMapCollision Collision(TileCollisionKind collision) => collision switch
    {
        TileCollisionKind.None => WorldMapCollision.None,
        TileCollisionKind.Solid => WorldMapCollision.Solid,
        TileCollisionKind.Wall or TileCollisionKind.WallCorner or TileCollisionKind.Diagonal => WorldMapCollision.Wall,
        _ => throw new ArgumentOutOfRangeException(nameof(collision)),
    };
}
