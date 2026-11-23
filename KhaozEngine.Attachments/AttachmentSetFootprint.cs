using System.Collections.Generic;

namespace KhaozEngine.Attachments;

internal static class AttachmentSetFootprint
{
    internal const string Path = "$.footprint";

    internal static void RequireLayer(LiftFootprintRef? footprint, AttachmentLayer? layer)
    {
        if (footprint is { } reference && layer is null)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.FootprintWithoutLayer,
                "A set with a lift footprint requires a layer.", nameof(footprint))
            { JsonPath = "$", PropertyName = "layer", FootprintKey = reference.Key };
    }

    internal static void CheckDefinition(LiftFootprintRef? footprint, AttachmentLayer? layer,
        IReadOnlyDictionary<string, AttachmentMeshRef> meshes)
    {
        if (footprint is not { } reference) return;
        RequireLayer(reference, layer);
        string? keyError = AttachmentAssetKey.Error(reference.Key);
        if (keyError is not null)
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.FootprintKeyInvalid,
                $"Lift footprint key '{reference.Key}' {keyError}.", nameof(footprint))
            { JsonPath = Path + ".key", FootprintKey = reference.Key };
        if (!AttachmentSetDefinition.ValidHash(reference.Sha256))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.FootprintHashInvalid,
                $"Lift footprint '{reference.Key}' requires a lowercase SHA-256.", nameof(footprint))
            { JsonPath = Path + ".sha256", FootprintKey = reference.Key, DeclaredHash = reference.Sha256 };
        if (meshes.ContainsKey(reference.Key))
            throw new AttachmentSetArgumentException(AttachmentSetRefusal.FootprintKeyUsedByMesh,
                $"Lift footprint key '{reference.Key}' is already used by a part's mesh.", nameof(footprint))
            { JsonPath = Path + ".key", FootprintKey = reference.Key };
    }

    internal static void CheckReference(LiftFootprintRef? footprint, IReadOnlyDictionary<string, string> knownAssets)
    {
        if (footprint is not { } reference) return;
        if (!knownAssets.TryGetValue(reference.Key, out string? actual) || actual != reference.Sha256)
            throw new AttachmentSetDataException(AttachmentSetRefusal.FootprintReferenceMissingOrHashMismatch,
                $"Lift footprint '{reference.Key}' is missing or its SHA-256 differs.")
            { FootprintKey = reference.Key, ExpectedHash = reference.Sha256, ActualHash = actual };
    }
}
