using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    /// <summary>Freezes the WHOLE zone via <see cref="FreezeZoneCommand"/>: bakes every scatter and companion layer
    /// across the document bounds into authored placements (each <c>baked-&lt;source&gt;-N</c> with an explicit Y, a
    /// <c>baked</c> tag, and the source layer name as a second tag), then removes all scatter layers, companion
    /// layers, exclusions, and scatter overrides, leaving a placements-only document with no procedural generation.
    /// The frozen props equal live generation for the same document (the command reuses the runtime scatter and
    /// companion calls, with the document's exclusions and overrides applied during generation and removed after).
    /// The command is applied, validated, and reverted on failure inside one world-affecting mutation (a bespoke
    /// path, not the shared choke point, because it returns a <see cref="FreezeZoneResult"/>). A document that has no
    /// scatter or companion layers is a no-op: it leaves the document untouched (never marking the session dirty) and
    /// returns a result with <see cref="FreezeZoneResult.Applied"/> false.</summary>
    public FreezeZoneResult FreezeZone()
    {
        // Check for work outside the mutation so a genuine no-op never marks the session dirty (there is nothing to
        // freeze, so the document must stay byte-identical, dirty flag included).
        if (!session.WithDocument((doc, _) => FreezeZoneCommand.HasWork(doc)))
            return new FreezeZoneResult(0, 0, 0, 0, 0, Applied: false);

        return session.Mutate((doc, registry) =>
        {
            int scatter = doc.ScatterLayers.Count;
            int companion = doc.CompanionLayers.Count;
            int exclusions = doc.Exclusions.Count;
            int overrides = doc.ScatterOverrides.Count;
            int placementsBefore = doc.Placements.Count;

            var command = new FreezeZoneCommand(registry);
            command.Apply(doc);

            IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw new InvalidOperationException("mutation rejected: " + string.Join("; ", errors));
            }

            return new FreezeZoneResult(doc.Placements.Count - placementsBefore, scatter, companion, exclusions,
                overrides, Applied: true);
        }, worldChanged: true);
    }
}
