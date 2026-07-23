using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public sealed partial class EditorToolController
{
    // ---- duplicate -----------------------------------------------------------------------------------------

    /// <summary>World-unit XZ offset applied to a duplicate's position, so it never lands exactly on top of its
    /// source (Cmd+D, decision 8). The kinds with no position (a biome band, a scatter or companion layer)
    /// ignore it entirely.</summary>
    const float DuplicateOffset = 2f;

    /// <summary>Identifies what <see cref="DuplicateSelection"/> created: the duplicated kind, and its fresh key
    /// (the new id/name for a keyed kind, or the new index as a string for an index-keyed kind), the same shape
    /// <see cref="EditorSelection.Set"/> already takes for that kind. Lets a caller confirm a duplicate actually
    /// landed rather than inferring it from a void return.</summary>
    public readonly record struct DuplicateResult(SelectionKind Kind, string Id);

    /// <summary>Duplicates the current selection: a deep clone with a fresh unique identity, offset +2/+2 on X/Z
    /// for the kinds that carry a position, added through the same kind's Add command and immediately sealed
    /// (<see cref="EditorDocument.SealGesture"/>) before the new element becomes the selection. Sealing right
    /// after Execute matters: several Add commands absorb a same-id Move that immediately follows
    /// (place-and-adjust), and a duplicate is not a place gesture, so without the seal a later drag of the fresh
    /// duplicate could silently fold into its Add instead of landing its own undo step. Mirrors the
    /// <see cref="DeleteSelection"/> dispatcher shape, covering every kind Delete removes plus the two it does not
    /// handle (scatter and companion layers, which have no viewport geometry to delete but are still document
    /// elements a user wants to clone). Returns a <see cref="DuplicateResult"/> naming what got created, or null
    /// when nothing was duplicated: an empty selection, Terrain (the singleton root), or a custom feature type
    /// <see cref="FeatureGeometry.Translated"/> does not know how to offset. Both no-op cases no-op silently here,
    /// exactly like Delete's own default branch, and the null return is what lets a caller (the scene's Cmd+D
    /// chord, or an automation caller) tell "duplicated" from "silently skipped" apart instead of assuming
    /// success. The owning scene surfaces a status note for the Terrain case and for a skipped custom feature
    /// type (this controller carries no status text of its own).</summary>
    public DuplicateResult? DuplicateSelection()
    {
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Placement:
            {
                if (FindPlacement(sel.Id) is not { } p) return null;
                string id = UniqueName("placement", PlacementIdExists);
                _document.Execute(new AddPlacementCommand(new MapPlacement
                {
                    Id = id, Kind = p.Kind, X = p.X + DuplicateOffset, Z = p.Z + DuplicateOffset, Y = p.Y,
                    Yaw = p.Yaw, Scale = p.Scale, Tags = new List<string>(p.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Placement, id);
                return new DuplicateResult(SelectionKind.Placement, id);
            }
            case SelectionKind.Spawn:
            {
                if (FindSpawn(sel.Id) is not { } s) return null;
                string id = UniqueName("spawn", SpawnIdExists);
                _document.Execute(new AddSpawnCommand(new MapSpawn
                {
                    Id = id, ArchetypeId = s.ArchetypeId, X = s.X + DuplicateOffset, Z = s.Z + DuplicateOffset,
                    Enabled = s.Enabled, Tags = new List<string>(s.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Spawn, id);
                return new DuplicateResult(SelectionKind.Spawn, id);
            }
            case SelectionKind.PlayerSpawn:
            {
                if (FindPlayerSpawn(sel.Id) is not { } ps) return null;
                string id = UniqueName("player", PlayerSpawnIdExists);
                // AddPlayerSpawnCommand deep-copies at construction (a fresh Tags list), so handing it a plain new
                // instance here is enough: the command never aliases this local's Tags list either way.
                _document.Execute(new AddPlayerSpawnCommand(new MapPlayerSpawn
                {
                    Id = id, X = ps.X + DuplicateOffset, Z = ps.Z + DuplicateOffset, Yaw = ps.Yaw,
                    Enabled = ps.Enabled, Tags = new List<string>(ps.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.PlayerSpawn, id);
                return new DuplicateResult(SelectionKind.PlayerSpawn, id);
            }
            case SelectionKind.Feature:
            {
                if (!TryFeatureIndex(sel.Id, out int fi)) return null;
                MapFeature source = _document.Doc.Terrain.Features[fi];
                // FeatureGeometry.Translated already clones AND offsets the center / through-point atomically. It
                // returns null for a custom feature type it does not know how to translate (the same "unknown
                // type, no guess" policy TryCenter / Scaled already follow), so an unsupported type no-ops here
                // rather than adding an un-offset clone. The null return is the signal the owning scene checks to
                // surface its "cannot duplicate this feature type" status note.
                if (FeatureGeometry.Translated(source, DuplicateOffset, DuplicateOffset) is not { } clone) return null;
                // A feature Name is optional and unique-when-set (round 5), but AddFeatureCommand carries no
                // add-time guard for that (only RenameFeatureCommand does), so a straight clone of a named
                // feature would silently collide. Uniquify it, an unnamed feature's null Name carries no key to
                // collide on and needs no change.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", FeatureNameExists);
                _document.Execute(new AddFeatureCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Terrain.Features.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.Feature, key);
                return new DuplicateResult(SelectionKind.Feature, key);
            }
            case SelectionKind.Exclusion:
            {
                if (!TryExclusionIndex(sel.Id, out int ei)) return null;
                MapExclusion source = _document.Doc.Exclusions[ei];
                var clone = new MapExclusion
                {
                    Name = source.Name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    Layers = source.Layers is { } layers ? new List<string>(layers) : null,
                };
                // Same round-5 name-collision dodge as Feature above: AddExclusionCommand has no add-time guard.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", ExclusionNameExists);
                _document.Execute(new AddExclusionCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Exclusions.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.Exclusion, key);
                return new DuplicateResult(SelectionKind.Exclusion, key);
            }
            case SelectionKind.ScatterOverride:
            {
                if (!TryScatterOverrideIndex(sel.Id, out int oi)) return null;
                MapScatterOverrideDoc source = _document.Doc.ScatterOverrides[oi];
                var clone = new MapScatterOverrideDoc
                {
                    Name = source.Name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    DensityMultiplier = source.DensityMultiplier,
                    // Fresh lists AND fresh MapPropKind elements. EditScatterOverrideValuesCommand's own Clone copies
                    // the Kinds list but shares its elements by reference, so a straight reuse of that discipline
                    // here would leave the clone's kinds aliasing the source's. Rebuild each element (CloneKinds) so
                    // a later scrub of the duplicate's kind mix can never mutate the original's.
                    Kinds = source.Kinds is { } kinds ? CloneKinds(kinds) : null,
                    Layers = source.Layers is { } layers ? new List<string>(layers) : null,
                };
                // Same round-5 name-collision dodge as Feature / Exclusion: AddScatterOverrideCommand has no add-time
                // name guard (only RenameScatterOverrideCommand does), so a named clone uniquifies itself here. An
                // unnamed override's null Name carries no key to collide on and needs no change.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", ScatterOverrideNameExists);
                _document.Execute(new AddScatterOverrideCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.ScatterOverrides.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.ScatterOverride, key);
                return new DuplicateResult(SelectionKind.ScatterOverride, key);
            }
            case SelectionKind.Region:
            {
                if (RegionByName(sel.Id) is not { } source) return null;
                // A region's Name IS its identity (like a placement id), always set and always unique, so a
                // duplicate takes a fresh generated name exactly like a freshly drawn region rather than deriving
                // one from the source name.
                string name = UniqueName("region", RegionExists);
                var clone = new MapRegion
                {
                    Name = name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    Tags = new List<string>(source.Tags),
                };
                _document.Execute(new AddRegionCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Region, name);
                return new DuplicateResult(SelectionKind.Region, name);
            }
            case SelectionKind.BiomeBand:
            {
                if (!TryListIndex(sel.Id, _document.Doc.Terrain.Biomes.Count, out int bi)) return null;
                MapBiomeBand source = _document.Doc.Terrain.Biomes[bi];
                // No name, no position (a band is a world-Z range, not a placed element): a plain verbatim
                // clone, no uniquify, no offset.
                var clone = new MapBiomeBand
                {
                    Start = source.Start, End = source.End, Biome = source.Biome,
                    BaseHeight = source.BaseHeight, HillAmplitude = source.HillAmplitude,
                };
                _document.Execute(new AddBiomeBandCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Terrain.Biomes.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.BiomeBand, key);
                return new DuplicateResult(SelectionKind.BiomeBand, key);
            }
            case SelectionKind.ScatterLayer:
            {
                if (ScatterLayerByName(sel.Id) is not { } source) return null;
                MapScatterLayer clone = MapEditorScene.CloneScatterLayer(source);
                clone.Name = UniqueName(source.Name + "-copy", ScatterLayerNameExists);
                _document.Execute(new AddScatterLayerCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.ScatterLayer, clone.Name);
                return new DuplicateResult(SelectionKind.ScatterLayer, clone.Name);
            }
            case SelectionKind.CompanionLayer:
            {
                if (CompanionLayerByName(sel.Id) is not { } source) return null;
                MapCompanionLayer clone = MapEditorScene.CloneCompanionLayer(source);
                clone.Name = UniqueName(source.Name + "-copy", CompanionLayerNameExists);
                _document.Execute(new AddCompanionLayerCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.CompanionLayer, clone.Name);
                return new DuplicateResult(SelectionKind.CompanionLayer, clone.Name);
            }
            default:
                return null;   // Terrain (a singleton) and an empty selection: nothing to duplicate.
        }
    }
}
