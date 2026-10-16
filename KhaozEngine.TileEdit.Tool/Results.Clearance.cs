using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>One clearance rule in its detached MCP form: objects of an archetype tagged with any subject tag keep
/// their meshes clear of objects of an archetype tagged with any obstacle tag, allowing the tolerance in metres.</summary>
public sealed record ClearanceRuleInfo(string Name, string[] SubjectTags, string[] ObstacleTags, float Tolerance)
{
    /// <summary>A detached copy of <paramref name="rule"/>.</summary>
    public static ClearanceRuleInfo Of(TileClearanceRule rule) =>
        new(rule.Name, rule.SubjectTags.ToArray(), rule.ObstacleTags.ToArray(), rule.Tolerance);
}

/// <summary>A world's clearance block in its detached MCP form. The mesh root is relative to the world
/// directory, as the manifest stores it.</summary>
public sealed record ClearanceInfo(string MeshRoot, float CellSize, string? WaiverTag, ClearanceRuleInfo[] Rules)
{
    /// <summary>A detached copy of <paramref name="settings"/>.</summary>
    public static ClearanceInfo Of(TileClearanceSettings settings) =>
        new(settings.MeshRoot, settings.CellSize, settings.WaiverTag,
            settings.Rules.Select(ClearanceRuleInfo.Of).ToArray());
}

/// <summary>What <c>clearance_check</c> found: clear when there are no issues, otherwise every issue as
/// <c>[code] message</c>.</summary>
public sealed record ClearanceCheckResult(bool Clear, IReadOnlyList<string> Issues);
