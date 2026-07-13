using System;

namespace KhaozEngine.MapEditor;

/// <summary>What kind of document element a <see cref="EditorSelection"/> refers to. The id string is
/// interpreted per kind: a placement or spawn id, a feature/exclusion/biome-band list index rendered as a
/// string, or the empty string for the singleton <see cref="Terrain"/> root.</summary>
public enum SelectionKind
{
    /// <summary>Nothing selected.</summary>
    None,
    /// <summary>An authored prop/building placement, keyed by its stable id.</summary>
    Placement,
    /// <summary>An NPC spawn marker, keyed by its stable id.</summary>
    Spawn,
    /// <summary>A player start marker, keyed by its stable id. Mirrors <see cref="Spawn"/> (same viewport marker,
    /// pick box, and Marker gizmo affordance), but carries no archetype and a facing yaw, and lives in its own
    /// document section (<see cref="MapDoc.MapDocument.PlayerSpawns"/>).</summary>
    PlayerSpawn,
    /// <summary>The terrain root: the document's single terrain block (water level, seed, biomes). A singleton,
    /// so the id is unused (the empty string). A dedicated kind, rather than a sentinel <see cref="Feature"/>
    /// index, keeps the index-parsing feature inspector and the placement/spawn gizmo checks free of a special
    /// case.</summary>
    Terrain,
    /// <summary>A terrain feature, keyed by its list index as a string.</summary>
    Feature,
    /// <summary>A scatter exclusion shape, keyed by its list index as a string.</summary>
    Exclusion,
    /// <summary>A named, game-interpreted region marker, keyed by its name.</summary>
    Region,
    /// <summary>A terrain biome band (an elevation-range biome slice), keyed by its list index as a string.
    /// Outline-only: bands have no viewport geometry, so they are never picked or gizmo-dragged and carry no
    /// visibility toggle. Index-keyed like <see cref="Feature"/> and <see cref="Exclusion"/>.</summary>
    BiomeBand,
    /// <summary>A named procedural scatter layer, keyed by its unique name. Outline-only: scatter layers have no
    /// viewport geometry, so they are never picked or gizmo-dragged. Name-keyed (not index-keyed) because layer
    /// names are already unique-required by the validator, so a rename re-points the selection to the new name
    /// (the region-rename precedent) rather than the selection being tied to a shifting list index.</summary>
    ScatterLayer,
    /// <summary>A named companion layer (props ringing a scatter layer's hosts), keyed by its unique name.
    /// Outline-only and name-keyed for the same reasons as <see cref="ScatterLayer"/>.</summary>
    CompanionLayer,
}

/// <summary>What is selected in the editor, by kind plus stable id (placement id, spawn id, feature index
/// as string, exclusion index as string, region name). Single selection in v1, resilient to list
/// reordering because it holds an id rather than a list position for the id-keyed kinds.</summary>
public sealed class EditorSelection
{
    /// <summary>The kind of element selected, or <see cref="SelectionKind.None"/> when empty.</summary>
    public SelectionKind Kind { get; private set; } = SelectionKind.None;

    /// <summary>The stable id of the selected element, or the empty string when nothing is selected.</summary>
    public string Id { get; private set; } = "";

    /// <summary>True when nothing is selected.</summary>
    public bool IsEmpty => Kind == SelectionKind.None;

    /// <summary>Fired whenever the selection changes (a <see cref="Set"/> or a <see cref="Clear"/> that
    /// actually cleared a prior selection).</summary>
    public event Action? Changed;

    /// <summary>Selects an element by kind and id. Passing <see cref="SelectionKind.None"/> clears the
    /// selection. Always fires <see cref="Changed"/> for a concrete kind.</summary>
    public void Set(SelectionKind kind, string id)
    {
        if (kind == SelectionKind.None)
        {
            Clear();
            return;
        }
        Kind = kind;
        Id = id ?? "";
        Changed?.Invoke();
    }

    /// <summary>Clears the selection. Fires <see cref="Changed"/> only when a selection was actually
    /// cleared, so a redundant clear raises no spurious event.</summary>
    public void Clear()
    {
        if (Kind == SelectionKind.None)
            return;
        Kind = SelectionKind.None;
        Id = "";
        Changed?.Invoke();
    }
}
