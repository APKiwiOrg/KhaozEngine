using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    /// <summary>Explicitly adopts both legacy native spawn lists through the shared undoable command.</summary>
    public MapPrefabMutationResult SpawnsAdopt(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var command = new AdoptSpawnsCommand(choices);
        var effects = session.ExecuteNativeCommand(command);
        return new(new("spawns_adopt", command.Label, false), effects);
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

    // ---- player spawns ----------------------------------------------------------------------------------------

    /// <summary>Adds a player start marker. When <paramref name="id"/> is null, auto-generates <c>player-N</c>
    /// with the smallest N &gt;= 1 unique against existing player spawn ids, the same scheme the editor's own
    /// auto-naming (<c>EditorTool.UniqueName</c>) uses. Which spawn a game uses at runtime (single-player start,
    /// party rally point, respawn point) is game code's concern, so unlike <see cref="SpawnAdd"/> there is no
    /// archetype here. A duplicate explicit id is not pre-checked here: the Add command's own guard rejects it
    /// before the append (#766), exactly as <see cref="SpawnAdd"/> and <see cref="PlacementAdd"/> are, so the
    /// document is untouched and the message is the guard's rather than the choke point's "mutation rejected:"
    /// shape, since nothing was applied for the validator to reject.</summary>
    public MutationResult PlayerSpawnAdd(float x, float z, float yaw = 0f, bool enabled = true,
        string? id = null, IReadOnlyList<string>? tags = null)
    {
        string spawnId = id ?? session.WithDocument((doc, _) =>
            GenerateId(doc.PlayerSpawns.Select(s => s.Id), "player-"));

        var spawn = new MapPlayerSpawn
        {
            Id = spawnId,
            X = x,
            Z = z,
            Yaw = yaw,
            Enabled = enabled,
            Tags = tags is null ? new List<string>() : new List<string>(tags),
        };

        MutationResult result = Apply(new AddPlayerSpawnCommand(spawn), "player_spawn_add",
            $"added player spawn at ({x:F1}, {z:F1})");
        return result with { Id = spawnId };
    }

    /// <summary>Moves a player spawn to a new XZ.</summary>
    public MutationResult PlayerSpawnMove(string id, float x, float z)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new MovePlayerSpawnCommand(id, x, z), "player_spawn_move",
            $"moved player spawn {id} to ({x:F1}, {z:F1})");
    }

    /// <summary>Sets a player spawn's facing yaw, in radians. A separate verb from <see cref="PlayerSpawnMove"/>,
    /// mirroring how <see cref="PlacementRotate"/> stays a separate verb from <see cref="PlacementMove"/>: the
    /// underlying commands (<see cref="SetPlayerSpawnYawCommand"/> vs <see cref="MovePlayerSpawnCommand"/>) are
    /// already distinct, each with its own independent TryMerge coalescing, so the MCP verb granularity follows
    /// the command granularity rather than folding yaw into the move verb.</summary>
    public MutationResult PlayerSpawnSetYaw(string id, float yaw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new SetPlayerSpawnYawCommand(id, yaw), "player_spawn_set_yaw",
            $"set player spawn {id} yaw to {yaw:F2}");
    }

    /// <summary>Toggles a player spawn's enabled flag.</summary>
    public MutationResult PlayerSpawnSetEnabled(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string state = enabled ? "enabled" : "disabled";
        return Apply(new SetPlayerSpawnEnabledCommand(id, enabled), "player_spawn_set_enabled",
            $"set player spawn {id} {state}");
    }

    /// <summary>Renames a player spawn. The target id must be unique in the document, rejected inside
    /// <see cref="RenamePlayerSpawnCommand.Apply"/> before it mutates (the in-Apply guard the editor GUI itself
    /// relies on, since it has no validate-and-revert net of its own), surfaced here as a clean error the same
    /// way the choke point surfaces every other rejected mutation.</summary>
    public MutationResult PlayerSpawnRename(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        MutationResult result = Apply(new RenamePlayerSpawnCommand(oldId, newId), "player_spawn_rename",
            $"renamed player spawn {oldId} to {newId}");
        return result with { Id = newId };
    }

    /// <summary>Removes a player spawn by id.</summary>
    public MutationResult PlayerSpawnRemove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RemovePlayerSpawnCommand(id), "player_spawn_remove", $"removed player spawn {id}");
    }

}
