using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Attachment uploads and complete rigid draws through an existing Scene3D.</summary>
public sealed class Scene3DAttachmentScene : IAttachmentScene
{
    readonly Scene3D _scene;

    public Scene3DAttachmentScene(Scene3D scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
    }

    public IAttachmentMeshLease UploadAttachment(IReadOnlyList<GltfMeshPart> parts) => AttachmentUpload.Create(_scene, parts);
    public ISkinnedAttachmentMeshLease UploadSkinnedAttachment(IReadOnlyList<GltfSkinnedMeshPart> parts) =>
        SkinnedAttachmentUpload.Create(_scene, parts);
    public void DrawMesh(in RigidInstanceDraw draw) => _scene.Draw(draw);
}
