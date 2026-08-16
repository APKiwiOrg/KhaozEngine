using System.Collections.Generic;
using System.ComponentModel;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Scenery objects: place, move, rotate, remove, tag, look up, and the two batch placers (a line and a
/// deterministic scatter). Every method delegates to <see cref="QueryService"/> or <see cref="MutationService"/>
/// through <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>An object is one instance of a catalog ARCHETYPE anchored at a tile on a plane, turned in quarter
/// turns clockwise (0 west, 1 north, 2 east, 3 south). Its footprint follows from the archetype's size and that
/// rotation, so a 1x2 bench covers different tiles at rotation 0 and rotation 1. The document allocates the id,
/// and an undone removal comes back with the id it had, so a client's references keep resolving.</para></summary>
[McpServerToolType]
public sealed class ObjectTools(QueryService query, MutationService mutate)
{
    /// <summary>Places one object and reports its id.</summary>
    [McpServerTool(Name = "object_place"), Description("Places one object from a catalog archetype at a tile and reports the id the document allocated for it. The archetype's collision kind is what makes the tile a wall or a solid block afterwards. One undo step. Returns the mutation fields plus the new object id.")]
    public ObjectPlaceResult ObjectPlace(
        [Description("Archetype id from the catalog (see catalog_list with kind archetypes).")] string archetypeId,
        [Description("Anchor tile x (east).")] int x,
        [Description("Anchor tile z (north).")] int z,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south). Defaults to 0.")] int rotation = 0,
        [Description("Authoring tags to attach, for later lookup with object_find. Null means none.")] string[]? tags = null)
        => ToolGuard.Guard(() => mutate.ObjectPlace(archetypeId, x, z, plane, rotation, tags));

    /// <summary>Moves one object's anchor.</summary>
    [McpServerTool(Name = "object_move"), Description("Moves one object's anchor tile, and its plane with it. One undo step. Returns the undo label, the dirty flag, the undo depth, the new world hash and the rects touched, which cover both the old footprint and the new one.")]
    public MutationResult ObjectMove(
        [Description("The object id, as reported by object_place or a lookup verb.")] long id,
        [Description("New anchor tile x (east).")] int x,
        [Description("New anchor tile z (north).")] int z,
        [Description("New plane index.")] int plane)
        => ToolGuard.Guard(() => mutate.ObjectMove(id, x, z, plane));

    /// <summary>Turns one object in place.</summary>
    [McpServerTool(Name = "object_rotate"), Description("Turns one object in place. A non-square archetype covers different tiles afterwards, so the dirty rects cover both footprints. One undo step. Returns the mutation fields.")]
    public MutationResult ObjectRotate(
        [Description("The object id.")] long id,
        [Description("Quarter turns clockwise, 0 to 3 (0 west, 1 north, 2 east, 3 south).")] int rotation)
        => ToolGuard.Guard(() => mutate.ObjectRotate(id, rotation));

    /// <summary>Deletes one object.</summary>
    [McpServerTool(Name = "object_remove"), Description("Deletes one object. One undo step, and the undo puts it back with the id it had, so every reference still resolves. Returns the mutation fields, whose dirty rects cover the object's whole footprint.")]
    public MutationResult ObjectRemove(
        [Description("The object id.")] long id)
        => ToolGuard.Guard(() => mutate.ObjectRemove(id));

    /// <summary>Replaces one object's tags.</summary>
    [McpServerTool(Name = "object_set_tags"), Description("Replaces one object's authoring tags outright, with null meaning no tags at all. Tags are how a client finds again what it scattered. One undo step. Returns the mutation fields.")]
    public MutationResult ObjectSetTags(
        [Description("The object id.")] long id,
        [Description("The complete new tag list. Null or empty removes every tag.")] string[]? tags = null)
        => ToolGuard.Guard(() => mutate.ObjectSetTags(id, tags));

    /// <summary>One object by id.</summary>
    [McpServerTool(Name = "object_get"), Description("Reads one object by id: its archetype, anchor tile, plane, rotation, tags and the tile rect its rotated footprint covers. Fails when no object carries that id.")]
    public ObjectInfo ObjectGet(
        [Description("The object id.")] long id)
        => ToolGuard.Guard(() => query.ObjectGet(id));

    /// <summary>Every object anchored inside a rect.</summary>
    [McpServerTool(Name = "objects_in_rect"), Description("Lists every object whose ANCHOR tile falls inside the rect, in id order. An object anchored just outside but overhanging into the rect is not listed, so widen the rect by the largest archetype when that matters.")]
    public IReadOnlyList<ObjectInfo> ObjectsInRect(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, x + width exclusive.")] int width,
        [Description("Rect height in tiles, z + height exclusive.")] int height,
        [Description("Plane index to restrict to. Null lists every plane.")] int? plane = null)
        => ToolGuard.Guard(() => query.ObjectsInRect(new TileRect(x, z, width, height), plane));

    /// <summary>Every object matching an archetype, a tag, or both.</summary>
    [McpServerTool(Name = "object_find"), Description("Lists every object in the whole world matching an archetype id, a tag, or both, in id order. Both left null lists everything, which is what to call after a scatter to see what landed.")]
    public IReadOnlyList<ObjectInfo> ObjectFind(
        [Description("Archetype id to match exactly. Null matches any archetype.")] string? archetypeId = null,
        [Description("Tag the object must carry. Null matches regardless of tags.")] string? tag = null)
        => ToolGuard.Guard(() => query.ObjectFind(archetypeId, tag));

    /// <summary>One object per tile of a line.</summary>
    [McpServerTool(Name = "objects_line"), Description("Places one object per tile of the straight line between two tiles, both ends included, as a SINGLE undo step. This is the fence and wall-run verb. Returns the mutation fields plus how many landed and their ids, so the batch can be tagged or removed as a unit.")]
    public PlacementBatchResult ObjectsLine(
        [Description("Archetype id from the catalog.")] string archetypeId,
        [Description("Start tile x (east).")] int fromX,
        [Description("Start tile z (north).")] int fromZ,
        [Description("End tile x, included in the run.")] int toX,
        [Description("End tile z, included in the run.")] int toZ,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Quarter turns clockwise applied to every object, 0 to 3. Defaults to 0.")] int rotation = 0)
        => ToolGuard.Guard(() => mutate.ObjectLine(archetypeId, fromX, fromZ, toX, toZ, plane, rotation));

    /// <summary>A deterministic scatter over a rect.</summary>
    [McpServerTool(Name = "objects_scatter"), Description("Scatters one archetype over a rect as a SINGLE undo step: a grid at the given spacing, each point jittered from a hash of that point and the seed, skipping tiles that are blocked or already occupied. The same arguments always produce the same world, and an empty result is a legitimate answer for a crowded rect. Returns the mutation fields plus how many landed and their ids.")]
    public PlacementBatchResult ObjectsScatter(
        [Description("Archetype id from the catalog.")] string archetypeId,
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, x + width exclusive.")] int width,
        [Description("Rect height in tiles, z + height exclusive.")] int height,
        [Description("Plane index, 0 is the ground storey.")] int plane,
        [Description("Tiles between grid points before jitter. Larger means sparser.")] int spacing,
        [Description("Maximum tiles each point may be nudged from its grid position. 0 leaves a hard grid.")] int jitter,
        [Description("Seed for the jitter hash. The same seed and arguments always produce the same placement.")] int seed)
        => ToolGuard.Guard(() => mutate.ObjectScatter(archetypeId, new TileRect(x, z, width, height), plane,
            spacing, jitter, seed));
}
