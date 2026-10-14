namespace KhaozEngine.MapDoc.Interiors;

/// <summary>A payload-1 storey identity, owned by an instance or by standalone building intent.</summary>
public sealed record MapStoreyDoc(int PayloadVersion, string Id, string Name, string? InstanceId);
