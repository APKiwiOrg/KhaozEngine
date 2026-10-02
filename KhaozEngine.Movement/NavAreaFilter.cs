using System;

namespace KhaozEngine.Movement;

/// <summary>Requires every required area bit and refuses every excluded bit. Zero masks allow all areas.</summary>
public readonly record struct NavAreaFilter
{
    public NavAreaFilter(uint Required, uint Excluded)
    {
        if ((Required & Excluded) != 0u)
            throw new ArgumentException("Required and excluded area bits must not overlap.", nameof(Excluded));
        this.Required = Required;
        this.Excluded = Excluded;
    }

    public uint Required { get; }
    public uint Excluded { get; }

    internal bool Allows(uint areas) => (areas & Required) == Required && (areas & Excluded) == 0u;
}
