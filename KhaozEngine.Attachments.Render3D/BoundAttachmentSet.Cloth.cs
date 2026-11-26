using System;
using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class BoundAttachmentSet
{
    /// <summary>Get the checked fitted surface for a cloth part in this set.</summary>
    public BoundClothSurface? ClothSurface(uint partId)
    {
        _owner.ThrowIfDisposed();
        return _binding.ClothById(partId);
    }

    /// <summary>Get detailed contact hulls and their matching fallback capsule indices.</summary>
    public ImmutableArray<BoundClothHull> ClothHulls
    {
        get { _owner.ThrowIfDisposed(); return _binding.ClothHulls; }
    }

    /// <summary>Borrow the fitted cloth sample from a motion created by this set.</summary>
    public bool TryGetClothPresentation(WornMotion motion, uint partId, out WornClothPresentation presentation)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(motion);
        motion.RequireBinding(MotionIdentity);
        return motion.TryGetClothPresentation(0, partId, out presentation);
    }

    internal void CopyClothFallbackPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> host, uint partId,
        Span<Matrix4x4> garment) => _binding.CopySkinnedPalette(skeleton, host, partId, garment);

    internal BoundClothSurface? PartCloth(int part) => _binding.Cloth(part);
}
