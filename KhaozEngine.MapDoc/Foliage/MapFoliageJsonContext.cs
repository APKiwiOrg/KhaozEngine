namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Tracks the typed location separately from the codec's developer-facing label.</summary>
internal readonly record struct MapFoliageJsonContext(string Label, string? FieldPath = null,
    string? LayerId = null, int? Index = null, MapFoliageChunkKey? FoliageChunk = null)
{
    internal MapFoliageJsonContext Property(string name) => this with
    {
        Label = Label + " " + name,
        FieldPath = FieldPath is null ? null : MapDocumentJsonPath.AppendProperty(FieldPath, name),
    };

    internal MapFoliageJsonContext Item(string name, int index) => Property(name) with
    {
        Label = $"{Label} {name}[{index}]",
        FieldPath = FieldPath is null ? null : MapDocumentJsonPath.AppendProperty(FieldPath, name) + $"[{index}]",
        Index = index,
    };

    internal MapDocumentException Refuse(MapDocumentRefusal reason, string message,
        long? actualByteCount = null, long? expectedByteCount = null, int? actualVersion = null,
        double? actualNumber = null, long? actualInteger = null, long? minimumInteger = null,
        long? maximumInteger = null, string? actualText = null, System.Type? enumType = null,
        string? budget = null, long? actualCount = null, long? limit = null) => new(reason, message)
    {
        FieldPath = FieldPath, LayerId = LayerId, Index = Index, FoliageChunk = FoliageChunk,
        ActualByteCount = actualByteCount, ExpectedByteCount = expectedByteCount,
        ActualVersion = actualVersion, SupportedVersions = actualVersion is null ? null : [1],
        ActualNumber = actualNumber, ActualInteger = actualInteger,
        MinimumInteger = minimumInteger, MaximumInteger = maximumInteger,
        ActualText = actualText, EnumType = enumType, Budget = budget, ActualCount = actualCount, Limit = limit,
    };

    public override string ToString() => Label;
}
