using System;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class BoundOutfit
{
    /// <summary>Whether the worn set at <paramref name="wornIndex"/> used its measured body-cell footprint.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The worn index is outside the outfit.</exception>
    /// <exception cref="ObjectDisposedException">The outfit or its cache has been disposed.</exception>
    public bool FootprintApplied(int wornIndex)
    {
        ThrowIfDisposed();
        CheckWornIndex(wornIndex);
        return _footprintApplied?[wornIndex] ?? false;
    }
}
