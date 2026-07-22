using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public sealed partial class EditorToolController
{
    /// <summary>What a <see cref="FreezeZone"/> committed: how many placements it froze and how many of each
    /// procedural collection it removed. Lets a caller report the outcome (the scene's status strip) or confirm the
    /// freeze actually landed rather than inferring it from a void return, the same role
    /// <see cref="DuplicateResult"/> plays for a duplicate.</summary>
    public readonly record struct FreezeZoneResult(
        int PlacementCount, int ScatterLayersRemoved, int CompanionLayersRemoved,
        int ExclusionsRemoved, int ScatterOverridesRemoved);

    /// <summary>Freezes the whole zone: bakes every scatter and companion layer across the document bounds into
    /// authored placements and removes all scatter layers, companion layers, exclusions, and scatter overrides, as a
    /// single undoable <see cref="FreezeZoneCommand"/> (sealed right after, so a later edit never coalesces into it).
    /// Returns a <see cref="FreezeZoneResult"/> describing what landed, or null when there is nothing to freeze (the
    /// document already has no scatter or companion layers), so no phantom undo entry is pushed and the caller can
    /// tell "froze" from "nothing to do" apart. Does not itself surface any status text (this controller carries
    /// none), so the owning scene reports the outcome.</summary>
    public FreezeZoneResult? FreezeZone()
    {
        MapDocument doc = _document.Doc;
        if (!FreezeZoneCommand.HasWork(doc)) return null;

        int scatter = doc.ScatterLayers.Count;
        int companion = doc.CompanionLayers.Count;
        int exclusions = doc.Exclusions.Count;
        int overrides = doc.ScatterOverrides.Count;
        int placementsBefore = doc.Placements.Count;

        _document.Execute(new FreezeZoneCommand(_document.Registry));
        _document.SealGesture();

        return new FreezeZoneResult(
            doc.Placements.Count - placementsBefore, scatter, companion, exclusions, overrides);
    }
}
