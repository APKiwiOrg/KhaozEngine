using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>The content half of the read side: the objects, markers, regions, catalogs and prefab files a
/// client lists before it decides what to place. Same session, same lock, still nothing that mutates.</summary>
public sealed partial class QueryService
{
    /// <summary>The catalog kinds <see cref="CatalogList"/> accepts.</summary>
    public const string CatalogKinds = "materials, archetypes";

    /// <summary>The file extension a prefab is written with, and the one <see cref="PrefabList"/> looks for.</summary>
    public const string PrefabExtension = ".json";

    /// <summary>Every object anchored inside the rect, on one plane or on all of them when
    /// <paramref name="plane"/> is null.</summary>
    /// <exception cref="ArgumentException">The rect covers no tiles.</exception>
    public IReadOnlyList<ObjectInfo> ObjectsInRect(TileRect rect, int? plane = null)
    {
        RequireRect(rect);
        return session.Read(e =>
        {
            TileLoadedCorners corners = TileLoadedCorners.Of(e.Document);
            return e.Document.ObjectsIn(rect, plane)
                .OrderBy(o => o.Id)
                .Select(o => Describe(e, corners, o))
                .ToArray();
        });
    }

    /// <summary>One object by id.</summary>
    /// <exception cref="TileWorldException"><see cref="TileWorldRefusal.ObjectMissing"/> carries
    /// <see cref="TileWorldException.ObjectId"/> when no object has that id.</exception>
    public ObjectInfo ObjectGet(long id) => session.Read(e =>
        Describe(e, TileLoadedCorners.Of(e.Document), e.Document.FindObject(id)
            ?? throw new TileWorldException(TileWorldRefusal.ObjectMissing, $"object {id} does not exist")
            { ObjectId = id }));

    /// <summary>Every object matching an archetype id, a tag, or both. Both null lists the whole world, which is
    /// what an author wants after a scatter.</summary>
    public IReadOnlyList<ObjectInfo> ObjectFind(string? archetypeId = null, string? tag = null) => session.Read(e =>
    {
        TileLoadedCorners corners = TileLoadedCorners.Of(e.Document);
        return e.Document.AllObjects()
            .Where(o => archetypeId is null || string.Equals(o.ArchetypeId, archetypeId, StringComparison.Ordinal))
            .Where(o => tag is null || (o.Tags is not null && o.Tags.Contains(tag, StringComparer.Ordinal)))
            .OrderBy(o => o.Id)
            .Select(o => Describe(e, corners, o))
            .ToArray();
    });

    /// <summary>Every marker in the world, in name order.</summary>
    public IReadOnlyList<MarkerInfo> MarkerList() => session.Read(e => e.Document.AllMarkers()
        .OrderBy(m => m.Name, StringComparer.Ordinal)
        .Select(m => new MarkerInfo(m.Name, m.X, m.Z, m.Plane, Tags(m.Tags)))
        .ToArray());

