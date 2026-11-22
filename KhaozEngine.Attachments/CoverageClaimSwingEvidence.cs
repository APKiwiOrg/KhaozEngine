using System;
using System.Collections.Generic;

namespace KhaozEngine.Attachments;

internal static class CoverageClaimSwingEvidence
{
    internal static void Validate(AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        IReadOnlyList<AttachmentPartDefinition> parts = piece.Parts;
        for (int index = 0; index < parts.Count; index++)
        {
            if (parts[index] is not SkinnedAttachmentPartDefinition { Motion: not null } part)
                continue;
            if (parts.Count != pieceMeshes.Count)
                throw new ArgumentException("A piece with motion needs one mesh per definition part in part ID order.", nameof(pieceMeshes));
            if (pieceMeshes[index].Swinging.IsEmpty)
                throw new CoverageClaimDataException(CoverageClaimRefusal.SwingMaskMissing,
                    $"Piece part {part.Id}, mesh '{part.Mesh.Key}', declares motion and requires a swinging vertex mask.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = part.Id, MeshKey = part.Mesh.Key };
        }
    }
}
