using System.Collections.Immutable;

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

    internal BoundClothSurface? PartCloth(int part) => _binding.Cloth(part);
}
