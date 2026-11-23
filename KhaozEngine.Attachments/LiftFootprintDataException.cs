using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of lift footprint data.</summary>
public sealed class LiftFootprintDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public LiftFootprintDataException(LiftFootprintRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public LiftFootprintDataException(LiftFootprintRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the data was refused.</summary>
    public LiftFootprintRefusal Reason { get; }

    /// <summary>The caller-supplied name added at the public file boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The zero-based file offset of the refused or incomplete field, or the table count for a missing normal.</summary>
    public int? ByteOffset { get; init; }

    /// <summary>The format version declared by the file when refused.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>The grid level declared by the file when refused.</summary>
    public int? Level { get; init; }

    /// <summary>The ID of the part containing the refusal, when available.</summary>
    public uint? PartId { get; init; }

    /// <summary>The zero-based position of the part containing the refusal.</summary>
    public int? PartIndex { get; init; }

    /// <summary>The zero-based position of the triangle within its part.</summary>
    public int? TriangleIndex { get; init; }

    /// <summary>The zero-based position of the cell or height within its triangle.</summary>
    public int? EntryIndex { get; init; }

    /// <summary>The offending entry's cell key, widened to include out-of-range decoded deltas.</summary>
    public long? CellKey { get; init; }

    /// <summary>The declared body triangle count when involved in a refusal.</summary>
    public long? BodyTriangleCount { get; init; }

    /// <summary>The refused height in file units of 0.01 mm.</summary>
    public int? HeightUnits { get; init; }

    /// <summary>The refused measured height in metres.</summary>
    public double? HeightMetres { get; init; }

    /// <summary>The refused body region map digest.</summary>
    public string? BodyRegionMapSha256 { get; init; }

    /// <summary>The flattened body triangle index involved in a face normal refusal.</summary>
    public long? BodyTriangleIndex { get; init; }

    /// <summary>The zero-based position of the refused face normal.</summary>
    public int? NormalIndex { get; init; }

    /// <summary>The declared face normal count when it cannot fit in the remaining bytes.</summary>
    public uint? NormalCount { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) =>
        new LiftFootprintDataException(Reason, $"{sourceName}: {Message}", this)
        {
            SourceName = sourceName,
            ByteOffset = ByteOffset,
            FormatVersion = FormatVersion,
            Level = Level,
            PartId = PartId,
            PartIndex = PartIndex,
            TriangleIndex = TriangleIndex,
            EntryIndex = EntryIndex,
            CellKey = CellKey,
            BodyTriangleCount = BodyTriangleCount,
            HeightUnits = HeightUnits,
            HeightMetres = HeightMetres,
            BodyRegionMapSha256 = BodyRegionMapSha256,
            BodyTriangleIndex = BodyTriangleIndex,
            NormalIndex = NormalIndex,
            NormalCount = NormalCount,
        };
}
