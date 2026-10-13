using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A skeleton binding that borrows its mesh resources from the cache.</summary>
public sealed class BoundRigidAttachmentSet
{
    readonly RigidAttachmentSetCache _owner;
    readonly RigidAttachmentBinding _binding;
    readonly int _drawCount;

    internal BoundRigidAttachmentSet(RigidAttachmentSetCache owner, RigidAttachmentSetDefinition definition,
        MeshHandle[][] partHandles, ContractJointMap map)
    {
        _owner = owner;
        _binding = new RigidAttachmentBinding(definition, map);
        foreach (MeshHandle[] handles in partHandles) _drawCount += handles.Length;
    }

    public Skeleton Skeleton
    {
        get { _owner.ThrowIfDisposed(); return _binding.Skeleton; }
    }

    /// <summary>One draw per material group of every canonical part.</summary>
    public int DrawCount
    {
        get { _owner.ThrowIfDisposed(); return _drawCount; }
    }
}
