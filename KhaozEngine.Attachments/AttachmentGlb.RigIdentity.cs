using System;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    /// <summary>Returns a versioned SHA-256 identity of a body's skin, rest frames and fitted geometry.</summary>
    /// <remarks>Animations and appearance are excluded. Persist the complete rig-v1: prefixed value.</remarks>
    public static string ComputeRigIdentity(ReadOnlyMemory<byte> glb, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using var container = new Container(glb, validateImages: false);
            return RigIdentity.Compute(container.Root, container.Binary);
        }
        catch (MalformedDataException error)
        {
            if (error is IAttachmentDataException sourced) throw sourced.WithSource(sourceName);
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw SourceFailure(error, sourceName);
        }
    }
}
