namespace KhaozEngine.Attachments;

/// <summary>Declares that a skinned attachment part lifts clear of layers beneath its set.</summary>
/// <param name="ClearanceMetres">The gap to preserve in metres, between zero and 0.05 inclusive.</param>
public sealed record PartLiftDefinition(float ClearanceMetres);
