using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// The per-cell surface source the step-surface overworld bake reads: the walkable surface height and
/// the headroom above it at a world XZ point. The default engine implementation is TerrainSurfaceProvider
/// (analytic terrain raised by <c>WorldSurfaces</c> prop tops). A game supplies its own (for example a
/// downward <c>PhysicsGroundProbe</c> raycast) by implementing this interface, so the bake reads a
/// physics-derived surface without KhaozEngine.Navigation taking a dependency on KhaozEngine.Physics.
/// Must be deterministic: identical inputs must return identical outputs, or the bake is not deterministic.
/// </summary>
public interface INavSurfaceProvider
{
    /// <summary>
    /// Samples the walkable surface at world (<paramref name="x"/>, <paramref name="z"/>). Returns
    /// false when there is no standable surface there (a hole, out of bounds, or a solid obstacle with
    /// no standable top), in which case the cell bakes as blocked and the out values are ignored. On
    /// true, <paramref name="height"/> is the world Y of the surface top the agent stands on and
    /// <paramref name="headroom"/> is the clear vertical space above it in world units
    /// (<see cref="float.PositiveInfinity"/> for open sky).
    /// </summary>
    bool TrySample(float x, float z, out float height, out float headroom);
}

/// <summary>
/// An <see cref="INavSurfaceProvider"/> backed by a delegate, so a game can supply a surface source
/// (a physics probe, a scripted height field) without declaring a named class. The delegate carries
/// the same contract as <see cref="INavSurfaceProvider.TrySample"/> and must be deterministic.
/// </summary>
public sealed class DelegateSurfaceProvider : INavSurfaceProvider
{
    /// <summary>The delegate form of <see cref="INavSurfaceProvider.TrySample"/>.</summary>
    public delegate bool SampleFunc(float x, float z, out float height, out float headroom);

    readonly SampleFunc _sample;

    /// <summary>Wraps <paramref name="sample"/>. Throws <see cref="ArgumentNullException"/> when null.</summary>
    public DelegateSurfaceProvider(SampleFunc sample)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
    }

    /// <inheritdoc/>
    public bool TrySample(float x, float z, out float height, out float headroom)
        => _sample(x, z, out height, out headroom);
}
