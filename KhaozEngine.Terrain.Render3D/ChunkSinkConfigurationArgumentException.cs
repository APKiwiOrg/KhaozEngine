using System;

namespace KhaozEngine.Terrain;

/// <summary>A structured refusal of ChunkSinkConfiguration input or state.</summary>
public sealed class ChunkSinkConfigurationArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ChunkSinkConfigurationArgumentException(ChunkSinkConfigurationRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public ChunkSinkConfigurationArgumentException(ChunkSinkConfigurationRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public ChunkSinkConfigurationRefusal Reason { get; }
}
