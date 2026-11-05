using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A server state admission refusal that preserves ArgumentException catch compatibility.</summary>
public sealed class TileServerStateArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileServerStateArgumentException(TileServerStateRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The stable reason the authoritative state was refused.</summary>
    public TileServerStateRefusal Reason { get; }

    /// <summary>The refused player footprint edge in tiles.</summary>
    public int? FootprintSize { get; init; }

    /// <summary>The state's tile, including its supplied plane, when refused for placement or step origin.</summary>
    public TileCoord? Tile { get; init; }

    /// <summary>The refused plane supplied by the state.</summary>
    public int? Plane { get; init; }

    /// <summary>The number of planes in the configured world.</summary>
    public int? PlaneCount { get; init; }

    /// <summary>The region the collision map has not loaded.</summary>
    public RegionCoord? Region { get; init; }

    /// <summary>The refused origin of the state's step in flight.</summary>
    public TileCoord? StepFrom { get; init; }

    /// <summary>The nonzero step progress that was not below the total duration.</summary>
    public byte? StepTicks { get; init; }

    /// <summary>The step duration the refused progress had reached or exceeded.</summary>
    public byte? StepTotal { get; init; }

    /// <summary>The refused route's remaining step count.</summary>
    public int? RemainingSteps { get; init; }

    /// <summary>The configured maximum number of remaining route steps.</summary>
    public int? MaximumSteps { get; init; }
}
