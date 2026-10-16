using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The world's clearance block: authoring rules that keep tagged meshes clear of each other, checked
/// against the glb meshes on disk and enforced by <c>world_save</c>.</summary>
[McpServerToolType]
public sealed class ClearanceTools(QueryService query, MutationService mutate)
{
    [McpServerTool(Name = "clearance_get"), Description("Reads the world's clearance block: the mesh root relative to the world directory, the sampling cell size in metres, the waiver tag and the rules in authoring order. Returns null when the world declares no block.")]
    public ClearanceInfo? ClearanceGet()
        => ToolGuard.Guard(query.ClearanceGet);

    [McpServerTool(Name = "clearance_set"), Description("Creates the clearance block, or updates its mesh root, cell size and waiver tag while keeping its rules, as one undo step. The mesh root names the directory holding the archetypes' glb meshes, relative to the world directory and never rooted, because the world file is committed and shared.")]
    public MutationResult ClearanceSet(
        [Description("Mesh directory relative to the world directory, for example ../models. Archetype meshRef paths resolve under it.")] string meshRoot,
        [Description("Sampling cell edge in metres, from 0.02 to 1. Smaller finds thinner overlaps and costs more.")] float cellSize = 0.1f,
        [Description("Object tag that exempts an object from every rule, set with object_set_tags. Null for no waiver.")] string? waiverTag = null)
        => ToolGuard.Guard(() => mutate.ClearanceSet(meshRoot, cellSize, waiverTag));

    [McpServerTool(Name = "clearance_rule_set"), Description("Adds a clearance rule, or replaces the rule of the same name in place, as one undo step. Objects whose archetype carries any subject tag must keep their meshes clear of objects whose archetype carries any obstacle tag. Refuses when the world has no clearance block, so run clearance_set first.")]
    public MutationResult ClearanceRuleSet(
        [Description("Rule name, unique within the block.")] string name,
        [Description("Archetype tags that mark the objects this rule moves, at least one.")] string[] subjectTags,
        [Description("Archetype tags that mark the objects they must stay clear of, at least one.")] string[] obstacleTags,
        [Description("Penetration depth in metres the rule allows before it reports, zero or more.")] float tolerance = 0f)
        => ToolGuard.Guard(() => mutate.ClearanceRuleSet(name, subjectTags, obstacleTags, tolerance));

    [McpServerTool(Name = "clearance_rule_remove"), Description("Removes one clearance rule by name as one undo step. Refuses an unknown name.")]
    public MutationResult ClearanceRuleRemove([Description("Rule name.")] string name)
        => ToolGuard.Guard(() => mutate.ClearanceRuleRemove(name));

    [McpServerTool(Name = "clearance_check"), Description("Checks every placed object's mesh against the clearance rules and reports each issue as '[code] message' without writing anything. Codes are clearance.overlap for a subject inside an obstacle, clearance.mesh for a mesh root or mesh that cannot be read, and clearance.rule for a rule whose tags match no archetype. world_save refuses a world with any of them. Meshes are read once per open world, so a glb re-exported while the world is open is seen after world_open.")]
    public ClearanceCheckResult ClearanceCheck()
        => ToolGuard.Guard(query.ClearanceCheck);
}
