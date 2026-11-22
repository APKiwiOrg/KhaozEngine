using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A verified body collider set for one skeleton instance. Borrows its cache.</summary>
public sealed class LoadedBodyColliders
{
    readonly AttachmentSetCache _owner;
    readonly BodyColliderSet _set;
    readonly Skeleton _skeleton;

    internal LoadedBodyColliders(AttachmentSetCache owner, string sourceName, BodyColliderSet set, Skeleton skeleton)
    {
        _owner = owner;
        SourceName = sourceName;
        _set = set;
        _skeleton = skeleton;
    }

    internal string SourceName { get; }

    /// <summary>The verified collider set.</summary>
    public BodyColliderSet Set
    {
        get { _owner.ThrowIfDisposed(); return _set; }
    }

    internal BodyColliderSet ForBody(Skeleton skeleton, string? expectedRigIdentity)
    {
        BodyColliderSet set = Set;
        if (expectedRigIdentity is not null)
            RequireRig(SourceName, set, expectedRigIdentity);
        if (!ReferenceEquals(_skeleton, skeleton))
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyCollidersMismatch,
                $"{SourceName}: the colliders were loaded for a different skeleton instance.")
            { SourceName = SourceName, ExpectedRigIdentity = expectedRigIdentity, ActualRigIdentity = set.RigIdentity };
        return set;
    }

    internal static void RequireRig(string sourceName, BodyColliderSet set, string expectedRigIdentity)
    {
        if (!string.Equals(set.RigIdentity, expectedRigIdentity, StringComparison.Ordinal))
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyCollidersMismatch,
                $"{sourceName}: the collider file tags rig '{set.RigIdentity}', but the body uses '{expectedRigIdentity}'.")
            { SourceName = sourceName, ExpectedRigIdentity = expectedRigIdentity, ActualRigIdentity = set.RigIdentity };
    }
}
