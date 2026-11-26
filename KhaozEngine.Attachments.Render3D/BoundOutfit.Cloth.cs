using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class BoundOutfit
{
    readonly Dictionary<uint, BoundClothSurface>?[]? _cloth;

    /// <summary>Get the fitted surface for a cloth part with moving triangles visible in this outfit.</summary>
    public BoundClothSurface? ClothSurface(int wornIndex, uint partId)
    {
        ThrowIfDisposed();
        CheckWornIndex(wornIndex);
        return _cloth?[wornIndex]?.GetValueOrDefault(partId);
    }

    /// <summary>Get the selected set's detailed contact hulls, including supports under a covered region.</summary>
    public ImmutableArray<BoundClothHull> ClothHulls(int wornIndex)
    {
        ThrowIfDisposed();
        CheckWornIndex(wornIndex);
        return _worn[wornIndex].ClothHulls;
    }
}
