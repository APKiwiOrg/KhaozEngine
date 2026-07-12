using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Mutates the session's open document: placements, spawns, regions, terrain globals, terrain features
/// and biome bands, exclusions, scatter overrides, scatter and companion layers, and region bake. Command-backed
/// mutations route through
/// <see cref="Apply(EditorCommand, string, string)"/> (or its factory overload,
/// <see cref="Apply(Func{MapDocument, MapDocRegistry, EditorCommand}, string, Func{EditorCommand, string}, bool)"/>,
/// for verbs that need a document read to build the command): apply the <see cref="EditorCommand"/>, validate the
/// document, and on any validation error revert the command and throw, so the in-session document is never left
/// invalid. The few mutations with no command (terrain seed, scatter overrides, region bake) reproduce that same
/// apply, validate, revert-on-error shape by hand inside one <see cref="MapEditSession.Mutate{T}"/> callback. A
/// rejected mutation leaves the session exactly as it was before the attempt, including the dirty flag, because
/// <see cref="MapEditSession.Mutate{T}"/> only marks dirty when its callback returns normally and a rejected
/// mutation throws out of that callback.</summary>
public sealed class MutationService(MapEditSession session)
{
    /// <summary>The shared mutation choke point used by every verb below: applies <paramref name="command"/> to
    /// the open document, validates the result with <see cref="MapDocumentValidator"/>, and on any validation
    /// error reverts the command and throws <see cref="InvalidOperationException"/> with the joined errors.
    /// <see cref="MutationResult.WorldChanged"/> mirrors the command's AffectsWorld flag, which also selects
    /// whether the session's cached terrain field is invalidated by this mutation.</summary>
    internal MutationResult Apply(EditorCommand command, string verb, string detail)
    {
        ArgumentNullException.ThrowIfNull(command);
        bool worldChanged = command.AffectsWorld;
        return session.Mutate((doc, registry) =>
        {
            command.Apply(doc);
            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }
            return new MutationResult(verb, detail, worldChanged);
        }, worldChanged);
    }

    /// <summary>Overload of <see cref="Apply(EditorCommand, string, string)"/> for verbs whose command needs a
    /// document read to construct (a precondition lookup, or a captured "old" value the command needs to be
    /// reversible). <paramref name="factory"/> runs INSIDE the <see cref="MapEditSession.Mutate{T}"/> callback,
    /// so the read, the apply, the validate, and any revert all happen under the same lock acquisition: no other
    /// call can mutate the document between the read and the apply. <paramref name="worldChanged"/> must equal
    /// the constructed command's <see cref="EditorCommand.AffectsWorld"/>. <see cref="MapEditSession.Mutate{T}"/>
    /// needs that flag before the callback runs (to decide whether to invalidate the cached field), so it cannot
    /// be read off the command itself, and a mismatch throws rather than silently mis-tagging the mutation.</summary>
    internal MutationResult Apply(Func<MapDocument, MapDocRegistry, EditorCommand> factory, string verb,
        Func<EditorCommand, string> detail, bool worldChanged)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(detail);
        return session.Mutate((doc, registry) =>
        {
            EditorCommand command = factory(doc, registry);
            if (command.AffectsWorld != worldChanged)
            {
                throw new InvalidOperationException(
                    $"internal error: Apply was called with worldChanged={worldChanged} but " +
                    $"{command.GetType().Name}.AffectsWorld={command.AffectsWorld}.");
            }

            command.Apply(doc);
            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }
            return new MutationResult(verb, detail(command), worldChanged);
        }, worldChanged);
    }

    // ---- placements ---------------------------------------------------------------------------------------

    /// <summary>Adds an authored placement. When <paramref name="id"/> is null, auto-generates
    /// <c>p-&lt;kind&gt;-N</c> with the smallest N &gt;= 1 unique against existing placement ids. When
    /// <paramref name="y"/> is null the placement keeps a null Y (ground-snap at load), and either way the result
    /// reports <see cref="MutationResult.GroundY"/> as the field's sampled height at (x, z), so the caller always
    /// sees the resolved height.</summary>
    public MutationResult PlacementAdd(string kind, float x, float z, float? y = null,
        float yaw = 0f, float scale = 1f, string? id = null, IReadOnlyList<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        float groundY = session.Field().SampleHeight(x, z);
        string placementId = id ?? session.WithDocument((doc, _) =>
            GenerateId(doc.Placements.Select(p => p.Id), $"p-{kind}-"));

        var placement = new MapPlacement
        {
            Id = placementId,
            Kind = kind,
            X = x,
            Z = z,
            Y = y,
            Yaw = yaw,
            Scale = scale,
            Tags = tags is null ? new List<string>() : new List<string>(tags),
        };

        MutationResult result = Apply(new AddPlacementCommand(placement), "placement_add",
            $"placed {kind} at ({x:F1}, {z:F1}) ground {groundY:F2}");
        return result with { GroundY = groundY, Id = placementId };
    }

    /// <summary>Moves a placement to a new XZ. When <paramref name="y"/> is provided it passes straight through.
    /// When <paramref name="y"/> is null and <paramref name="keepExplicitY"/> is true, the placement's current Y
    /// is preserved (the gizmo's drag policy). The default (<paramref name="y"/> null, flag false) forces a null
    /// Y, re-snapping to ground, matching the R-key behavior.</summary>
    public MutationResult PlacementMove(string id, float x, float z, float? y = null, bool keepExplicitY = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        float? newY = y;
        if (newY is null && keepExplicitY)
        {
            newY = session.WithDocument((doc, _) =>
                doc.Placements.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))?.Y);
        }

        return Apply(new MovePlacementCommand(id, x, z, newY), "placement_move",
            $"moved placement {id} to ({x:F1}, {z:F1})");
    }

    /// <summary>Sets a placement's yaw.</summary>
    public MutationResult PlacementRotate(string id, float yaw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RotatePlacementCommand(id, yaw), "placement_rotate",
            $"rotated placement {id} to yaw {yaw:F2}");
    }

    /// <summary>Sets a placement's uniform scale.</summary>
    public MutationResult PlacementScale(string id, float scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new ScalePlacementCommand(id, scale), "placement_scale",
            $"scaled placement {id} to {scale:F2}");
    }

    /// <summary>Renames a placement. The target id must be unique in the document (validated at the choke
    /// point).</summary>
    public MutationResult PlacementRename(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        MutationResult result = Apply(new RenamePlacementCommand(oldId, newId), "placement_rename",
            $"renamed placement {oldId} to {newId}");
        return result with { Id = newId };
    }

    /// <summary>Removes a placement by id.</summary>
    public MutationResult PlacementRemove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RemovePlacementCommand(id), "placement_remove", $"removed placement {id}");
    }

    // ---- spawns ---------------------------------------------------------------------------------------------

    /// <summary>Adds an NPC spawn marker. When <paramref name="id"/> is null, auto-generates
    /// <c>s-&lt;archetypeId&gt;-N</c> with the smallest N &gt;= 1 unique against existing spawn ids.</summary>
    public MutationResult SpawnAdd(string archetypeId, float x, float z, bool enabled = true,
        string? id = null, IReadOnlyList<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);

        string spawnId = id ?? session.WithDocument((doc, _) =>
            GenerateId(doc.Spawns.Select(s => s.Id), $"s-{archetypeId}-"));

        var spawn = new MapSpawn
        {
            Id = spawnId,
            ArchetypeId = archetypeId,
            X = x,
            Z = z,
            Enabled = enabled,
            Tags = tags is null ? new List<string>() : new List<string>(tags),
        };

        MutationResult result = Apply(new AddSpawnCommand(spawn), "spawn_add",
            $"added spawn {archetypeId} at ({x:F1}, {z:F1})");
        return result with { Id = spawnId };
    }

    /// <summary>Moves a spawn to a new XZ.</summary>
    public MutationResult SpawnMove(string id, float x, float z)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new MoveSpawnCommand(id, x, z), "spawn_move", $"moved spawn {id} to ({x:F1}, {z:F1})");
    }

    /// <summary>Toggles a spawn's enabled flag.</summary>
    public MutationResult SpawnSetEnabled(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string state = enabled ? "enabled" : "disabled";
        return Apply(new SetSpawnEnabledCommand(id, enabled), "spawn_set_enabled", $"set spawn {id} {state}");
    }

    /// <summary>Renames a spawn. The target id must be unique in the document (validated at the choke
    /// point).</summary>
    public MutationResult SpawnRename(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        MutationResult result = Apply(new RenameSpawnCommand(oldId, newId), "spawn_rename",
            $"renamed spawn {oldId} to {newId}");
        return result with { Id = newId };
    }

    /// <summary>Removes a spawn by id.</summary>
    public MutationResult SpawnRemove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RemoveSpawnCommand(id), "spawn_remove", $"removed spawn {id}");
    }

    // ---- regions --------------------------------------------------------------------------------------------

    /// <summary>Adds a named region marker.</summary>
    public MutationResult RegionAdd(string name, MapShapeDoc shape, IReadOnlyList<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shape);

        var region = new MapRegion
        {
            Name = name,
            Shape = shape,
            Tags = tags is null ? new List<string>() : new List<string>(tags),
        };

        return Apply(new AddRegionCommand(region), "region_add", $"added region {name}");
    }

    /// <summary>Replaces a region's shape. Looks up the region and captures its current shape (
    /// <see cref="EditRegionShapeCommand"/> needs both the new and old shape to be reversible) inside the
    /// factory overload of the choke point, so the read and the apply+validate+revert happen under the same
    /// lock acquisition. Otherwise, a concurrent mutation of the same region between a separate read and the
    /// eventual apply could make the captured old shape stale, and a validation-rejected edit would revert to
    /// that stale shape and silently clobber the concurrent change.</summary>
    public MutationResult RegionEditShape(string name, MapShapeDoc shape)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shape);

        return Apply((doc, _) =>
        {
            MapRegion region = doc.Regions.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No region named '{name}' in the document.");
            MapShapeDoc oldShape = region.Shape
                ?? throw new InvalidOperationException($"region '{name}' has no shape to edit.");
            return new EditRegionShapeCommand(name, shape, oldShape);
        }, "region_edit_shape", _ => $"edited region {name} shape", worldChanged: false);
    }

    /// <summary>Renames a region. The target name must be unique in the document (validated at the choke
    /// point).</summary>
    public MutationResult RegionRename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        MutationResult result = Apply(new RenameRegionCommand(oldName, newName), "region_rename",
            $"renamed region {oldName} to {newName}");
        return result with { Id = newName };
    }

    /// <summary>Removes a region by name.</summary>
    public MutationResult RegionRemove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Apply(new RemoveRegionCommand(name), "region_remove", $"removed region {name}");
    }

    // ---- terrain globals ------------------------------------------------------------------------------------

    /// <summary>Edits the terrain globals: the water level, the noise seed, the biome-edge blend distance, and
    /// the gentle/detail noise scalars, all carried by the widened <see cref="EditTerrainCommand"/> (only the
    /// supplied fields are applied and reverted). At least one must be supplied. The one command is applied,
    /// validated once, and reverted on failure inside a single world-affecting mutation, so a rejected edit
    /// restores every touched field to its prior value and the cached field is rebuilt only on success.</summary>
    public MutationResult TerrainEdit(float? waterLevel = null, int? seed = null, float? biomeBlend = null,
        float? gentleFrequency = null, float? gentleAmplitude = null, float? detailFrequency = null,
        int? detailOctaves = null)
    {
        if (waterLevel is null && seed is null && biomeBlend is null && gentleFrequency is null &&
            gentleAmplitude is null && detailFrequency is null && detailOctaves is null)
            throw new ArgumentException("terrain_edit needs at least one field to change.");

        return session.Mutate((doc, registry) =>
        {
            MapTerrain t = doc.Terrain;
            // One widened command carries whichever fields were supplied (only-set-fields apply), so each scalar
            // reverts independently on a validation failure.
            var command = new EditTerrainCommand(
                newWaterLevel: waterLevel, oldWaterLevel: waterLevel is null ? null : t.WaterLevel,
                newSeed: seed, oldSeed: seed is null ? null : t.Seed,
                newBiomeBlend: biomeBlend, oldBiomeBlend: biomeBlend is null ? null : t.BiomeBlend,
                newGentleFrequency: gentleFrequency, oldGentleFrequency: gentleFrequency is null ? null : t.GentleFrequency,
                newGentleAmplitude: gentleAmplitude, oldGentleAmplitude: gentleAmplitude is null ? null : t.GentleAmplitude,
                newDetailFrequency: detailFrequency, oldDetailFrequency: detailFrequency is null ? null : t.DetailFrequency,
                newDetailOctaves: detailOctaves, oldDetailOctaves: detailOctaves is null ? null : t.DetailOctaves);
            command.Apply(doc);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            var parts = new List<string>(7);
            if (waterLevel is float w) parts.Add($"water level {w.ToString("F2", CultureInfo.InvariantCulture)}");
            if (seed is int s) parts.Add($"seed {s.ToString(CultureInfo.InvariantCulture)}");
            if (biomeBlend is float bb) parts.Add($"biome blend {bb.ToString("F2", CultureInfo.InvariantCulture)}");
            if (gentleFrequency is float gf) parts.Add($"gentle frequency {gf.ToString("F3", CultureInfo.InvariantCulture)}");
            if (gentleAmplitude is float ga) parts.Add($"gentle amplitude {ga.ToString("F2", CultureInfo.InvariantCulture)}");
            if (detailFrequency is float df) parts.Add($"detail frequency {df.ToString("F3", CultureInfo.InvariantCulture)}");
            if (detailOctaves is int oct) parts.Add($"detail octaves {oct.ToString(CultureInfo.InvariantCulture)}");
            return new MutationResult("terrain_edit", "set " + string.Join(" and ", parts), WorldChanged: true);
        }, worldChanged: true);
    }

    // ---- terrain biome bands (terrain-shape affecting) -------------------------------------------------------

    /// <summary>Appends a terrain biome band (an elevation-range biome slice). The result reports the appended
    /// index (bands are index-addressed, the same key <see cref="BiomeBandEdit"/>/<see cref="BiomeBandRemove"/>
    /// take). Affects the streamed world (bands feed biome selection and height shaping).</summary>
    public MutationResult BiomeBandAdd(float? start = null, float? end = null, string biome = "Meadow",
        float baseHeight = 0f, float hillAmplitude = 0f)
    {
        BiomeId parsedBiome = ParseBiome(biome);
        int addedIndex = -1;
        MutationResult result = Apply((doc, _) =>
        {
            addedIndex = doc.Terrain.Biomes.Count;   // appended at the current tail
            var band = new MapBiomeBand
            {
                Start = start, End = end, Biome = parsedBiome, BaseHeight = baseHeight, HillAmplitude = hillAmplitude,
            };
            return new AddBiomeBandCommand(band);
        }, "biome_band_add", _ => $"added {biome} biome band at index {addedIndex}", worldChanged: true);
        return result with { Index = addedIndex };
    }

    /// <summary>Replaces the terrain biome band at <paramref name="index"/> with a new whole value: every field
    /// must be supplied (there is no partial-field "leave unchanged" here, unlike the scatter/companion layer
    /// edits), matching how the editor's inspector always writes the whole band on a scrub. The index is
    /// range-checked at this service layer up front, since <see cref="EditBiomeBandCommand"/> itself indexes the
    /// live list with no guard of its own. Affects the streamed world.</summary>
    public MutationResult BiomeBandEdit(int index, float? start, float? end, string biome, float baseHeight,
        float hillAmplitude)
    {
        BiomeId parsedBiome = ParseBiome(biome);
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Terrain.Biomes.Count, "biome band", nameof(index));
            MapBiomeBand old = doc.Terrain.Biomes[index];
            var newValue = new MapBiomeBand
            {
                Start = start, End = end, Biome = parsedBiome, BaseHeight = baseHeight, HillAmplitude = hillAmplitude,
            };
            return new EditBiomeBandCommand(index, newValue, old);
        }, "biome_band_edit", _ => $"edited biome band at index {index}", worldChanged: true);
        return result with { Index = index };
    }

    /// <summary>Removes the terrain biome band at <paramref name="index"/> (range-checked up front, matching
    /// <see cref="RemoveBiomeBandCommand"/>'s own guard for a consistent message). Affects the streamed
    /// world.</summary>
    public MutationResult BiomeBandRemove(int index)
    {
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Terrain.Biomes.Count, "biome band", nameof(index));
            return new RemoveBiomeBandCommand(index);
        }, "biome_band_remove", _ => $"removed biome band at index {index}", worldChanged: true);
        return result with { Index = index };
    }

    // ---- scatter layers (terrain-scatter affecting) ------------------------------------------------------------

    /// <summary>Appends a named procedural scatter layer with no rules (rule editing is not exposed through MCP
    /// this round, mirroring the editor's deliberately v1-crude rule surface, so add rules through the editor).
    /// The layer name must be unique in the document, rejected inside <see cref="AddScatterLayerCommand.Apply"/>
    /// before it mutates. Affects the streamed world.</summary>
    public MutationResult ScatterLayerAdd(string name, int seed = 1337, float cellSize = 4.5f, float jitter = 1.6f,
        float? maxHeight = null, float scaleMin = 0.8f, float scaleMax = 1.35f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var layer = new MapScatterLayer
        {
            Name = name, Seed = seed, CellSize = cellSize, Jitter = jitter, MaxHeight = maxHeight,
            ScaleMin = scaleMin, ScaleMax = scaleMax,
        };
        return Apply(new AddScatterLayerCommand(layer), "scatter_layer_add", $"added scatter layer '{name}'");
    }

    /// <summary>Edits a scatter layer's scalars by name, replacing only the supplied fields (a null argument
    /// leaves that field unchanged, the read-modify pattern the editor's per-field rows use). Rules are always
    /// carried through unchanged (not exposed through MCP this round). <paramref name="clearMaxHeight"/> forces
    /// MaxHeight back to unset (no height cap), taking precedence over <paramref name="maxHeight"/>, since a
    /// single nullable float parameter cannot otherwise distinguish "leave unchanged" from "clear to null". The
    /// whole edited value is deep-cloned so the command's new and old values never alias the same nested Rules
    /// list. Affects the streamed world.</summary>
    public MutationResult ScatterLayerEdit(string name, int? seed = null, float? cellSize = null,
        float? jitter = null, float? maxHeight = null, bool clearMaxHeight = false, float? scaleMin = null,
        float? scaleMax = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Apply((doc, _) =>
        {
            MapScatterLayer old = doc.ScatterLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No scatter layer named '{name}' in the document.");
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
    /// the streamed world.</summary>
    public MutationResult ScatterLayerRename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        MutationResult result = Apply(new RenameScatterLayerCommand(oldName, newName), "scatter_layer_rename",
            $"renamed scatter layer '{oldName}' to '{newName}'");
        return result with { Id = newName };
    }

    // ---- companion layers (terrain-scatter affecting) ----------------------------------------------------------

    /// <summary>Appends a named companion layer ringing hosts from a scatter layer. The layer name must be unique
    /// in the document, rejected inside <see cref="AddCompanionLayerCommand.Apply"/> before it mutates.
    /// <paramref name="hostLayer"/> naming an unknown scatter layer is not rejected there: the standard document
    /// validator at the choke point catches it (the same invariant the editor's HostLayer chooser relies on
    /// live, but MCP has no live chooser, so a bad name surfaces as a validation rejection instead).
    /// <paramref name="kinds"/> entries parse as <c>"id"</c> (weight 1) or <c>"id:weight"</c>. Affects the
    /// streamed world.</summary>
    public MutationResult CompanionLayerAdd(string name, string hostLayer, int seed = 1337,
        IReadOnlyList<string>? hostKinds = null, IReadOnlyList<string>? kinds = null,
        int countMin = 2, int countMax = 4, float radiusMin = 0.6f, float radiusMax = 1.8f,
        float scaleMin = 0.7f, float scaleMax = 1.1f, float? maxHeight = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostLayer);

        var layer = new MapCompanionLayer
        {
            Name = name, HostLayer = hostLayer, Seed = seed,
            HostKinds = hostKinds is null ? new List<string>() : new List<string>(hostKinds),
            Kinds = kinds is null ? new List<MapPropKind>() : ParseKinds(kinds),
            CountMin = countMin, CountMax = countMax, RadiusMin = radiusMin, RadiusMax = radiusMax,
            ScaleMin = scaleMin, ScaleMax = scaleMax, MaxHeight = maxHeight,
        };
        return Apply(new AddCompanionLayerCommand(layer), "companion_layer_add", $"added companion layer '{name}'");
    }

    /// <summary>Edits a companion layer by name, replacing only the supplied fields (a null argument leaves that
    /// field unchanged, the read-modify pattern the editor's per-field rows use). <paramref name="clearMaxHeight"/>
    /// forces MaxHeight back to unset, taking precedence over <paramref name="maxHeight"/>, the same idiom
    /// <see cref="ScatterLayerEdit"/> uses. The whole edited value is deep-cloned so the command's new and old
    /// values never alias the same nested Kinds list. Affects the streamed world.</summary>
    public MutationResult CompanionLayerEdit(string name, string? hostLayer = null, int? seed = null,
        IReadOnlyList<string>? hostKinds = null, IReadOnlyList<string>? kinds = null,
        int? countMin = null, int? countMax = null, float? radiusMin = null, float? radiusMax = null,
        float? scaleMin = null, float? scaleMax = null, float? maxHeight = null, bool clearMaxHeight = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Apply((doc, _) =>
        {
            MapCompanionLayer old = doc.CompanionLayers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No companion layer named '{name}' in the document.");
            var newValue = new MapCompanionLayer
            {
                Name = old.Name,
                HostLayer = hostLayer ?? old.HostLayer,
                Seed = seed ?? old.Seed,
                HostKinds = hostKinds is null ? new List<string>(old.HostKinds) : new List<string>(hostKinds),
                Kinds = kinds is null ? CloneKinds(old.Kinds) : ParseKinds(kinds),
                CountMin = countMin ?? old.CountMin,
                CountMax = countMax ?? old.CountMax,
                RadiusMin = radiusMin ?? old.RadiusMin,
                RadiusMax = radiusMax ?? old.RadiusMax,
                ScaleMin = scaleMin ?? old.ScaleMin,
                ScaleMax = scaleMax ?? old.ScaleMax,
                MaxHeight = clearMaxHeight ? null : (maxHeight ?? old.MaxHeight),
            };
            return new EditCompanionLayerCommand(name, newValue, old);
        }, "companion_layer_edit", _ => $"edited companion layer '{name}'", worldChanged: true);
    }

    /// <summary>Removes the companion layer named <paramref name="name"/>. Nothing else in the document
    /// references a companion layer by name, so unlike <see cref="ScatterLayerRemove"/> there is no
    /// referenced-removal to reject. Affects the streamed world.</summary>
    public MutationResult CompanionLayerRemove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Apply(new RemoveCompanionLayerCommand(name), "companion_layer_remove", $"removed companion layer '{name}'");
    }

    /// <summary>Renames a companion layer. Nothing references a companion layer by name (they are leaf
    /// consumers), so unlike <see cref="ScatterLayerRename"/> there is no cascade. The target name must be
    /// unique among companion layers, rejected before it mutates. Renaming changes nothing streamed, so it does
    /// not affect the world.</summary>
    public MutationResult CompanionLayerRename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        MutationResult result = Apply(new RenameCompanionLayerCommand(oldName, newName), "companion_layer_rename",
            $"renamed companion layer '{oldName}' to '{newName}'");
        return result with { Id = newName };
    }

    // ---- terrain features (terrain-shape affecting) ---------------------------------------------------------

    /// <summary>Appends a terrain feature parsed from <paramref name="featureJson"/> with the document's own
    /// serializer options (an unknown discriminator throws <see cref="System.Text.Json.JsonException"/> from
    /// <see cref="DocJson.ParseFeature"/> before the choke point runs). The result's
    /// <see cref="MutationResult.Index"/> is the appended feature's list position.</summary>
    public MutationResult FeatureAdd(string featureJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureJson);

        int addedIndex = -1;
        MapFeature? parsed = null;
        MutationResult result = Apply((doc, registry) =>
        {
            parsed = DocJson.ParseFeature(featureJson, registry);
            addedIndex = doc.Terrain.Features.Count;   // appended at the current tail
            return new AddFeatureCommand(parsed);
        }, "feature_add", _ => $"added {parsed!.Type} feature at index {addedIndex}", worldChanged: true);
        return result with { Index = addedIndex };
    }

    /// <summary>Replaces the feature at <paramref name="index"/> with one parsed from
    /// <paramref name="featureJson"/>. The old feature is fetched inside the choke point's factory (the read, apply,
    /// validate, and any revert all share one lock acquisition), after an up-front range check throws a precise
    /// <see cref="ArgumentException"/> rather than letting the engine's raw indexing surface an
    /// <see cref="ArgumentOutOfRangeException"/>.</summary>
    public MutationResult FeatureEdit(int index, string featureJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureJson);

        MapFeature? parsed = null;
        MutationResult result = Apply((doc, registry) =>
        {
            RequireIndexInRange(index, doc.Terrain.Features.Count, "feature", nameof(index));
            parsed = DocJson.ParseFeature(featureJson, registry);
            MapFeature old = doc.Terrain.Features[index];
            return new EditFeatureCommand(index, parsed, old);
        }, "feature_edit", _ => $"edited feature at index {index} to {parsed!.Type}", worldChanged: true);
        return result with { Index = index };
    }

    /// <summary>Removes the feature at <paramref name="index"/> (range-checked up front).</summary>
    public MutationResult FeatureRemove(int index)
    {
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Terrain.Features.Count, "feature", nameof(index));
            return new RemoveFeatureCommand(index);
        }, "feature_remove", _ => $"removed feature at index {index}", worldChanged: true);
        return result with { Index = index };
    }

    /// <summary>Moves the feature at <paramref name="fromIndex"/> to <paramref name="toIndex"/>. Features fold in
    /// list order, so this is how an author picks the winner between overlapping features. Both endpoints are
    /// range-checked up front.</summary>
    public MutationResult FeatureReorder(int fromIndex, int toIndex)
    {
        MutationResult result = Apply((doc, _) =>
        {
            int count = doc.Terrain.Features.Count;
            RequireIndexInRange(fromIndex, count, "feature", nameof(fromIndex));
            RequireIndexInRange(toIndex, count, "feature", nameof(toIndex));
            return new ReorderFeatureCommand(fromIndex, toIndex);
        }, "feature_reorder", _ => $"moved feature from index {fromIndex} to {toIndex}", worldChanged: true);
        return result with { Index = toIndex };
    }

    /// <summary>Renames the terrain feature at <paramref name="index"/> (range-checked up front). Empty clears
    /// the name back to unnamed. The target name must be unique among named features, validated inside
    /// <see cref="RenameFeatureCommand.Apply"/> (rejected before it mutates, so a rejected rename leaves the
    /// document unchanged). Renaming does not change terrain shape, so it does not affect the streamed world.</summary>
    public MutationResult FeatureRename(int index, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Terrain.Features.Count, "feature", nameof(index));
            string oldName = doc.Terrain.Features[index].Name ?? "";
            return new RenameFeatureCommand(index, name, oldName);
        }, "feature_rename", _ => $"renamed feature at index {index} to '{name}'", worldChanged: false);
        return result with { Index = index };
    }

    // ---- exclusions (scatter-input affecting) ---------------------------------------------------------------

    /// <summary>Appends a scatter exclusion whose shape is parsed from <paramref name="shapeJson"/>, optionally
    /// filtered to <paramref name="layers"/> (null = every scatter layer). An unknown layer filter is rejected by
    /// the validator at the choke point and the append is reverted.</summary>
    public MutationResult ExclusionAdd(string shapeJson, IReadOnlyList<string>? layers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeJson);

        int addedIndex = -1;
        MutationResult result = Apply((doc, registry) =>
        {
            MapShapeDoc shape = DocJson.ParseShape(shapeJson, registry);
            addedIndex = doc.Exclusions.Count;
            return new AddExclusionCommand(new MapExclusion { Shape = shape, Layers = layers?.ToList() });
        }, "exclusion_add", _ => $"added exclusion at index {addedIndex}", worldChanged: true);
        return result with { Index = addedIndex };
    }

    /// <summary>Replaces the shape of the exclusion at <paramref name="index"/> with one parsed from
    /// <paramref name="shapeJson"/>. The old shape is captured inside the factory (same lock acquisition as the
    /// apply and validate) after an up-front range check.</summary>
    public MutationResult ExclusionEdit(int index, string shapeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeJson);

        MutationResult result = Apply((doc, registry) =>
        {
            RequireIndexInRange(index, doc.Exclusions.Count, "exclusion", nameof(index));
            MapShapeDoc newShape = DocJson.ParseShape(shapeJson, registry);
            MapShapeDoc old = doc.Exclusions[index].Shape
                ?? throw new InvalidOperationException($"exclusion at index {index} has no shape to edit.");
            return new EditExclusionShapeCommand(index, newShape, old);
        }, "exclusion_edit", _ => $"edited exclusion shape at index {index}", worldChanged: true);
        return result with { Index = index };
    }

    /// <summary>Removes the exclusion at <paramref name="index"/> (range-checked up front).</summary>
    public MutationResult ExclusionRemove(int index)
    {
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Exclusions.Count, "exclusion", nameof(index));
            return new RemoveExclusionCommand(index);
        }, "exclusion_remove", _ => $"removed exclusion at index {index}", worldChanged: true);
        return result with { Index = index };
    }

    /// <summary>Renames the exclusion at <paramref name="index"/> (range-checked up front). Empty clears the name
    /// back to unnamed. The target name must be unique among named exclusions, validated inside
    /// <see cref="RenameExclusionCommand.Apply"/> (rejected before it mutates, so a rejected rename leaves the
    /// document unchanged). Renaming does not change the exclusion's shape or layer filter, so it does not affect
    /// the streamed world.</summary>
    public MutationResult ExclusionRename(int index, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Exclusions.Count, "exclusion", nameof(index));
            string oldName = doc.Exclusions[index].Name ?? "";
            return new RenameExclusionCommand(index, name, oldName);
        }, "exclusion_rename", _ => $"renamed exclusion at index {index} to '{name}'", worldChanged: false);
        return result with { Index = index };
    }

    /// <summary>Replaces the layer filter of the exclusion at <paramref name="index"/> (range-checked up front).
    /// Null <paramref name="layers"/> applies the exclusion to every scatter layer including any added later, an
    /// exact document semantic <see cref="EditExclusionLayersCommand"/> preserves: passing an empty list is
    /// different from passing null (empty means the exclusion applies to nothing, legal per the model). An
    /// unknown layer name is not rejected here: the standard document validator at the choke point catches it on
    /// save, the same invariant every other layer-filter verb already relies on. Affects the streamed world
    /// (targeting changes scatter output).</summary>
    public MutationResult ExclusionSetLayers(int index, IReadOnlyList<string>? layers = null)
    {
        MutationResult result = Apply((doc, _) =>
        {
            RequireIndexInRange(index, doc.Exclusions.Count, "exclusion", nameof(index));
            List<string>? oldLayers = doc.Exclusions[index].Layers;
            return new EditExclusionLayersCommand(index, layers?.ToList(), oldLayers);
        }, "exclusion_set_layers",
            _ => $"set exclusion {index} layers to " + (layers is null ? "all" : "[" + string.Join(", ", layers) + "]"),
            worldChanged: true);
        return result with { Index = index };
    }

    // ---- scatter overrides (no command: direct list mutation + manual revert) -------------------------------

    /// <summary>Appends a scatter override (density multiplier and/or kind substitution) over a shape parsed from
    /// <paramref name="shapeJson"/>, optionally filtered to <paramref name="layers"/>. Scatter overrides have no
    /// <see cref="EditorCommand"/>, so this mutates the list directly inside one world-affecting
    /// <see cref="MapEditSession.Mutate{T}"/>, validates, and reverts by removing the appended entry on failure,
    /// mirroring the invariant the command paths get from the choke point. <paramref name="kinds"/> entries parse
    /// as <c>"id"</c> (weight 1) or <c>"id:weight"</c>.</summary>
    public MutationResult ScatterOverrideAdd(string shapeJson, float densityMultiplier = 1f,
        IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? layers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shapeJson);

        return session.Mutate((doc, registry) =>
        {
            var over = new MapScatterOverrideDoc
            {
                Shape = DocJson.ParseShape(shapeJson, registry),
                DensityMultiplier = densityMultiplier,
                Kinds = kinds is null ? null : ParseKinds(kinds),
                Layers = layers?.ToList(),
            };
            doc.ScatterOverrides.Add(over);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                doc.ScatterOverrides.Remove(over);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            int index = doc.ScatterOverrides.Count - 1;
            return new MutationResult("scatter_override_add", $"added scatter override at index {index}",
                WorldChanged: true, Index: index);
        }, worldChanged: true);
    }

    /// <summary>Edits the scatter override at <paramref name="index"/>, replacing only the supplied fields (a null
    /// argument leaves that field unchanged). The whole entry is swapped for an edited copy so a validation
    /// failure reverts by restoring the captured old entry, with the range check up front.</summary>
    public MutationResult ScatterOverrideEdit(int index, string? shapeJson = null,
        float? densityMultiplier = null, IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? layers = null)
    {
        return session.Mutate((doc, registry) =>
        {
            RequireIndexInRange(index, doc.ScatterOverrides.Count, "scatter override", nameof(index));
            MapScatterOverrideDoc old = doc.ScatterOverrides[index];
            var edited = new MapScatterOverrideDoc
            {
                Shape = shapeJson is null ? old.Shape : DocJson.ParseShape(shapeJson, registry),
                DensityMultiplier = densityMultiplier ?? old.DensityMultiplier,
                Kinds = kinds is null ? old.Kinds : ParseKinds(kinds),
                Layers = layers is null ? old.Layers : layers.ToList(),
            };
            doc.ScatterOverrides[index] = edited;

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                doc.ScatterOverrides[index] = old;
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new MutationResult("scatter_override_edit", $"edited scatter override at index {index}",
                WorldChanged: true, Index: index);
        }, worldChanged: true);
    }

    /// <summary>Removes the scatter override at <paramref name="index"/> (range-checked up front), reverting by
    /// re-inserting it at that index on a validation failure.</summary>
    public MutationResult ScatterOverrideRemove(int index)
    {
        return session.Mutate((doc, registry) =>
        {
            RequireIndexInRange(index, doc.ScatterOverrides.Count, "scatter override", nameof(index));
            MapScatterOverrideDoc removed = doc.ScatterOverrides[index];
            doc.ScatterOverrides.RemoveAt(index);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                doc.ScatterOverrides.Insert(index, removed);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new MutationResult("scatter_override_remove", $"removed scatter override at index {index}",
                WorldChanged: true, Index: index);
        }, worldChanged: true);
    }

    // ---- bake -----------------------------------------------------------------------------------------------

    /// <summary>Freezes scatter layer <paramref name="layer"/> over the rect (<paramref name="minX"/>,
    /// <paramref name="minZ"/>)..(<paramref name="maxX"/>, <paramref name="maxZ"/>) into authored placements via
    /// <see cref="BakeRegionCommand"/>. Each frozen prop becomes a <c>baked-&lt;layer&gt;-N</c> placement with an
    /// explicit Y and a <c>baked</c> tag, and a covering rect exclusion limited to the layer is appended so the
    /// frozen props are not re-scattered over themselves. An unknown layer throws
    /// <see cref="MapDocumentException"/> before anything is mutated. The command is applied, validated, and
    /// reverted on failure inside one world-affecting mutation (a bespoke path, not the shared choke point, because
    /// it returns a <see cref="BakeResult"/> and diffs placements before and after under the same lock). The baked
    /// ids and count come from the placement-id diff across the apply.</summary>
    public BakeResult BakeRegion(string layer, float minX, float minZ, float maxX, float maxZ)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        return session.Mutate((doc, registry) =>
        {
            var command = new BakeRegionCommand(new RectArea(minX, minZ, maxX, maxZ), layer, registry);
            var before = new HashSet<string>(doc.Placements.Select(p => p.Id), StringComparer.Ordinal);
            int exclusionsBefore = doc.Exclusions.Count;

            command.Apply(doc);   // throws MapDocumentException for an unknown layer, before it mutates anything

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            List<string> bakedIds = doc.Placements.Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
            bool exclusionAdded = doc.Exclusions.Count > exclusionsBefore;
            return new BakeResult(layer, bakedIds.Count, bakedIds, exclusionAdded);
        }, worldChanged: true);
    }

    // ---- index + kind parsing helpers ------------------------------------------------------------------------

    /// <summary>Throws a precise <see cref="ArgumentException"/> when <paramref name="index"/> is outside
    /// <c>[0, count)</c>. Done up front so the engine's raw list indexing (which throws the wrong exception type)
    /// is never reached with a bad index.</summary>
    static void RequireIndexInRange(int index, int count, string kind, string paramName)
    {
        if (index < 0 || index >= count)
            throw new ArgumentException(
                count == 0
                    ? $"{kind} index {index} is out of range: the document has no {kind}s to address."
                    : $"{kind} index {index} is out of range. Valid range is [0, {count - 1}].",
                paramName);
    }

    /// <summary>Parses scatter kind strings, each <c>"id"</c> (weight 1) or <c>"id:weight"</c> (weight parsed with
    /// the invariant culture). Throws <see cref="ArgumentException"/> on an empty id or a non-numeric weight.</summary>
    static List<MapPropKind> ParseKinds(IReadOnlyList<string> kinds)
    {
        var result = new List<MapPropKind>(kinds.Count);
        foreach (string entry in kinds)
        {
            int colon = entry.LastIndexOf(':');
            string id = colon < 0 ? entry : entry[..colon];
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException($"scatter kind '{entry}': id must be non-empty.");

            float weight = 1f;
            if (colon >= 0)
            {
                string weightText = entry[(colon + 1)..];
                if (!float.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
                    throw new ArgumentException($"scatter kind '{entry}': weight '{weightText}' is not a number.");
            }
            result.Add(new MapPropKind { Id = id, Weight = weight });
        }
        return result;
    }

    /// <summary>Parses a biome name (case-insensitive) into a <see cref="BiomeId"/>. Throws
    /// <see cref="ArgumentException"/> naming every valid value when <paramref name="biome"/> does not match
    /// one.</summary>
    static BiomeId ParseBiome(string biome)
    {
        if (Enum.TryParse(biome, ignoreCase: true, out BiomeId parsed)) return parsed;
        throw new ArgumentException(
            $"biome '{biome}' is not a recognized BiomeId. Valid values: {string.Join(", ", Enum.GetNames<BiomeId>())}.",
            nameof(biome));
    }

    /// <summary>Deep-clones a scatter kind list (each <see cref="MapPropKind"/> copied, not shared), the
    /// whole-value-edit copy discipline <see cref="EditScatterLayerCommand"/>/<see cref="EditCompanionLayerCommand"/>
    /// require so a caller's edited copy never aliases the command's captured old value.</summary>
    static List<MapPropKind> CloneKinds(IReadOnlyList<MapPropKind> kinds)
    {
        var result = new List<MapPropKind>(kinds.Count);
        foreach (MapPropKind k in kinds) result.Add(new MapPropKind { Id = k.Id, Weight = k.Weight });
        return result;
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

    // ---- id generation ---------------------------------------------------------------------------------------

    /// <summary>The smallest N &gt;= 1 such that <paramref name="prefix"/> + N is not already in
    /// <paramref name="existingIds"/> (the same uniqueness approach as <see cref="BakeRegionCommand"/>).</summary>
    static string GenerateId(IEnumerable<string> existingIds, string prefix)
    {
        var taken = new HashSet<string>(existingIds, StringComparer.Ordinal);
        int n = 1;
        string id;
        do { id = prefix + n.ToString(CultureInfo.InvariantCulture); n++; }
        while (taken.Contains(id));
        return id;
    }
}
