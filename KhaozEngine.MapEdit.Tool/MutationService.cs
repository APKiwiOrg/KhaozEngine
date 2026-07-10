using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

/// <summary>Mutates the session's open document: placements, spawns, and regions (terrain globals, features,
/// exclusions, and scatter overrides land in Task 4, reusing the same choke point). Every mutation routes through
/// <see cref="Apply(EditorCommand, string, string)"/> (or its factory overload,
/// <see cref="Apply(Func{MapDocument, MapDocRegistry, EditorCommand}, string, Func{EditorCommand, string}, bool)"/>,
/// for verbs that need a document read to build the command): apply the <see cref="EditorCommand"/>, validate the
/// document, and on any validation error revert the command and throw, so the in-session document is never left
/// invalid. A rejected mutation leaves the session exactly as it was before the attempt, including the dirty
/// flag, because <see cref="MapEditSession.Mutate{T}"/> only marks dirty when its callback returns normally and a
/// rejected mutation throws out of that callback.</summary>
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
