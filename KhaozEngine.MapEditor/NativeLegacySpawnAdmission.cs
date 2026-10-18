using System;
using System.Text.Json;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

internal sealed record NativeLegacySpawnPermit(string BeforeLists, string AfterLists);

/// <summary>The native legacy-list boundary. Only the adoption command and its captured undo may change the lists.</summary>
internal static class NativeLegacySpawnAdmission
{
    internal static string Canonical(MapDocument document)
    {
        // Use the released document codec for both mutable DTO lists, without embedding or inspecting world geometry.
        var lists = new MapDocument
        {
            Id = "legacy-spawn-lists",
            Bounds = new() { MinX = -1, MinZ = -1, MaxX = 1, MaxZ = 1 },
            Spawns = document.Spawns,
            PlayerSpawns = document.PlayerSpawns,
        };
        using var json = JsonDocument.Parse(MapDocumentFile.SaveText(lists));
        return json.RootElement.GetProperty("spawns").GetRawText() + "\n" + json.RootElement.GetProperty("playerSpawns").GetRawText();
    }

    /// <summary>The canonical lists of a live document, computed once per transaction and passed to every admission
    /// check against that document. Never kept across transactions, as callers can change lists in place.</summary>
    internal static string LiveLists(MapDocument live)
    {
        try { return Canonical(live); }
        catch (Exception error) when (error is MapDocumentException or JsonException or ArgumentException)
        {
            throw new MapDocumentException(Guidance(live), error);
        }
    }

    internal static void RequireAdmitted(MapDocument live, MapDocument candidate, NativeLegacySpawnPermit? permit, string? liveLists = null)
    {
        try
        {
            string before = liveLists ?? Canonical(live), after = Canonical(candidate);
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            if (permit is not null && string.Equals(permit.BeforeLists, before, StringComparison.Ordinal) &&
                string.Equals(permit.AfterLists, after, StringComparison.Ordinal)) return;
        }
        catch (Exception error) when (error is MapDocumentException or JsonException or ArgumentException)
        {
            // A malformed attempted legacy write still names the native migration path, before general validation.
            throw new MapDocumentException(Guidance(live), error);
        }
        throw new MapDocumentException(Guidance(live));
    }

    internal static string Guidance(MapDocument document) => document.ResolverIdentity is { ResolverVersion: 1 }
        ? "Native legacy spawn editing requires resolver adoption first with MapResolverAdoption.ConvertToAuthoredSupport, " +
            "then marker_set, marker_remove and spawns_adopt."
        : "Native legacy spawn lists are read-only. Use marker_set and marker_remove for markers, or spawns_adopt to explicitly adopt both legacy lists.";

    internal static void RequireMarkerCommand(MapDocument document, IEditorCommand command)
    {
        if (command is AddSpawnCommand or MoveSpawnCommand or SetSpawnEnabledCommand or RenameSpawnCommand or RemoveSpawnCommand or
            AddPlayerSpawnCommand or MovePlayerSpawnCommand or SetPlayerSpawnYawCommand or SetPlayerSpawnEnabledCommand or RenamePlayerSpawnCommand or RemovePlayerSpawnCommand)
            throw new MapDocumentException(Guidance(document));
    }
}
