using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed record MapTerrainMutationResult(MutationResult Mutation, MapNativeEditEffects Effects);

public sealed partial class MutationService
{
    /// <summary>Applies the same terrain command as the GUI under the session's native mutation lock.</summary>
    public MapTerrainMutationResult TerrainApply(MapTerrainEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var command = new TerrainEditCommand(edit);
        return session.Mutate((doc, registry) =>
        {
            if (doc.ResolverIdentity is null) throw new MapDocumentException(MapDocumentRefusal.ToolNativeDocumentRequired, "Terrain edits require a native document.")
            { DocumentId = doc.Id, Operation = nameof(TerrainApply), CommandType = command.GetType() };
            MapNativeEditEffects effects = session.ApplyNative(command, doc, registry);
            return new MapTerrainMutationResult(new("terrain_apply", command.Label, command.AffectsWorld), effects);
        }, command.AffectsWorld);
    }
}
