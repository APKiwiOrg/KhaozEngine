namespace KhaozEngine.Terrain;

/// <summary>Optional draw-time predicate for a retained prop batch. The layer identity is null when its producer
/// did not assign one. A null filter on renderer APIs keeps every prop visible.</summary>
/// <param name="layerIdentity">Stable producer-assigned layer identity, or null.</param>
/// <param name="kitId">The placement kit identity.</param>
public delegate bool PropDrawFilter(string? layerIdentity, string kitId);
