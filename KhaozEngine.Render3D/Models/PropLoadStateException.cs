using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of PropLoad inputs.</summary>
public sealed class PropLoadStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PropLoadStateException(PropLoadRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public PropLoadStateException(PropLoadRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public PropLoadRefusal Reason { get; }

    /// <summary>The manifest entry id, or null for direct normalization.</summary>
    public string? EntryId { get; init; }

    /// <summary>The source file path for an asset load failure.</summary>
    public string? Path { get; init; }

    /// <summary>The declared height in metres.</summary>
    public float? HeightMeters { get; init; }

    /// <summary>The minimum admitted declared height in metres.</summary>
    public float? MinimumHeightMeters { get; init; }

    /// <summary>The maximum admitted declared height in metres.</summary>
    public float? MaximumHeightMeters { get; init; }

    /// <summary>The vertical extent of the raw geometry.</summary>
    public float? RawHeight { get; init; }

    /// <summary>The uniform raw-to-declared scale that was refused.</summary>
    public float? Scale { get; init; }

    /// <summary>The minimum admitted normalization scale.</summary>
    public float? MinimumScale { get; init; }

    /// <summary>The maximum admitted normalization scale.</summary>
    public float? MaximumScale { get; init; }
}
