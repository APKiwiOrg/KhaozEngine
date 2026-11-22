using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Attachments;

internal static class CoverageClaimSwingEvidence
{
    internal static void Validate(AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        IReadOnlyList<AttachmentPartDefinition> parts = piece.Parts;
        if (!parts.Any(HasMotion)) return;

        Dictionary<uint, AttachmentPartDefinition> partsById = parts.ToDictionary(part => part.Id);
        var seen = new HashSet<uint>();
        for (int index = 0; index < pieceMeshes.Count; index++)
        {
            CoverageClaimMesh mesh = pieceMeshes[index];
            uint id = mesh.PartId;
            if (id == 0)
                throw new CoverageClaimDataException(CoverageClaimRefusal.PieceMeshPartIdMissing,
                    $"Piece mesh {index} needs a definition part ID.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id };
            if (!partsById.TryGetValue(id, out AttachmentPartDefinition? part))
                throw new CoverageClaimDataException(CoverageClaimRefusal.PieceMeshPartIdUnknown,
                    $"Piece mesh {index} names part {id}, which the piece does not declare.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id };
            if (!seen.Add(id))
                throw new CoverageClaimDataException(CoverageClaimRefusal.PieceMeshPartIdDuplicate,
                    $"Piece mesh {index} names part {id}, which already has a mesh.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id, MeshKey = part.Mesh.Key };
            if (HasMotion(part) && mesh.Swinging.IsEmpty)
                throw new CoverageClaimDataException(CoverageClaimRefusal.SwingMaskMissing,
                    $"Piece part {id}, mesh '{part.Mesh.Key}', declares motion and requires a swinging vertex mask.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id, MeshKey = part.Mesh.Key };
        }

        if (seen.Count == parts.Count) return;
        foreach (AttachmentPartDefinition part in parts)
            if (!seen.Contains(part.Id))
                throw new CoverageClaimDataException(CoverageClaimRefusal.PiecePartMeshMissing,
                    $"Piece part {part.Id}, mesh '{part.Mesh.Key}', has no mesh in the claim.")
                { MeshKind = CoverageClaimMeshKind.Piece, PartId = part.Id, MeshKey = part.Mesh.Key };
    }

    static bool HasMotion(AttachmentPartDefinition part) => part is SkinnedAttachmentPartDefinition { Motion: not null };
}
