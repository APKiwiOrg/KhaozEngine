using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Archetype collision heights: measure each archetype's model top from the kit's glb meshes, then write
/// the heights into the catalog files. Every method delegates to <see cref="ArchetypeHeightService"/> through
/// <see cref="ToolGuard.Guard{T}"/>.
///
/// <para>A physics consumer needs a height on every Solid, Diagonal, Wall and WallCorner archetype. Neither verb
/// touches the world or its undo history: measuring reads, and writing changes catalog FILES, each height in the one
/// file that defines its archetype, keeping every byte it does not change. Only <c>collisionHeight</c> is refreshed
/// in the open session afterwards. Other catalog edits made outside the tool need <c>world_open</c>.</para></summary>
[McpServerToolType]
public sealed class ArchetypeHeightTools(ArchetypeHeightService service)
{
    /// <summary>Measures every archetype's model top.</summary>
    [McpServerTool(Name = "archetype_measure_heights", ReadOnly = true), Description("Measures every archetype of the open catalogs from its glb mesh under a kit directory: the height is the model's top in metres above its local base (max Y of the mesh's vertices). There is no greybox fallback, so a missing or unreadable glb is an error entry for that archetype, and so is a top at or below 0, which needs a hand-set height. Each entry also carries the collision kind and the height the catalog already records. Writes nothing.")]
    public MeasureHeightsResult MeasureHeights(
        [Description("The kit directory each archetype's meshRef resolves under. A relative path resolves against the OPEN WORLD's directory. Must exist.")] string kitRoot)
        => ToolGuard.Guard(() => service.Measure(kitRoot));

    /// <summary>Writes collision heights into the catalog files.</summary>
    [McpServerTool(Name = "archetype_set_collision_heights"), Description("Writes collisionHeight into the catalog file that defines each archetype, changing only that property: a new one goes right after collisionKind in the entry's own layout, an existing one has only its value replaced, and comments, order, indentation and line endings stay. An archetype that already records a height is skipped unless overwrite is true. A height that is not a finite number above 0, or an id the open catalogs do not define, is an error entry and nothing is written for it. Afterwards ONLY collisionHeight is refreshed in the open session, so later verbs see the new heights, and any other catalog edit made outside the tool still needs world_open. If that refresh fails, the files written are still reported as changed and an error entry with an empty id says world_open is needed. Not an undo step. Returns each change with the file it went into, the skips and the errors.")]
    public CollisionHeightsResult SetCollisionHeights(
        [Description("The heights to write, each an archetype id and a height in metres above 0. The good entries of archetype_measure_heights pass straight through.")] ArchetypeHeight[] heights,
        [Description("When true, replace a height the catalog already records. Defaults to false, which keeps it.")] bool overwrite = false)
        => ToolGuard.Guard(() => service.SetCollisionHeights(heights, overwrite));
}
