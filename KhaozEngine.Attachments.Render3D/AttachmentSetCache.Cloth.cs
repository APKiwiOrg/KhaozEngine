using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    static Dictionary<AttachmentMeshRef, List<ClothRequest>>? ClothRequests(AttachmentSetDefinition definition)
    {
        Dictionary<AttachmentMeshRef, List<ClothRequest>>? requests = null;
        for (int partIndex = 0; partIndex < definition.Parts.Count; partIndex++)
        {
            if (definition.Parts[partIndex] is not SkinnedAttachmentPartDefinition { Motion: ClothMotionDefinition cloth } part)
                continue;
            requests ??= [];
            if (!requests.TryGetValue(part.Mesh, out List<ClothRequest>? parts)) requests.Add(part.Mesh, parts = []);
            parts.Add(new(partIndex, part.Id, part.Mesh.Sha256, cloth));
        }
        return requests;
    }

    CheckedClothSurface ReadClothSurface(ClothRequest request, byte[] glb)
    {
        ClothSurfaceRef reference = request.Motion.Surface;
        byte[] sidecar;
        try { sidecar = _source.Read(reference.Key).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.AssetRead, reference.Key, "The cloth sidecar cannot be read.", error)
            { PartId = request.PartId };
        }

        if (Convert.ToHexStringLower(SHA256.HashData(sidecar)) != reference.Sha256)
            throw new ClothSurfaceDataException(ClothSurfaceRefusal.AssetDigest, reference.Key, "The cloth sidecar bytes differ from the declared SHA-256.")
            { PartId = request.PartId };
        ClothSurfaceFile file = ClothSurfaceJson.ParseChecked(sidecar, reference.Key, request.PartId, out ClothRig rig);
        return ClothSurfaceBinding.Read(glb, request.MeshSha256, file, request.Motion.Fallback, rig, reference.Key, request.PartId);
    }

    readonly record struct ClothRequest(int Index, uint PartId, string MeshSha256, ClothMotionDefinition Motion);
}
