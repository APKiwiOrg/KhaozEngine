using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    // ---- scatter layers (terrain-scatter affecting) ------------------------------------------------------------

    /// <summary>Appends a named procedural scatter layer with no rules. Per-rule editing goes through the
    /// <see cref="ScatterRuleAdd"/>/<see cref="ScatterRuleEdit"/>/<see cref="ScatterRuleRemove"/> triad once the
    /// layer exists. The layer name must be unique in the document, rejected inside
    /// <see cref="AddScatterLayerCommand.Apply"/> before it mutates. Affects the streamed world.</summary>
    public MutationResult ScatterLayerAdd(string name, int seed = 1337, float cellSize = 4.5f, float jitter = 1.6f,
        float? maxHeight = null, float scaleMin = 0.8f, float scaleMax = 1.35f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var layer = new MapScatterLayer
        {
            Name = name,
            Seed = seed,
            CellSize = cellSize,
            Jitter = jitter,
            MaxHeight = maxHeight,
            ScaleMin = scaleMin,
            ScaleMax = scaleMax,
        };
        return Apply(new AddScatterLayerCommand(layer), "scatter_layer_add", $"added scatter layer '{name}'");
    }

    /// <summary>Edits a scatter layer's scalars by name, replacing only the supplied fields (a null argument
    /// leaves that field unchanged, the read-modify pattern the editor's per-field rows use). Rules are always
    /// carried through unchanged here: edit them with the <see cref="ScatterRuleAdd"/>/<see cref="ScatterRuleEdit"/>/
    /// <see cref="ScatterRuleRemove"/> triad instead. <paramref name="clearMaxHeight"/> forces MaxHeight back to
    /// unset (no height cap), taking precedence over <paramref name="maxHeight"/>, since a single nullable float
    /// parameter cannot otherwise distinguish "leave unchanged" from "clear to null". The whole edited value is
    /// deep-cloned so the command's new and old values never alias the same nested Rules list. Affects the
    /// streamed world.</summary>
    public MutationResult ScatterLayerEdit(string name, int? seed = null, float? cellSize = null,
        float? jitter = null, float? maxHeight = null, bool clearMaxHeight = false, float? scaleMin = null,
        float? scaleMax = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Apply((doc, _) =>
        {
            MapScatterLayer old = doc.ScatterLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                ?? throw new MapMutationException(MapDocumentRefusal.ReferenceMissing, $"No scatter layer named '{name}' in the document.")
                { DocumentId = doc.Id, EntityId = name, LayerId = name, SubjectKind = "scatter layer", FieldPath = "scatterLayers" };
            var newValue = new MapScatterLayer
            {
                Name = old.Name,
                Seed = seed ?? old.Seed,
                CellSize = cellSize ?? old.CellSize,
                Jitter = jitter ?? old.Jitter,
                MaxHeight = clearMaxHeight ? null : (maxHeight ?? old.MaxHeight),
                ScaleMin = scaleMin ?? old.ScaleMin,
                ScaleMax = scaleMax ?? old.ScaleMax,
                Rules = CloneRules(old.Rules),
            };
            return new EditScatterLayerCommand(name, newValue, old);
        }, "scatter_layer_edit", _ => $"edited scatter layer '{name}'", worldChanged: true);
    }

    /// <summary>Removes the scatter layer named <paramref name="name"/>. Rejected inside
    /// <see cref="RemoveScatterLayerCommand.Apply"/> before it mutates when a companion layer's HostLayer, or an
    /// exclusion/scatter-override explicit layer filter, still names it: the rejection message lists every
    /// referencing element. Affects the streamed world.</summary>
    public MutationResult ScatterLayerRemove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Apply(new RemoveScatterLayerCommand(name), "scatter_layer_remove", $"removed scatter layer '{name}'");
    }

    /// <summary>Renames a scatter layer, cascading the rename through every companion layer's HostLayer and every
    /// exclusion/scatter-override explicit layer filter that names it (<see cref="RenameScatterLayerCommand"/>),
    /// so the document stays valid and no reference is silently orphaned. The target name must be unique among
    /// scatter layers, rejected before it mutates. Renaming keeps the same props streaming, so it does not affect
    /// the streamed world. The cascaded reference count is recounted up front (inside the factory, under the same
    /// lock the command's own apply runs in) and appended to <see cref="MutationResult.Detail"/> when positive, so
    /// the caller sees at a glance whether the rename touched anything besides the layer itself.</summary>
    public MutationResult ScatterLayerRename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        int cascadedRefs = 0;
        MutationResult result = Apply((doc, _) =>
        {
            cascadedRefs = CountScatterLayerReferences(doc, oldName);
            return new RenameScatterLayerCommand(oldName, newName);
        }, "scatter_layer_rename", _ =>
        {
            string detail = $"renamed scatter layer '{oldName}' to '{newName}'";
            return cascadedRefs > 0 ? detail + $", cascaded {cascadedRefs} reference(s)" : detail;
        }, worldChanged: false);
        return result with { Id = newName };
    }

    // ---- scatter layer rules (terrain-scatter affecting) ---------------------------------------------------

    /// <summary>Appends a biome scatter rule to the named scatter layer, the write path for the per-biome
    /// density/kind rows the editor's rule surface edits. Reads the layer by name, deep-clones its Rules
    /// (<see cref="CloneRules"/>) with the new rule appended, and replaces the whole layer value through
    /// <see cref="EditScatterLayerCommand"/>, the same whole-value-edit path <see cref="ScatterLayerEdit"/>
    /// uses. The result reports the appended rule's index (rules are index-addressed, the same key
    /// <see cref="ScatterRuleEdit"/>/<see cref="ScatterRuleRemove"/> take). An unknown layer name throws before
    /// anything mutates. Affects the streamed world.</summary>
    public MutationResult ScatterRuleAdd(string layerName, string biome, float density = 0.55f,
        IReadOnlyList<string>? kinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        BiomeId parsedBiome = ParseBiome(biome);
        List<MapPropKind> parsedKinds = kinds is null ? new List<MapPropKind>() : ParseKinds(kinds);

        int addedIndex = -1;
        MutationResult result = Apply((doc, _) =>
        {
            MapScatterLayer old = FindScatterLayer(doc, layerName);
            List<MapBiomeScatterRule> newRules = CloneRules(old.Rules);
            newRules.Add(new MapBiomeScatterRule { Biome = parsedBiome, Density = density, Kinds = parsedKinds });
            addedIndex = newRules.Count - 1;
            return new EditScatterLayerCommand(layerName, WithRules(old, newRules), old);
        }, "scatter_rule_add", _ => $"added {biome} scatter rule to layer '{layerName}' at index {addedIndex}",
            worldChanged: true);
        return result with { Index = addedIndex };
    }

    /// <summary>Edits the scatter rule at <paramref name="ruleIndex"/> on the named scatter layer, replacing only
    /// the supplied fields (a null argument leaves that field unchanged, the same read-modify pattern
    /// <see cref="ScatterLayerEdit"/> uses for the layer's own scalars). At least one of
    /// <paramref name="biome"/>/<paramref name="density"/>/<paramref name="kinds"/> must be supplied. The index is
    /// range-checked against the layer's live Rules count up front. Routes through
    /// <see cref="EditScatterLayerCommand"/> the same way <see cref="ScatterRuleAdd"/> does. Affects the streamed
    /// world.</summary>
    public MutationResult ScatterRuleEdit(string layerName, int ruleIndex, string? biome = null,
        float? density = null, IReadOnlyList<string>? kinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        if (biome is null && density is null && kinds is null)
            throw new MapMutationArgumentException(MapDocumentRefusal.ToolMutationEmpty, "scatter_rule_edit needs at least one field to change.")
            { Operation = "scatter_rule_edit", LayerId = layerName, Index = ruleIndex };
        BiomeId? parsedBiome = biome is null ? null : ParseBiome(biome);

        MutationResult result = Apply((doc, _) =>
        {
            MapScatterLayer old = FindScatterLayer(doc, layerName);
            RequireIndexInRange(ruleIndex, old.Rules.Count, "scatter rule", nameof(ruleIndex));

            List<MapBiomeScatterRule> newRules = CloneRules(old.Rules);
            MapBiomeScatterRule oldRule = old.Rules[ruleIndex];
            newRules[ruleIndex] = new MapBiomeScatterRule
            {
                Biome = parsedBiome ?? oldRule.Biome,
                Density = density ?? oldRule.Density,
                Kinds = kinds is null ? CloneKinds(oldRule.Kinds) : ParseKinds(kinds),
            };
            return new EditScatterLayerCommand(layerName, WithRules(old, newRules), old);
        }, "scatter_rule_edit", _ => $"edited scatter rule {ruleIndex} on layer '{layerName}'", worldChanged: true);
        return result with { Index = ruleIndex };
    }

    /// <summary>Removes the scatter rule at <paramref name="ruleIndex"/> from the named scatter layer
    /// (range-checked up front). Routes through <see cref="EditScatterLayerCommand"/> the same way
    /// <see cref="ScatterRuleAdd"/> does. Affects the streamed world.</summary>
    public MutationResult ScatterRuleRemove(string layerName, int ruleIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        MutationResult result = Apply((doc, _) =>
        {
            MapScatterLayer old = FindScatterLayer(doc, layerName);
            RequireIndexInRange(ruleIndex, old.Rules.Count, "scatter rule", nameof(ruleIndex));

            List<MapBiomeScatterRule> newRules = CloneRules(old.Rules);
            newRules.RemoveAt(ruleIndex);
            return new EditScatterLayerCommand(layerName, WithRules(old, newRules), old);
        }, "scatter_rule_remove", _ => $"removed scatter rule {ruleIndex} from layer '{layerName}'",
            worldChanged: true);
        return result with { Index = ruleIndex };
    }

    /// <summary>Deep-clones a scatter layer's rule list (each rule's nested Kinds list cloned too), the same
    /// aliasing-safety discipline as <see cref="CloneKinds"/>.</summary>
    static List<MapBiomeScatterRule> CloneRules(List<MapBiomeScatterRule> rules)
    {
        var result = new List<MapBiomeScatterRule>(rules.Count);
        foreach (MapBiomeScatterRule r in rules)
            result.Add(new MapBiomeScatterRule { Biome = r.Biome, Density = r.Density, Kinds = CloneKinds(r.Kinds) });
        return result;
    }

    /// <summary>Finds the scatter layer named <paramref name="name"/>, throwing the same precise message
    /// <see cref="ScatterLayerEdit"/> uses when no layer carries that name.</summary>
    static MapScatterLayer FindScatterLayer(MapDocument doc, string name) =>
        doc.ScatterLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
            ?? throw new MapMutationException(MapDocumentRefusal.ReferenceMissing, $"No scatter layer named '{name}' in the document.")
            { DocumentId = doc.Id, EntityId = name, SubjectKind = "scatter layer", FieldPath = "ScatterLayers", LayerId = name };

    /// <summary>Builds a fresh <see cref="MapScatterLayer"/> carrying <paramref name="old"/>'s scalars and
    /// <paramref name="rules"/> as its Rules, the whole-value copy the <see cref="ScatterRuleAdd"/>/
    /// <see cref="ScatterRuleEdit"/>/<see cref="ScatterRuleRemove"/> triad hands to
    /// <see cref="EditScatterLayerCommand"/> so its new and old values never alias the same nested list.</summary>
    static MapScatterLayer WithRules(MapScatterLayer old, List<MapBiomeScatterRule> rules) => new()
    {
        Name = old.Name,
        Seed = old.Seed,
        CellSize = old.CellSize,
        Jitter = old.Jitter,
        MaxHeight = old.MaxHeight,
        ScaleMin = old.ScaleMin,
        ScaleMax = old.ScaleMax,
        Rules = rules,
    };

    /// <summary>Counts the document elements that reference the scatter layer named <paramref name="name"/>: a
    /// companion layer hosting it, or an exclusion/scatter-override whose explicit layer filter names it (a null
    /// "all layers" filter is not a named reference and is not counted). Mirrors the counting logic
    /// <see cref="RemoveScatterLayerCommand"/>'s own reference scan uses for its rejection message (that scan is
    /// private to <c>KhaozEngine.MapEditor</c>, so this recounts rather than calling it), so
    /// <see cref="ScatterLayerRename"/> can report how many references its cascade retargeted.</summary>
    static int CountScatterLayerReferences(MapDocument doc, string name)
    {
        int count = 0;
        foreach (MapCompanionLayer c in doc.CompanionLayers)
            if (string.Equals(c.HostLayer, name, StringComparison.Ordinal)) count++;
        foreach (MapExclusion e in doc.Exclusions)
            if (e.Layers is { } ls && ls.Contains(name)) count++;
        foreach (MapScatterOverrideDoc o in doc.ScatterOverrides)
            if (o.Layers is { } ls && ls.Contains(name)) count++;
        return count;
    }
}
