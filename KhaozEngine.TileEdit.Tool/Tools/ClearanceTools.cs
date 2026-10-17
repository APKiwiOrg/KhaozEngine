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

    [McpServerTool(Name = "clearance_set"), Description("Creates the clearance block, or replaces its header while keeping its rules, as one undo step. Every call sets all three of the mesh root, the cell size and the waiver tag, and an omitted cellSize or waiverTag resets to its default rather than keeping the current value, so pass the current cellSize and waiverTag (read them with clearance_get) to keep them. The mesh root names the directory holding the archetypes' glb meshes, relative to the world directory and never rooted, because the world file is committed and shared.")]
    public MutationResult ClearanceSet(
        [Description("Mesh directory relative to the world directory, for example ../models. Archetype meshRef paths resolve under it.")] string meshRoot,
        [Description("Sampling cell edge in metres, from 0.02 to 1. Smaller finds thinner overlaps and costs more. Omitted means 0.1, even when the block already had another value.")] float cellSize = 0.1f,
        [Description("Object tag that exempts an object from every rule, set with object_set_tags. Omitted or null means no waiver tag, even when the block already had one, so every previously waived object is checked again.")] string? waiverTag = null)
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

    [McpServerTool(Name = "clearance_check"), Description("Checks every placed object's mesh against the clearance rules and reports each issue as '[code] message' without writing anything. Codes are clearance.overlap for a subject inside an obstacle, clearance.mesh for a mesh root or mesh that cannot be read, and clearance.rule for a rule whose tags match no archetype. world_save refuses a world with any of them. Each glb is re-read when its last write time or length changes, so a glb exported or re-exported while the world is open is seen at the next check.")]
    public ClearanceCheckResult ClearanceCheck()
        => ToolGuard.Guard(query.ClearanceCheck);
}
