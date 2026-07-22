using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// The many-surfaces-per-column source the layered overworld bake reads: every standable surface in
/// the vertical column at a world XZ point, each with its own headroom. This is the phase-2 widening
/// of the <see cref="INavSurfaceProvider"/> seam: Navigation still never references Physics, and the
/// GAME implements this over its own physics world (a repeated downward raycast sweep such as
/// <c>PhysicsColumnProbe</c> in KhaozEngine.Physics, glued with a one-line delegate) and hands it to
/// <see cref="NavLayerBaker.BakeOverworldLayered"/>. Must be deterministic: identical inputs must
/// return identical outputs, or the bake is not deterministic.
/// </summary>
public interface INavColumnProvider
{
    /// <summary>
    /// Samples every standable surface in the column at world (<paramref name="x"/>, <paramref name="z"/>)
    /// into <paramref name="surfaces"/>, bottom-up (strictly ascending <see cref="NavSurfaceSample.Height"/>),
    /// and returns how many were written. Zero means no standable surface in the column (a hole, out of
    /// bounds, solid rock). Each entry's <see cref="NavSurfaceSample.Headroom"/> is the clear vertical
    /// space above THAT surface (<see cref="float.PositiveInfinity"/> for open sky). Entries with
    /// <see cref="NavSurfaceSample.Standable"/> false are permitted and skipped by the bake, so an
    /// adapter can forward a single-surface miss verbatim. A provider must never return more than
    /// <paramref name="surfaces"/>.Length: when the column genuinely holds more surfaces than the
    /// buffer, drop the excess deterministically (by convention the highest ones).
    /// </summary>
    int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces);
}

/// <summary>
/// An <see cref="INavColumnProvider"/> backed by a delegate, so a game or a test can supply a column
/// source (a physics sweep, a scripted layer stack) without declaring a named class. The delegate
/// carries the same contract as <see cref="INavColumnProvider.SampleColumn"/> and must be deterministic.
/// </summary>
public sealed class DelegateColumnProvider : INavColumnProvider
{
    /// <summary>The delegate form of <see cref="INavColumnProvider.SampleColumn"/>.</summary>
    public delegate int SampleColumnFunc(float x, float z, Span<NavSurfaceSample> surfaces);

    readonly SampleColumnFunc _sample;

    /// <summary>Wraps <paramref name="sample"/>. Throws <see cref="ArgumentNullException"/> when null.</summary>
    public DelegateColumnProvider(SampleColumnFunc sample)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
    }

    /// <inheritdoc/>
    public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
        => _sample(x, z, surfaces);
}

/// <summary>
/// Adapts a single-surface <see cref="INavSurfaceProvider"/> to the <see cref="INavColumnProvider"/>
/// seam: every column reports at most the one surface <see cref="INavSurfaceProvider.TrySample"/>
/// returns. Lets a phase-1 world (analytic terrain, <see cref="TerrainSurfaceProvider"/>) run through
/// the layered bake unchanged, which then degenerates to a single layer.
/// </summary>
public sealed class SurfaceColumnAdapter : INavColumnProvider
{
    readonly INavSurfaceProvider _surface;

    /// <summary>Wraps <paramref name="surface"/>. Throws <see cref="ArgumentNullException"/> when null.</summary>
    public SurfaceColumnAdapter(INavSurfaceProvider surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    /// <inheritdoc/>
    public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
    {
        if (surfaces.Length == 0) return 0;
        if (!_surface.TrySample(x, z, out float height, out float headroom)) return 0;
        surfaces[0] = new NavSurfaceSample(true, height, headroom);
        return 1;
    }
}
