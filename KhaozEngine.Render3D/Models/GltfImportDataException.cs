using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of GltfImport inputs.</summary>
public sealed class GltfImportDataException : MalformedDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public GltfImportDataException(GltfImportRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public GltfImportDataException(GltfImportRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public GltfImportRefusal Reason { get; }

    /// <summary>The raw asset path or caller-supplied asset identity.</summary>
    public string? Identity { get; init; }
}
