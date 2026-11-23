using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Attachments;

internal static class CoverageClaimSwingEvidence
{
    internal static void Validate(AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        if (piece.Parts.Any(HasMotion))
            ResolvePieceMeshes(piece, pieceMeshes, footprint: false);
    }

    internal static Dictionary<uint, int> ResolveFootprintMeshes(AttachmentSetDefinition piece,
        IReadOnlyList<CoverageClaimMesh> pieceMeshes) => ResolvePieceMeshes(piece, pieceMeshes, footprint: true);

    static Dictionary<uint, int> ResolvePieceMeshes(AttachmentSetDefinition piece,
        IReadOnlyList<CoverageClaimMesh> pieceMeshes, bool footprint)
    {
        IReadOnlyList<AttachmentPartDefinition> parts = piece.Parts;
        Dictionary<uint, AttachmentPartDefinition> partsById = parts.ToDictionary(part => part.Id);
        var seen = new Dictionary<uint, int>();
        for (int index = 0; index < pieceMeshes.Count; index++)
        {
            CoverageClaimMesh mesh = pieceMeshes[index];
            uint id = mesh.PartId;
            if (id == 0)
                throw new CoverageClaimDataException(footprint ? CoverageClaimRefusal.FootprintMeshPartIdMissing : CoverageClaimRefusal.PieceMeshPartIdMissing,
                    $"Piece mesh {index} needs a definition part ID.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id };
            if (!partsById.TryGetValue(id, out AttachmentPartDefinition? part))
                throw new CoverageClaimDataException(footprint ? CoverageClaimRefusal.FootprintMeshPartIdUnknown : CoverageClaimRefusal.PieceMeshPartIdUnknown,
                    $"Piece mesh {index} names part {id}, which the piece does not declare.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id };
            if (!seen.TryAdd(id, index))
                throw new CoverageClaimDataException(footprint ? CoverageClaimRefusal.FootprintMeshPartIdDuplicate : CoverageClaimRefusal.PieceMeshPartIdDuplicate,
                    $"Piece mesh {index} names part {id}, which already has a mesh.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id, MeshKey = part.Mesh.Key };
            if (HasMotion(part) && mesh.Swinging.IsEmpty)
                throw new CoverageClaimDataException(CoverageClaimRefusal.SwingMaskMissing,
                    $"Piece part {id}, mesh '{part.Mesh.Key}', declares motion and requires a swinging vertex mask.")
                { MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = index, PartId = id, MeshKey = part.Mesh.Key };
        }

        if (seen.Count != parts.Count)
        {
            foreach (AttachmentPartDefinition part in parts)
                if (!seen.ContainsKey(part.Id))
                    throw new CoverageClaimDataException(footprint ? CoverageClaimRefusal.FootprintPartMeshMissing : CoverageClaimRefusal.PiecePartMeshMissing,
                        $"Piece part {part.Id}, mesh '{part.Mesh.Key}', has no mesh in the claim.")
                    { MeshKind = CoverageClaimMeshKind.Piece, PartId = part.Id, MeshKey = part.Mesh.Key };
        }
        return seen;
    }

    static bool HasMotion(AttachmentPartDefinition part) => part is SkinnedAttachmentPartDefinition { Motion: not null };
}
