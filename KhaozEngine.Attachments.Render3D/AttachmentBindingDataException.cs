using System;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A structured refusal of AttachmentBinding input or access.</summary>
public sealed class AttachmentBindingDataException : MalformedDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentBindingDataException(AttachmentBindingRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentBindingDataException(AttachmentBindingRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public AttachmentBindingRefusal Reason { get; }

    /// <summary>The attachment part involved in the refusal.</summary>
    public uint? PartId { get; init; }

    /// <summary>The joint involved in the refusal.</summary>
    public string? JointName { get; init; }

    /// <summary>The garment mesh key involved in the refusal.</summary>
    public string? MeshKey { get; init; }

    /// <summary>The skin joint count involved in the refusal.</summary>
    public int? JointCount { get; init; }

    /// <summary>The supplied inverse-bind count.</summary>
    public int? InverseBindCount { get; init; }

    /// <summary>The supplied rest-pose count.</summary>
    public int? RestCount { get; init; }

    /// <summary>The inclusive absolute per-element inverse-bind tolerance.</summary>
    public float? BindTolerance { get; init; }

    /// <summary>The skin-bone index involved in the refusal.</summary>
    public int? BoneIndex { get; init; }

    /// <summary>The node index involved in the refusal.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>The garment skeleton node count.</summary>
    public int? NodeCount { get; init; }

    /// <summary>The minimum garment joint count.</summary>
    public int? MinimumJointCount { get; init; }

    /// <summary>The maximum garment joint count.</summary>
    public int? MaximumJointCount { get; init; }

    /// <summary>The cache key of the refused region map or worn set.</summary>
    public string? SourceName { get; init; }

    /// <summary>The zero-based position of the refused set in the outfit's worn list.</summary>
    public int? WornIndex { get; init; }

    /// <summary>The zero-based index of the refused run within a skinned part.</summary>
    public int? RunIndex { get; init; }

    /// <summary>The region name the body does not declare.</summary>
    public string? RegionName { get; init; }

    /// <summary>The rig identity the consumer gave for the body.</summary>
    public string? ExpectedRigIdentity { get; init; }

    /// <summary>The rig identity the region map declares.</summary>
    public string? ActualRigIdentity { get; init; }

    /// <summary>The number of skinned material parts the body has.</summary>
    public int? ExpectedPartCount { get; init; }

    /// <summary>The number of parts the region map tags.</summary>
    public int? ActualPartCount { get; init; }

    /// <summary>The zero-based body material part whose triangles differ.</summary>
    public int? BodyPartIndex { get; init; }

    /// <summary>The triangle count of the uploaded mesh.</summary>
    public int? ExpectedTriangleCount { get; init; }

    /// <summary>The triangle count the region runs cover.</summary>
    public int? ActualTriangleCount { get; init; }
}
