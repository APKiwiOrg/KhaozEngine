using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Mutates the session's open document: placements, spawns, regions, terrain globals, terrain features,
/// exclusions, scatter overrides, and region bake. Command-backed mutations route through
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

    /// <summary>Edits the terrain globals: the water level (via <see cref="EditTerrainCommand"/>) and/or the noise
    /// seed (a direct field edit, since no command carries it). At least one must be supplied. Both are applied,
    /// validated once, and reverted together on failure inside a single world-affecting mutation, so a rejected
    /// edit restores both to their prior values and the cached field is rebuilt only on success. The water command
    /// is applied first, then the seed, matching the "globals" ordering the engine command was named for.</summary>
    public MutationResult TerrainEdit(float? waterLevel = null, int? seed = null)
    {
        if (waterLevel is null && seed is null)
            throw new ArgumentException("terrain_edit needs at least one of waterLevel or seed.");

        return session.Mutate((doc, registry) =>
        {
            int oldSeed = doc.Terrain.Seed;
            EditTerrainCommand? waterCommand = null;

            if (waterLevel is float newWater)
            {
                waterCommand = new EditTerrainCommand(newWater, doc.Terrain.WaterLevel);
                waterCommand.Apply(doc);
            }
            if (seed is int newSeed) doc.Terrain.Seed = newSeed;

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                if (seed is not null) doc.Terrain.Seed = oldSeed;
                waterCommand?.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            var parts = new List<string>(2);
            if (waterLevel is float w) parts.Add($"water level {w.ToString("F2", CultureInfo.InvariantCulture)}");
            if (seed is int s) parts.Add($"seed {s.ToString(CultureInfo.InvariantCulture)}");
            return new MutationResult("terrain_edit", "set " + string.Join(" and ", parts), WorldChanged: true);
        }, worldChanged: true);
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
