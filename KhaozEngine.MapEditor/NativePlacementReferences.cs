using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The authoritative visitor for typed authored-placement references.</summary>
public static class NativePlacementReferences
{
    /// <summary>Remaps typed references only. Format 4 has none. Tags, resource IDs, asset IDs and Kind are not placement references.</summary>
    public static void Remap(MapDocument document, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        // Future structured prefab/marker references extend this visitor, never arbitrary strings or tags.
    }
}
