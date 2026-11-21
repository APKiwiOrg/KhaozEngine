using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of an outfit resolved against a body's region map.</summary>
public sealed class OutfitDataException : MalformedDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public OutfitDataException(OutfitRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public OutfitDataException(OutfitRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the outfit was refused.</summary>
    public OutfitRefusal Reason { get; }

    /// <summary>The zero-based position of the refused set in the worn list.</summary>
    public int? WornIndex { get; init; }

    /// <summary>The refused part's ID, or null when the name is one of the set's covered regions.</summary>
    public uint? PartId { get; init; }

    /// <summary>The zero-based index of the refused run within a skinned part, or null for a covered region or a rigid part.</summary>
    public int? RunIndex { get; init; }

    /// <summary>The region name the body does not declare.</summary>
    public string? RegionName { get; init; }
}
