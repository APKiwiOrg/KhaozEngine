using System;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A verified body collider set. Borrows its cache.</summary>
public sealed class LoadedBodyColliders
{
    readonly AttachmentSetCache _owner;
    readonly BodyColliderSet _set;

    internal LoadedBodyColliders(AttachmentSetCache owner, string sourceName, BodyColliderSet set)
    {
        _owner = owner;
        SourceName = sourceName;
        _set = set;
    }

    internal string SourceName { get; }

    /// <summary>The verified collider set.</summary>
    public BodyColliderSet Set
    {
        get { _owner.ThrowIfDisposed(); return _set; }
    }

    internal BodyColliderSet ForRig(string expectedRigIdentity)
    {
        BodyColliderSet set = Set;
        if (!string.Equals(set.RigIdentity, expectedRigIdentity, StringComparison.Ordinal))
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyCollidersMismatch,
                $"{SourceName}: the collider file tags rig '{set.RigIdentity}', but the body uses '{expectedRigIdentity}'.")
            { SourceName = SourceName, ExpectedRigIdentity = expectedRigIdentity, ActualRigIdentity = set.RigIdentity };
        return set;
    }
}