    /// <summary>The loaded catalogs' materials or archetypes, whichever <paramref name="kind"/> asks for.</summary>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="CatalogKinds"/>.</exception>
    public CatalogListResult CatalogList(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        string key = kind.Trim().ToLowerInvariant();
        if (key is not ("materials" or "archetypes"))
            throw new ArgumentException($"'{kind}' is not a catalog kind. The kinds are {CatalogKinds}.", nameof(kind));
        return session.Read(e =>
        {
            if (key == "materials")
            {
                MaterialInfo[] materials = e.Catalogs.Materials.Values
                    .OrderBy(m => m.Id)
                    .Select(m => new MaterialInfo(m.Id, m.Name, m.Color, m.Kind.ToString()))
                    .ToArray();
                return new CatalogListResult(key, materials, Array.Empty<ArchetypeInfo>());
            }
            ArchetypeInfo[] archetypes = e.Catalogs.Archetypes.Values
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => new ArchetypeInfo(a.Id, a.Name, a.MeshRef, a.SizeX, a.SizeZ,
                    a.CollisionKind.ToString(), a.IsRoof, a.Interactive, Tags(a.Tags), Boxes(a.CollisionBoxes)))
                .ToArray();
            return new CatalogListResult(key, Array.Empty<MaterialInfo>(), archetypes);
        });
    }

    /// <summary>Every region the world holds, south row first and west to east within a row, with what is
    /// anchored in each.</summary>
    public IReadOnlyList<RegionInfo> RegionList() => session.Read(e => e.Document.Regions.Values
        .OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx)
        .Select(r => new RegionInfo(r.Coord.Rx, r.Coord.Rz, RectInfo.Of(r.Coord.Rect), r.Objects.Count, r.Markers.Count))
        .ToArray());

    /// <summary>The prefab files in a directory, by name. A relative directory resolves against the open world's
    /// directory, the same rule every other path argument follows.</summary>
    /// <exception cref="TileWorldException">The directory does not exist, carrying
    /// <see cref="TileWorldRefusal.PrefabDirectoryMissing"/> and its resolved path in
    /// <see cref="TileWorldException.SourceName"/>. A closed session carries
    /// <see cref="TileWorldRefusal.SessionNotOpen"/> instead.</exception>
    public IReadOnlyList<PrefabFileInfo> PrefabList(string directory)
    {
        string resolved = session.ResolvePath(directory);
        if (!Directory.Exists(resolved))
            throw new TileWorldException(TileWorldRefusal.PrefabDirectoryMissing,
                $"{resolved}: no such directory, there are no prefabs to list")
            { SourceName = resolved };
        return Directory.EnumerateFiles(resolved, "*" + PrefabExtension)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new PrefabFileInfo(System.IO.Path.GetFileNameWithoutExtension(p), p, new FileInfo(p).Length))
            .ToArray();
    }

    // An object with its rotated footprint and its placed collision boxes. A dangling archetype (content the validator
    // reports) falls back to the single anchor tile and no boxes rather than faulting a read.
    static ObjectInfo Describe(TileEditingDocument e, TileLoadedCorners corners, TileObject o)
    {
        TileObjectArchetype? a = e.Catalogs.Archetype(o.ArchetypeId);
        TileRect footprint = a is null ? new TileRect(o.X, o.Z, 1, 1) : TileFootprint.Of(a, o.X, o.Z, o.Rotation);
        return new ObjectInfo(o.Id, o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation, Tags(o.Tags), RectInfo.Of(footprint),
            PlacedBoxes(e.Document, corners, a, o));
    }

    // The boxes exactly where tile world physics builds them, through the one placement rule both read, for an
    // archetype physics describes by its boxes. Empty for any other, and a box that cannot place is left out as
    // physics leaves it out.
    static IReadOnlyList<PlacedCollisionBoxInfo> PlacedBoxes(TileWorldDocument document, TileLoadedCorners corners,
        TileObjectArchetype? archetype, TileObject o)
    {
        if (archetype is null || !TileCollisionBoxes.Describes(archetype)) return Array.Empty<PlacedCollisionBoxInfo>();
        var placed = new List<PlacedCollisionBoxInfo>(archetype.CollisionBoxes!.Count);
        foreach (TileCollisionBox? box in archetype.CollisionBoxes)
        {
            if (!TileCollisionBoxes.TryPlace(document, corners, archetype, o, box, out TilePlacedCollisionBox p)) continue;
            placed.Add(new PlacedCollisionBoxInfo(p.CentreX, p.CentreZ, p.HalfX, p.HalfZ, p.YawDegrees, p.Bottom, p.Top));
        }
        return placed;
    }

    static IReadOnlyList<CollisionBoxInfo> Boxes(List<TileCollisionBox>? boxes) =>
        boxes is null
            ? Array.Empty<CollisionBoxInfo>()
            : boxes.OfType<TileCollisionBox>()
                .Select(b => new CollisionBoxInfo(b.MinX, b.MaxX, b.MinY, b.MaxY, b.MinZ, b.MaxZ, b.YawDegrees))
                .ToArray();

    static IReadOnlyList<string> Tags(List<string>? tags) =>
        tags is null ? Array.Empty<string>() : tags.ToArray();
}
