using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed record MapWaterMutationResult(MutationResult Mutation, MapNativeEditEffects Effects);

public sealed partial class MutationService
{
    /// <summary>Applies the same water command as the GUI under the session's native mutation lock.</summary>
    public MapWaterMutationResult WaterApply(MapWaterEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var command = new WaterEditCommand(edit);
        return session.Mutate((doc, registry) =>
        {
            if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ||
                doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
                throw new MapDocumentException("Water edits require a resolver-2 native document.");
            MapNativeEditEffects effects = session.ApplyNative(command, doc, registry);
            return new MapWaterMutationResult(new("water_apply", command.Label, command.AffectsWorld), effects);
        }, command.AffectsWorld);
    }
}
