using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    /// <summary>Applies one standalone marker set or remove through the native session history, the command the GUI
    /// invokes. A resolver (1, 1) document refuses and names resolver adoption as the prerequisite.</summary>
    public MapPrefabMutationResult MarkerApply(MapMarkerEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var command = new MarkerEditCommand(edit);
        string? id = edit switch { MapSetMarker set => set.Marker?.Id, MapRemoveMarker remove => remove.Id, _ => null };
        var effects = session.ExecuteNativeCommand(command);
        return new(new(edit is MapRemoveMarker ? "marker_remove" : "marker_set", command.Label, false, Id: id), effects);
    }
}
