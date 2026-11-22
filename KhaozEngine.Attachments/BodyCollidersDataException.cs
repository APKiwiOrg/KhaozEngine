using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of body collider data.</summary>
public sealed class BodyCollidersDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public BodyCollidersDataException(BodyCollidersRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public BodyCollidersDataException(BodyCollidersRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public BodyCollidersRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public JSON boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The schema location of the refused value or containing object, in input order.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The unknown, repeated or missing property name.</summary>
    public string? PropertyName { get; init; }

    /// <summary>The refused format version when representable as an Int32.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The refused rig identity.</summary>
    public string? RigIdentity { get; init; }

    /// <summary>The zero-based position of the refused collider in the input sequence.</summary>
    public int? ColliderIndex { get; init; }

    /// <summary>The joint name of a collider refused for its name, points or radius, when available.</summary>
    public string? JointName { get; init; }

    /// <summary>The size of a collider sequence that exceeds the limit.</summary>
    public int? ColliderCount { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new BodyCollidersDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        JsonPath = JsonPath,
        PropertyName = PropertyName,
        FormatVersion = FormatVersion,
        RigIdentity = RigIdentity,
        ColliderIndex = ColliderIndex,
        JointName = JointName,
        ColliderCount = ColliderCount,
    };
}
