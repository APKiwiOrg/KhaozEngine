using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

// Identity lookup and uniqueness admission shared by reversible editor commands.
public abstract partial class EditorCommand
{
    private protected static MapPlacement FindPlacement(MapDocument doc, string id)
    {
        foreach (MapPlacement p in doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                return p;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No placement with id '{id}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "placement", EntityId = id, FieldPath = "Placements" };
    }

    private protected static MapSpawn FindSpawn(MapDocument doc, string id)
    {
        foreach (MapSpawn s in doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                return s;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No spawn with id '{id}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "spawn", EntityId = id, FieldPath = "Spawns" };
    }

    private protected static int IndexOfRegion(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.Regions.Count; i++)
            if (string.Equals(doc.Regions[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No region named '{name}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "region", EntityId = name, FieldPath = "Regions" };
    }

    private protected static void GuardNoPlacement(MapDocument doc, string id)
    {
        foreach (MapPlacement p in doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A placement with id '{id}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "placement", EntityId = id, FieldPath = "Placements" };
    }

    private protected static void GuardNoSpawn(MapDocument doc, string id)
    {
        foreach (MapSpawn s in doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A spawn with id '{id}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "spawn", EntityId = id, FieldPath = "Spawns" };
    }

    private protected static MapPlayerSpawn FindPlayerSpawn(MapDocument doc, string id)
    {
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                return s;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No player spawn with id '{id}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "player spawn", EntityId = id, FieldPath = "PlayerSpawns" };
    }

    private protected static void GuardNoPlayerSpawn(MapDocument doc, string id)
    {
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A player spawn with id '{id}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "player spawn", EntityId = id, FieldPath = "PlayerSpawns" };
    }

    /// <summary>Rejects a duplicate region name, on the add path (which passes -1, having nothing to exclude) and
    /// on the rename path, where <paramref name="exceptIndex"/> excludes the renaming region's own index (resolved
    /// by its current name at the call site) so a collapsed self-rename stays legal.</summary>
    private protected static void GuardNoRegion(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.Regions.Count; i++)
            if (i != exceptIndex && string.Equals(doc.Regions[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A region named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "region", EntityId = name, FieldPath = "Regions", Index = i };
    }

    /// <summary>Rejects a duplicate terrain feature name before <see cref="RenameFeatureCommand.Apply"/> mutates
    /// anything. Features are index-addressed (see <see cref="RenameFeatureCommand"/>), so the scan excludes the
    /// renaming feature's own index (renaming to its current name, or the empty-clearing case with another
    /// unnamed feature already present, both stay legal). A null or empty name never collides since an unnamed
    /// feature carries no key to clash on.</summary>
    private protected static void GuardNoFeatureName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapFeature> features = doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
            if (i != exceptIndex && string.Equals(features[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A terrain feature named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "terrain feature", EntityId = name, FieldPath = "Terrain.Features", Index = i };
    }

    /// <summary>Rejects a duplicate exclusion name before <see cref="RenameExclusionCommand.Apply"/> mutates
    /// anything. Exclusions are index-addressed (see <see cref="RenameExclusionCommand"/>), so the scan excludes
    /// the renaming exclusion's own index (renaming to its current name, or the empty-clearing case with another
    /// unnamed exclusion already present, both stay legal). A null or empty name never collides since an unnamed
    /// exclusion carries no key to clash on.</summary>
    private protected static void GuardNoExclusionName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapExclusion> exclusions = doc.Exclusions;
        for (int i = 0; i < exclusions.Count; i++)
            if (i != exceptIndex && string.Equals(exclusions[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"An exclusion named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "exclusion", EntityId = name, FieldPath = "Exclusions", Index = i };
    }

    /// <summary>Rejects a duplicate scatter override name before <see cref="RenameScatterOverrideCommand.Apply"/>
    /// mutates anything. Scatter overrides are index-addressed (see <see cref="RenameScatterOverrideCommand"/>),
    /// so the scan excludes the renaming override's own index (renaming to its current name, or the
    /// empty-clearing case with another unnamed override already present, both stay legal). A null or empty name
    /// never collides since an unnamed override carries no key to clash on.</summary>
    private protected static void GuardNoScatterOverrideName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapScatterOverrideDoc> overrides = doc.ScatterOverrides;
        for (int i = 0; i < overrides.Count; i++)
            if (i != exceptIndex && string.Equals(overrides[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A scatter override named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "scatter override", EntityId = name, FieldPath = "ScatterOverrides", Index = i };
    }

    private protected static int IndexOfScatterLayer(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.ScatterLayers.Count; i++)
            if (string.Equals(doc.ScatterLayers[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No scatter layer named '{name}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "scatter layer", EntityId = name, FieldPath = "ScatterLayers", LayerId = name };
    }

    private protected static int IndexOfCompanionLayer(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.CompanionLayers.Count; i++)
            if (string.Equals(doc.CompanionLayers[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new MapEditorCommandException(MapDocumentRefusal.ReferenceMissing, $"No companion layer named '{name}' in the document.")
        { DocumentId = doc.Id, SubjectKind = "companion layer", EntityId = name, FieldPath = "CompanionLayers", LayerId = name };
    }

    /// <summary>Rejects a duplicate scatter layer name. <paramref name="exceptIndex"/> excludes the renaming
    /// layer's own index (resolved by its current name at the call site), so a collapsed self-rename stays
    /// legal.</summary>
    private protected static void GuardNoScatterLayerName(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.ScatterLayers.Count; i++)
            if (i != exceptIndex && string.Equals(doc.ScatterLayers[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A scatter layer named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "scatter layer", EntityId = name, FieldPath = "ScatterLayers", LayerId = name, Index = i };
    }

    /// <summary>Rejects a duplicate companion layer name. <paramref name="exceptIndex"/> excludes the renaming
    /// layer's own index (resolved by its current name at the call site), so a collapsed self-rename stays
    /// legal.</summary>
    private protected static void GuardNoCompanionLayerName(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.CompanionLayers.Count; i++)
            if (i != exceptIndex && string.Equals(doc.CompanionLayers[i].Name, name, StringComparison.Ordinal))
                throw new MapEditorCommandException(MapDocumentRefusal.IdentityDuplicate, $"A companion layer named '{name}' already exists in the document.")
                { DocumentId = doc.Id, SubjectKind = "companion layer", EntityId = name, FieldPath = "CompanionLayers", LayerId = name, Index = i };
    }

    /// <summary>Lists the document elements that reference the scatter layer named <paramref name="name"/>: a
    /// companion layer hosting it, or an exclusion / scatter-override whose explicit layer filter names it (a
    /// null "all layers" filter is not a named reference and is skipped). Each entry reads validator-consistent
    /// (host layer / layer filter), so a <see cref="RemoveScatterLayerCommand"/> rejection message lists exactly
    /// what the standard validator's unknown-layer rules would flag if the layer vanished under them.</summary>
    private protected static List<MapEditorScatterLayerReference> ScatterLayerReferences(MapDocument doc, string name)
    {
        var refs = new List<MapEditorScatterLayerReference>();
        foreach (MapCompanionLayer c in doc.CompanionLayers)
            if (string.Equals(c.HostLayer, name, StringComparison.Ordinal))
                refs.Add(new(c.Name, $"companion layer '{c.Name}' (host layer)"));
        for (int i = 0; i < doc.Exclusions.Count; i++)
            if (doc.Exclusions[i].Layers is { } ls && ls.Contains(name))
                refs.Add(new(FormattableString.Invariant($"Exclusions[{i}]"), $"exclusion[{i}] (layer filter)"));
        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
            if (doc.ScatterOverrides[i].Layers is { } ls && ls.Contains(name))
                refs.Add(new(FormattableString.Invariant($"ScatterOverrides[{i}]"), $"scatter override[{i}] (layer filter)"));
        return refs;
    }
}
