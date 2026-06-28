namespace KhaozEngine.Physics;

/// <summary>Layer mask for queries. <c>Layers == 0</c> (the default) matches every body.</summary>
public readonly record struct QueryFilter(uint Layers)
{
    /// <summary>Matches all layers.</summary>
    public static readonly QueryFilter All = default;
}
