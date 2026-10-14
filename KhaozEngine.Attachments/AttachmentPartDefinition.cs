using System.Numerics;

namespace KhaozEngine.Attachments;

public readonly record struct AttachmentMeshRef(string Key, string Sha256);
public abstract record AttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh);
public sealed record RigidAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh, string Joint, Matrix4x4 Local)
    : AttachmentPartDefinition(Id, Mesh);
public sealed record SkinnedAttachmentPartDefinition(uint Id, AttachmentMeshRef Mesh) : AttachmentPartDefinition(Id, Mesh);
