using System;

namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    /// <summary>A distinct binding retaining every real flight input and the original ordinary environment.
    /// It never borrows the normalized ground proof's Hold or mutates a cached runtime binding.</summary>
    internal EffectiveMovementBinding CreateFallProofBinding()
    {
        AssertUsable();
        if (Kind != EffectiveMovementBindingKind.OrdinaryContact)
            throw new InvalidOperationException("A fall proof requires ordinary contact movement.");
        return new(Kind, Tuning, Water, _context, _ordinary, _view, composition: _composition);
    }
}
