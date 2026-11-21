using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of body region map data.</summary>
public sealed class BodyRegionMapDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public BodyRegionMapDataException(BodyRegionMapRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public BodyRegionMapDataException(BodyRegionMapRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public BodyRegionMapRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public JSON boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The canonical JSON schema location of the refused value or containing object.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The unknown, repeated or missing property name.</summary>
    public string? PropertyName { get; init; }

    /// <summary>The refused format version when representable as an Int32.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The refused rig identity.</summary>
    public string? RigIdentity { get; init; }

    /// <summary>The refused or repeated region name.</summary>
    public string? RegionName { get; init; }

    /// <summary>The zero-based position of the refused region, or the region index a refused run names.</summary>
    public int? RegionIndex { get; init; }

    /// <summary>The number of regions the map declares.</summary>
    public int? RegionCount { get; init; }

    /// <summary>The zero-based index of the refused part.</summary>
    public int? PartIndex { get; init; }

    /// <summary>The zero-based index of the refused run within its part.</summary>
    public int? RunIndex { get; init; }

    /// <summary>The triangle count a part declares.</summary>
    public int? DeclaredTriangleCount { get; init; }

    /// <summary>The triangle count a part's runs add up to.</summary>
    public long? ActualTriangleCount { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new BodyRegionMapDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        JsonPath = JsonPath,
        PropertyName = PropertyName,
        FormatVersion = FormatVersion,
        RigIdentity = RigIdentity,
        RegionName = RegionName,
        RegionIndex = RegionIndex,
        RegionCount = RegionCount,
        PartIndex = PartIndex,
        RunIndex = RunIndex,
        DeclaredTriangleCount = DeclaredTriangleCount,
        ActualTriangleCount = ActualTriangleCount,
    };
}
