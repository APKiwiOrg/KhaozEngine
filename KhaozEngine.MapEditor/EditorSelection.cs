using System;

namespace KhaozEngine.MapEditor;

/// <summary>What kind of document element a <see cref="EditorSelection"/> refers to. The id string is
/// interpreted per kind: a placement or spawn id, or a feature/exclusion list index rendered as a string.</summary>
public enum SelectionKind
{
    /// <summary>Nothing selected.</summary>
    None,
    /// <summary>An authored prop/building placement, keyed by its stable id.</summary>
    Placement,
    /// <summary>An NPC spawn marker, keyed by its stable id.</summary>
    Spawn,
    /// <summary>A terrain feature, keyed by its list index as a string.</summary>
    Feature,
    /// <summary>A scatter exclusion shape, keyed by its list index as a string.</summary>
    Exclusion,
    /// <summary>A named, game-interpreted region marker, keyed by its name.</summary>
    Region,
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
