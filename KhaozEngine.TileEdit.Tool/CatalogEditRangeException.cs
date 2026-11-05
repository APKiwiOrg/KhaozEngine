using System;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>A catalog edit height refusal that preserves ArgumentOutOfRangeException catch compatibility.</summary>
public sealed class CatalogEditRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its stable reason and original parameter and value context.</summary>
    public CatalogEditRangeException(TileWorldRefusal reason, string message, string? paramName, object? actualValue)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The stable reason the catalog edit height was refused.</summary>
    public TileWorldRefusal Reason { get; }

    /// <summary>The archetype whose supplied height was refused.</summary>
    public string? ArchetypeId { get; init; }
}
