using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed record MapPrefabMutationResult(MutationResult Mutation, MapNativeEditEffects Effects);

public sealed partial class MutationService
{
    /// <summary>Applies one closure-bound prefab command through the native session history.</summary>
    public MapPrefabMutationResult PrefabApply(MapPrefabEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var command = new PrefabEditCommand(edit);
        return new(new("prefab_apply", command.Label, true), session.ExecuteNativeCommand(command));
    }
    /// <summary>Applies one standalone interior or roof command through the native session history.</summary>
    public MapPrefabMutationResult InteriorApply(MapInteriorEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var command = new InteriorEditCommand(edit);
        return new(new("interior_apply", command.Label, true), session.ExecuteNativeCommand(command));
    }
}
