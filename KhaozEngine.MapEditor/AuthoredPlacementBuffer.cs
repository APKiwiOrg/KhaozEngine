using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Refills the authored prop draw input without retaining stale visibility or selection state.</summary>
internal sealed class AuthoredPlacementBuffer
{
    readonly List<PropPlacement> _unselected = new();

    /// <summary>Visible, unselected props in document order.</summary>
    internal IReadOnlyList<PropPlacement> Unselected => _unselected;

    /// <summary>The first visible placement matching the selected id.</summary>
    internal EditorPlacement? Selected { get; private set; }

    /// <summary>Refills both outputs from the current placements and effective visibility.</summary>
    internal void Prepare(IReadOnlyList<EditorPlacement> placements, EditorVisibility visibility,
        Func<string, bool>? kindVisible, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(visibility);
        _unselected.Clear();
        Selected = null;
        if (!visibility.GetGroup(VisibilityGroup.Placements)) return;
        Fill(placements, visibility, kindVisible, selectedId);
    }

    /// <summary>Defers rebuilding a dirty placement cache while the effective placement group is hidden.</summary>
    internal void Prepare(PlacementCache placements, MapDocument document, TerrainField field,
        EditorVisibility visibility, Func<string, bool>? kindVisible, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(visibility);
        _unselected.Clear();
        Selected = null;
        if (!visibility.GetGroup(VisibilityGroup.Placements)) return;
        Fill(placements.Get(document, field), visibility, kindVisible, selectedId);
    }

    void Fill(IReadOnlyList<EditorPlacement> placements, EditorVisibility visibility,
        Func<string, bool>? kindVisible, string? selectedId)
    {
        for (int i = 0; i < placements.Count; i++)
        {
            EditorPlacement placement = placements[i];
            bool visible = !visibility.IsElementHidden(SelectionKind.Placement, placement.Id)
                && (kindVisible is null || kindVisible(placement.Prop.Id));
            if (!visible) continue;
            if (Selected is null && selectedId is not null
                && string.Equals(placement.Id, selectedId, StringComparison.Ordinal))
            {
                Selected = placement;
            }
            else
            {
                _unselected.Add(placement.Prop);
            }
        }
    }
}
