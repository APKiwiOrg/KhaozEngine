using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Support;

namespace KhaozEngine.MapEditor;

/// <summary>One spawn marker the viewport draws: its kind, stable ID, billboard centre and enabled state.</summary>
internal readonly record struct SpawnBillboard(SelectionKind Kind, string Id, Vector3 Position, bool Enabled);

/// <summary>The spawn markers of one frame, NPC spawns first and then player spawns in document order, plus the
/// developer diagnostics for rows that could not be placed.</summary>
internal sealed record SpawnBillboardFrame(IReadOnlyList<SpawnBillboard> Billboards, IReadOnlyList<string> Diagnostics);

/// <summary>The one source of spawn marker heights for drawing, picking and focus. An analytic document stands its
/// markers on the analytic terrain, as the editor always has. A resolver-2 native document reads its legacy spawns on
/// the caller's named Surface through <see cref="NativeMarkerReadouts"/> and never on the terrain field. A row whose
/// sample is not Supported is left out with a diagnostic rather than drawn at an invented height. GPU free.</summary>
internal static class SpawnBillboards
{
    /// <summary>Billboard lift above the ground, so the marker floats as a pin instead of z-fighting it.</summary>
    internal const float Lift = 1.0f;

    /// <summary>Lift of the focus pivot above a spawn's ground.</summary>
    internal const float FocusLift = 0.75f;

    const string Guidance = "Supply MapEditorOptions.MarkerOptions with a LegacySpawnSurfaceId that supports them, " +
        "or adopt them as markers with spawns_adopt and author heights with marker_set.";

    /// <summary>The spawn markers <paramref name="visibility"/> shows, gated by the Spawns and PlayerSpawns groups and
    /// the per-element hides as the released draw loops were. A row that needs a native height and has no Supported
    /// sample is omitted, and the frame then carries one diagnostic naming every omitted row.</summary>
    internal static SpawnBillboardFrame Build(MapDocument document, EditorVisibility visibility,
        Func<float, float, float> analyticGround, NativeMarkerReadouts? native)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(analyticGround);
        bool authored = UsesAuthoredSupport(document);
        var rows = new List<SpawnBillboard>();
        var omitted = new List<string>();
        void Add(SelectionKind kind, string id, float x, float z, bool enabled)
        {
            if (visibility.IsElementHidden(kind, id)) return;
            string? why = null;
            float? ground = authored ? NativeGround(native, kind, id, x, z, out why) : analyticGround(x, z);
            if (ground is float y) rows.Add(new(kind, id, new Vector3(x, y + Lift, z), enabled));
            else omitted.Add(why!);
        }
        if (visibility.GetGroup(VisibilityGroup.Spawns))
            foreach (MapSpawn spawn in document.Spawns) Add(SelectionKind.Spawn, spawn.Id, spawn.X, spawn.Z, spawn.Enabled);
        if (visibility.GetGroup(VisibilityGroup.PlayerSpawns))
            foreach (MapPlayerSpawn spawn in document.PlayerSpawns)
                Add(SelectionKind.PlayerSpawn, spawn.Id, spawn.X, spawn.Z, spawn.Enabled);
        string[] diagnostics = omitted.Count == 0 ? [] :
            ["Native legacy spawn markers not drawn: " + string.Join(" ", omitted) + " " + Guidance];
        return new(rows.AsReadOnly(), Array.AsReadOnly(diagnostics));
    }

    /// <summary>The ground a spawn focus pivot stands on, before <see cref="FocusLift"/>. Analytic documents sample
    /// <paramref name="analyticGround"/>. A resolver-2 native document needs a Supported native sample, otherwise this
    /// returns null with a developer <paramref name="diagnostic"/>. An unknown spawn returns null with no diagnostic.</summary>
    internal static float? FocusGround(MapDocument document, SelectionKind kind, string id,
        Func<float, float, float> analyticGround, NativeMarkerReadouts? native, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(analyticGround);
        diagnostic = null;
        (float X, float Z)? at = kind switch
        {
            SelectionKind.Spawn => document.Spawns.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } s
                ? (s.X, s.Z) : null,
            SelectionKind.PlayerSpawn => document.PlayerSpawns.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } p
                ? (p.X, p.Z) : null,
            _ => null,
        };
        if (at is not { } point) return null;
        (float x, float z) = point;
        if (!UsesAuthoredSupport(document)) return analyticGround(x, z);
        float? ground = NativeGround(native, kind, id, x, z, out string? why);
        if (ground is null) diagnostic = "Cannot focus native legacy " + why + " " + Guidance;
        return ground;
    }

    /// <summary>True for a native document whose identity names authored support. Resolver (1, 1) keeps the analytic
    /// recipe its identity names. An unsupported identity never falls back to the terrain field.</summary>
    internal static bool UsesAuthoredSupport(MapDocument document)
    {
        if (document.ResolverIdentity is null) return false;
        try
        {
            return MapNativeResolution.ResolverVersionOf(document.ResolverIdentity) != 1;
        }
        catch (MapDocumentException)
        {
            return true;
        }
    }

    static float? NativeGround(NativeMarkerReadouts? native, SelectionKind kind, string id, float x, float z, out string? why)
    {
        string row = $"{kind} '{id}'";
        if (native is null)
        {
            why = row + " (native marker readouts are not configured).";
            return null;
        }
        MapSupportResult support = native.LegacySpawnHeight(x, z);
        if (support.Status == MapSupportStatus.Supported)
        {
            why = null;
            return support.WorldY;
        }
        why = $"{row} (support {support.Status}: {support.Detail}).";
        return null;
    }
}
