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
        if (dto.Rules is null) throw new ArgumentException("Invalid clearance settings: rules cannot be null.");
        return new TileClearanceSettings(dto.MeshRoot, dto.CellSize ?? TileClearanceSettings.DefaultCellSize,
            dto.WaiverTag, dto.Rules.Select(r => r is null
                ? throw new ArgumentException("Invalid clearance settings: a rule entry cannot be null.")
                : new TileClearanceRule(r.Name, r.SubjectTags, r.ObstacleTags, r.Tolerance ?? 0f)).ToList());
    }

    static void ValidateClearanceDto(TileWorldClearanceDto? dto, string path)
    {
        if (dto is null) return;
        try { ClearanceFromDto(dto); }
        catch (ArgumentException ex)
        {
            throw new TileWorldException($"{path}: {ex.Message}", ex);
        }
    }
}
