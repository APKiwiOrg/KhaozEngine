namespace KhaozEngine.Terrain
{
    /// <summary>Stable identity for one prop layer in one streamed world area.</summary>
    public readonly record struct PropClusterKey(string LayerId, int X, int Z, int Plane);
}
