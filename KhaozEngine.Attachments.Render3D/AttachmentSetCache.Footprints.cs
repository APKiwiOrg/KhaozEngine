using System;
using System.Linq;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    LiftFootprint? ReadFootprint(AttachmentSetDefinition definition)
    {
        if (definition.Footprint is not { } reference) return null;
        var asset = new AttachmentMeshRef(reference.Key, reference.Sha256);
        LiftFootprint footprint = LiftFootprintFile.Parse(ReadVerified(asset, reference.Key), reference.Key);

        // Both lists own their IDs in ascending order, so the first difference names the lowest unmatched ID.
        int shared = Math.Min(definition.Parts.Count, footprint.Parts.Count);
        for (int index = 0; index < shared; index++)
        {
            uint defined = definition.Parts[index].Id;
            uint measured = footprint.Parts[index].PartId;
            if (defined != measured) throw FootprintPartIdsMismatch(reference.Key, Math.Min(defined, measured));
        }
        if (definition.Parts.Count > shared) throw FootprintPartIdsMismatch(reference.Key, definition.Parts[shared].Id);
        if (footprint.Parts.Count > shared) throw FootprintPartIdsMismatch(reference.Key, footprint.Parts[shared].PartId);
        return footprint;
    }

    static LiftFootprintDataException FootprintPartIdsMismatch(string key, uint partId) =>
        new(LiftFootprintRefusal.PartIdsMismatch, $"{key}: footprint and definition differ at part ID {partId}.")
        { SourceName = key, PartId = partId };

    static LiftFootprint?[]? ReadOutfitFootprints(LoadedBodyRegions body, BoundAttachmentSet[] sets,
        ReadOnlyMemory<uint>[]?[][] tagged)
    {
        LiftFootprint?[]? footprints = null;
        long? bodyTriangleCount = null;
        string bodyHash = body.Map.Sha256;
        for (int wornIndex = 0; wornIndex < sets.Length; wornIndex++)
        {
            BoundAttachmentSet set = sets[wornIndex];
            if (set.Footprint is not { } footprint) continue;
            if (footprint.BodyRegionMapSha256 == bodyHash &&
                footprint.BodyTriangleCount == (bodyTriangleCount ??= body.Indices.Sum(part => (long)part.Length / 3)))
                CheckFootprintTriangles(set, wornIndex, footprint, tagged[wornIndex]);
            (footprints ??= new LiftFootprint?[sets.Length])[wornIndex] = footprint;
        }
        return footprints;
    }

    static void CheckFootprintTriangles(BoundAttachmentSet set, int wornIndex, LiftFootprint footprint,
        ReadOnlyMemory<uint>[]?[] tagged)
    {
        for (int part = 0; part < footprint.Parts.Count; part++)
        {
            // Loading matched the part IDs. A footprint needs a layer, so every skinned part has tagged indices.
            LiftFootprintPart measured = footprint.Parts[part];
            int expected = tagged[part] is { } indices ? TriangleCount(indices) : set.Resources[part].RigidTriangleCount;
            if (measured.TriangleCount != expected)
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.FootprintTriangleCountMismatch,
                    $"{set.SourceName}: part {measured.PartId} has {measured.TriangleCount} footprint triangles, its mesh has {expected}.")
                {
                    SourceName = set.SourceName, WornIndex = wornIndex, PartId = measured.PartId,
                    ExpectedTriangleCount = expected, ActualTriangleCount = measured.TriangleCount,
                };
        }
    }

    static int TriangleCount(ReadOnlyMemory<uint>[] indices)
    {
        int triangles = 0;
        foreach (ReadOnlyMemory<uint> material in indices) triangles += material.Length / 3;
        return triangles;
    }

    static bool[] FootprintApplications(OutfitLift lift, int wornCount)
    {
        var applied = new bool[wornCount];
        for (int index = 0; index < wornCount; index++) applied[index] = lift.FootprintApplied(index);
        return applied;
    }
}
