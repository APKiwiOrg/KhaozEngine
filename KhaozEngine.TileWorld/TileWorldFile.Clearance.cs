using System;
using System.Linq;

namespace KhaozEngine.TileWorld;

public static partial class TileWorldFile
{
    // Every default is written out, so a saved block reads the same without knowing this engine's defaults.
    internal static TileWorldClearanceDto ClearanceToDto(TileClearanceSettings s) => new()
    {
        MeshRoot = s.MeshRoot,
        CellSize = s.CellSize,
        WaiverTag = s.WaiverTag,
        Rules = s.Rules.Select(r => new TileClearanceRuleDto
        {
            Name = r.Name,
            SubjectTags = r.SubjectTags.ToList(),
            ObstacleTags = r.ObstacleTags.ToList(),
            Tolerance = r.Tolerance,
        }).ToList(),
    };

    internal static TileClearanceSettings ClearanceFromDto(TileWorldClearanceDto dto)
    {
        if (dto.Rules is null)
            throw new TileClearanceArgumentException(TileWorldRefusal.ClearanceRulesNull,
                "Invalid clearance settings: rules cannot be null.", null)
            { Field = "rules" };
        return new TileClearanceSettings(dto.MeshRoot, dto.CellSize ?? TileClearanceSettings.DefaultCellSize,
            dto.WaiverTag, dto.Rules.Select((r, index) => r is null
                ? throw new TileClearanceArgumentException(TileWorldRefusal.ClearanceRuleNull,
                    "Invalid clearance settings: a rule entry cannot be null.", null)
                { RuleIndex = index }
                : new TileClearanceRule(r.Name, r.SubjectTags, r.ObstacleTags, r.Tolerance ?? 0f)).ToList());
    }

    static void ValidateClearanceDto(TileWorldClearanceDto? dto, string path)
    {
        if (dto is null) return;
        try { ClearanceFromDto(dto); }
        catch (TileClearanceArgumentException ex)
        {
            throw new TileWorldException(ex.Reason, $"{path}: {ex.Message}", ex)
            {
                SourceName = path,
                MeshRoot = ex.MeshRoot,
                CellSize = ex.CellSize,
                Minimum = ex.Minimum,
                Maximum = ex.Maximum,
                WaiverTag = ex.WaiverTag,
                RuleName = ex.RuleName,
                RuleCount = ex.RuleCount,
                RuleIndex = ex.RuleIndex,
                Field = ex.Field,
                Tolerance = ex.Tolerance,
                TagCount = ex.TagCount,
                TagIndex = ex.TagIndex,
                Tag = ex.Tag,
            };
        }
        catch (ArgumentNullException ex) when (ex.ParamName == "meshRoot")
        {
            throw new TileWorldException(TileWorldRefusal.ClearanceMeshRootNull, $"{path}: {ex.Message}", ex)
            { SourceName = path, Field = "meshRoot" };
        }
    }
}
