using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Attachment uploads and complete mixed draws and outlines through an existing Scene3D.</summary>
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
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices) =>
        _scene.DrawSkinned(draw, boneMatrices);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh,
        Matrix4x4 world, float dissolve, bool dissolveComplement) =>
        _scene.DrawMeshOutlineDissolved(group, mesh, world, dissolve, dissolveComplement);
    public void DrawSkinnedOutlineDissolved(MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world, float dissolve, bool dissolveComplement) =>
        _scene.DrawSkinnedOutlineDissolved(group, mesh, boneMatrices, world, dissolve, dissolveComplement);
}
