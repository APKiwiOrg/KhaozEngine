using System.Collections.Generic;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    readonly Dictionary<MeshIdentity, LiftBasis> _liftBases = [];

    internal LiftBasis? FindLiftBasis(AttachmentMeshRef mesh) =>
        _liftBases.GetValueOrDefault(new MeshIdentity(mesh, Skinned: true));

    Dictionary<AttachmentMeshRef, uint>? UncachedLiftRequests(AttachmentSetDefinition definition)
    {
        Dictionary<AttachmentMeshRef, uint>? requests = null;
        foreach (AttachmentPartDefinition part in definition.Parts)
            if (part is SkinnedAttachmentPartDefinition { Lift: not null } && FindLiftBasis(part.Mesh) is null)
                (requests ??= []).TryAdd(part.Mesh, part.Id);
        return requests;
    }
}
