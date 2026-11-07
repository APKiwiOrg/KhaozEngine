namespace KhaozEngine.TileWorld;

/// <summary>A single semantic steep-companion finding, shared by prose reporting and structured refusal.</summary>
internal sealed record GroundMaterialSteepProblem(TileWorldRefusal Reason, string Message)
{
    public ushort? CompanionMaterialId { get; init; }
    public string? CompanionMaterialName { get; init; }
    public string? Field { get; init; }
    public float? FromDegrees { get; init; }
    public float? ToDegrees { get; init; }
    public bool? FromIsDefault { get; init; }
    public bool? ToIsDefault { get; init; }
}
