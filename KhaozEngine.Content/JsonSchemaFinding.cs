namespace KhaozEngine.Content;

/// <summary>A schema evaluation failure with engine-owned, immutable location data.</summary>
/// <param name="InstanceLocation">The instance location as a JSON Pointer. Empty denotes the root.</param>
/// <param name="Keyword">The failing schema keyword, when known. Null for a Boolean schema without a known owning keyword.</param>
/// <param name="PropertyName">The decoded property name when the instance location addresses an object
/// property. Null for the root or an array element. A missing required property is not inferred from prose.</param>
public sealed record JsonSchemaFinding(string InstanceLocation, string? Keyword, string? PropertyName);
