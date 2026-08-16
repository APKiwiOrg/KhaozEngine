using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>The content half of the write side: objects, markers, regions and prefabs. Same command-backed
/// shape as the tile and height verbs in the other part, with one exception called out on the method itself
/// (<see cref="PrefabExtract"/> writes a FILE and changes no world state, so it is not a command at all).</summary>
public sealed partial class MutationService
{
    /// <summary>Places one object from a catalog archetype and reports the id the document allocated for it.</summary>
    public ObjectPlaceResult ObjectPlace(string archetypeId, int x, int z, int plane, int rotation = 0,
        IEnumerable<string>? tags = null)
    {
        (MutationResult result, PlaceObjectCommand command) =
            ExecuteCapturing(e => new PlaceObjectCommand(e.Catalogs, archetypeId, x, z, plane, rotation, tags));
        return new ObjectPlaceResult(result, command.ObjectId ?? 0L);
    }

    /// <summary>Moves one object's anchor. Two moves of the same object in one gesture coalesce into a single
    /// undo step, so a drag undoes all the way home.</summary>
    public MutationResult ObjectMove(long id, int x, int z, int plane) =>
        session.Execute(e => new MoveObjectCommand(e.Catalogs, id, x, z, plane));

    /// <summary>Turns one object in place, in quarter turns clockwise.</summary>
    public MutationResult ObjectRotate(long id, int rotation) =>
        session.Execute(e => new RotateObjectCommand(e.Catalogs, id, rotation));

    /// <summary>Deletes one object. The undo puts it back with the id it had, so every reference still
    /// resolves.</summary>
    public MutationResult ObjectRemove(long id) =>
        session.Execute(e => new RemoveObjectCommand(e.Catalogs, id));

    /// <summary>Replaces one object's authoring tags, with null meaning no tags at all.</summary>
    public MutationResult ObjectSetTags(long id, IEnumerable<string>? tags) =>
        session.Execute(_ => new SetObjectTagsCommand(id, tags));

    /// <summary>One object per tile of the line from (fromX, fromZ) to (toX, toZ), both ends included, as a
    /// single undo step.</summary>
    public PlacementBatchResult ObjectLine(string archetypeId, int fromX, int fromZ, int toX, int toZ, int plane,
        int rotation = 0)
    {
        (MutationResult result, CompositeCommand command) = ExecuteCapturing(e =>
            TileEditOps.Line(e.Catalogs, archetypeId, (fromX, fromZ), (toX, toZ), plane, rotation));
        return new PlacementBatchResult(result, PlacedIds(command));
    }

    /// <summary>A deterministic scatter of one archetype over a rect: a grid at <paramref name="spacing"/> tiles
    /// jittered by up to <paramref name="jitter"/> from a hash of the point and <paramref name="seed"/>, skipping
    /// blocked and already-occupied tiles. The same arguments always produce the same world, and the result can
    /// legitimately be empty.</summary>
    public PlacementBatchResult ObjectScatter(string archetypeId, TileRect rect, int plane, int spacing,
        int jitter, int seed)
    {
        (MutationResult result, CompositeCommand command) = ExecuteCapturing(e =>
            TileEditOps.Scatter(e, archetypeId, rect, plane, spacing, jitter, seed));
        return new PlacementBatchResult(result, PlacedIds(command));
    }

    /// <summary>Places or re-homes the uniquely named marker.</summary>
    public MutationResult MarkerSet(string name, int x, int z, int plane, IEnumerable<string>? tags = null) =>
        session.Execute(_ => new SetMarkerCommand(name, x, z, plane, tags));

    /// <summary>Deletes the named marker.</summary>
    public MutationResult MarkerRemove(string name) =>
        session.Execute(_ => new RemoveMarkerCommand(name));

    /// <summary>Materialises an empty region, which is void ground until something paints it.</summary>
    public MutationResult RegionCreate(int rx, int rz) =>
        session.Execute(_ => new CreateRegionCommand(new RegionCoord(rx, rz)));

    /// <summary>Deletes a whole region, layers, objects and markers included. The undo puts all of it back.</summary>
    public MutationResult RegionDelete(int rx, int rz) =>
        session.Execute(_ => new DeleteRegionCommand(new RegionCoord(rx, rz)));

    /// <summary>Stamps a prefab file at (x, z) as one undo step. A relative path resolves against the world's own
    /// directory.</summary>
    public MutationResult PrefabPlace(string prefabPath, int x, int z, int plane, int rotation = 0)
    {
        TilePrefab prefab = TilePrefabFile.Load(session.ResolvePath(prefabPath));
        return session.Execute(_ => TileEditOps.PlacePrefab(prefab, x, z, plane, rotation));
    }

    /// <summary>Extracts a rect of the world into a prefab FILE. This is the one verb here that is not a
    /// mutation: it reads the world, changes nothing about it, and writes a file, so there is no command and
    /// nothing to undo (deleting the file is the undo). It lives on this service because it WRITES, which is the
    /// distinction a client cares about. A relative path resolves against the world's own directory.</summary>
    public PrefabSaveResult PrefabExtract(TileRect rect, int planeFrom, int planeCount, string savePath,
        bool includeObjects = true, bool includeMarkers = true)
    {
        string resolved = session.ResolvePath(savePath);
        string name = System.IO.Path.GetFileNameWithoutExtension(resolved);
        TilePrefab prefab = session.Read(e => TilePrefabs.Extract(e.Document, e.Catalogs, rect, planeFrom,
            planeCount, includeObjects, includeMarkers, name));
        TilePrefabFile.Save(prefab, resolved);
        return new PrefabSaveResult(resolved, prefab.Name, prefab.Width, prefab.Height, prefab.PlaneCount,
            prefab.Objects.Count, prefab.Markers.Count, new FileInfo(resolved).Length);
    }

    // The ids a batch actually placed, in apply order. A child that is not a placement (there are none today,
    // but a composite is a general shape) contributes nothing rather than a zero.
    static IReadOnlyList<long> PlacedIds(CompositeCommand command) => command.Commands
        .OfType<PlaceObjectCommand>()
        .Where(c => c.ObjectId is not null)
        .Select(c => c.ObjectId!.Value)
        .ToArray();
}
