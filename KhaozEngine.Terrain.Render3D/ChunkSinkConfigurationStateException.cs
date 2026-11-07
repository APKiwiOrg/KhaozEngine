using System;

namespace KhaozEngine.Terrain;

/// <summary>A structured refusal of ChunkSinkConfiguration input or state.</summary>
public sealed class ChunkSinkConfigurationStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ChunkSinkConfigurationStateException(ChunkSinkConfigurationRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public ChunkSinkConfigurationStateException(ChunkSinkConfigurationRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public ChunkSinkConfigurationRefusal Reason { get; }

    /// <summary>The number of CPU chunk builds still running when configuration replacement was refused.</summary>
    public int BuildCount { get; init; }
}
