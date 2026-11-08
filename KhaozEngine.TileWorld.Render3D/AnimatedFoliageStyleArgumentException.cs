using System;

namespace KhaozEngine.TileWorld;

/// <summary>A structured refusal of AnimatedFoliageStyle input.</summary>
public sealed class AnimatedFoliageStyleArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public AnimatedFoliageStyleArgumentException(AnimatedFoliageStyleRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public AnimatedFoliageStyleArgumentException(AnimatedFoliageStyleRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AnimatedFoliageStyleRefusal Reason { get; }

    /// <summary>The authored archetype id that was refused. Null when unused by the reason.</summary>
    public string? ArchetypeId { get; init; }

    /// <summary>The first selected archetype sharing the mesh. Null when unused by the reason.</summary>
    public string? FirstArchetypeId { get; init; }

    /// <summary>The conflicting selected archetype sharing the mesh. Null when unused by the reason.</summary>
    public string? SecondArchetypeId { get; init; }
}
