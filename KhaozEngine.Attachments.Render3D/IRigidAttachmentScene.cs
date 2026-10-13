using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Owns the resources from one successful attachment upload. Dispose before its scene.</summary>
/// <remarks>Disposal is idempotent and releases meshes before their textures. Handles are immutable and borrowed.</remarks>
public interface IAttachmentMeshLease : IDisposable
{
    IReadOnlyList<MeshHandle> Handles { get; }
}

/// <summary>Optional scene capability for attachment upload and complete rigid draw descriptors.</summary>
public interface IRigidAttachmentScene
{
    /// <summary>Upload all material groups atomically. A failure releases only resources acquired by this call.</summary>
    IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts);
    void DrawMesh(in RigidInstanceDraw draw);
}
