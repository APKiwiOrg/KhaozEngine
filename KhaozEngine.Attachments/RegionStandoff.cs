namespace KhaozEngine.Attachments;

/// <summary>The authored height of a worn set's outer surface above the bare skin in one region.</summary>
/// <param name="Region">The ordinal region name.</param>
/// <param name="Metres">The rest-pose height in metres, between zero and 0.25 inclusive.</param>
public readonly record struct RegionStandoff(string Region, float Metres);
