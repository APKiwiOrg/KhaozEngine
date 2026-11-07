using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of attachment-set JSON or reference data.</summary>
public sealed class AttachmentSetDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentSetDataException(AttachmentSetRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentSetDataException(AttachmentSetRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentSetRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public JSON boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The canonical JSON schema location of the refused value or containing object.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The missing, repeated, unknown or non-string property name.</summary>
    public string? PropertyName { get; init; }

    /// <summary>The refused format version when representable as an Int32.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The unsupported authored part kind.</summary>
    public string? Kind { get; init; }

    /// <summary>The local-transform array length, or null when the value is not an array.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The required local-transform component count.</summary>
    public int? ExpectedCount { get; init; }

    /// <summary>The zero-based index of the refused local-transform component.</summary>
    public int? ComponentIndex { get; init; }

    /// <summary>The original parameter name when JSON admission wraps a constructor refusal.</summary>
    public string? ParamName { get; init; }

    /// <summary>The number of parts in an empty set.</summary>
    public int? PartCount { get; init; }

    /// <summary>The zero-based input index of the refused part.</summary>
    public int? PartIndex { get; init; }

    /// <summary>The stable identifier of the refused part.</summary>
    public uint? PartId { get; init; }

    /// <summary>The unsupported runtime part type.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The refused mesh asset key.</summary>
    public string? MeshKey { get; init; }

    /// <summary>The hash declared for the refused mesh.</summary>
    public string? DeclaredHash { get; init; }

    /// <summary>The previously declared hash for a conflicting mesh key.</summary>
    public string? ExistingHash { get; init; }

    /// <summary>The rigid joint name involved in the refusal.</summary>
    public string? JointName { get; init; }

    /// <summary>The mesh hash required by the set reference closure.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>The mesh hash supplied by the reference closure, or null when missing.</summary>
    public string? ActualHash { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentSetDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        JsonPath = JsonPath,
        PropertyName = PropertyName,
        FormatVersion = FormatVersion,
        Kind = Kind,
        ActualCount = ActualCount,
        ExpectedCount = ExpectedCount,
        ComponentIndex = ComponentIndex,
        ParamName = ParamName,
        PartCount = PartCount,
        PartIndex = PartIndex,
        PartId = PartId,
        ActualType = ActualType,
        MeshKey = MeshKey,
        DeclaredHash = DeclaredHash,
        ExistingHash = ExistingHash,
        JointName = JointName,
        ExpectedHash = ExpectedHash,
        ActualHash = ActualHash,
    };
}
