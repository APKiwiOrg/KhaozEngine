namespace KhaozEngine.Physics;

/// <summary>Which body mobilities a raycast / sweep / overlap query is allowed to hit. Statics are the
/// terrain, props, and buildings registered via <see cref="IPhysicsWorld.AddStatic"/>; dynamics are the rigid
/// bodies from <see cref="IPhysicsWorld.AddDynamic"/> (including the infinite-mass kinematic ones). The default
/// (<see cref="All"/>) hits both, so a query that passes no explicit mode keeps the "hit everything" behaviour.</summary>
public enum QueryMobility
{
    /// <summary>Hit both static and dynamic bodies. This is the default so an unspecified query matches everything.</summary>
    All = 0,

    /// <summary>Hit only static bodies (terrain / props / buildings). A dynamic body between the query and a static
    /// is ignored, so a downward ground probe reads the terrain under a crate rather than the crate's top.</summary>
    Statics = 1,

    /// <summary>Hit only dynamic bodies. Statics are ignored.</summary>
    Dynamics = 2,
}

/// <summary>Filter for a physics query. <see cref="Mobility"/> selects which body mobilities the query may hit
/// (static / dynamic / both). <see cref="Layers"/> is a per-body layer mask (<c>0</c>, the default, matches every
/// layer). The <c>default</c> value (and <see cref="All"/>) matches every body, so a query that omits the filter
/// keeps the "hit everything" behaviour.</summary>
public readonly record struct QueryFilter(QueryMobility Mobility = QueryMobility.All, uint Layers = 0)
{
    /// <summary>Matches every body (both mobilities, all layers). The default filter.</summary>
    public static readonly QueryFilter All = default;

    /// <summary>Matches only static bodies (terrain / props / buildings), all layers.</summary>
    public static readonly QueryFilter StaticsOnly = new(QueryMobility.Statics);

    /// <summary>Matches only dynamic bodies, all layers.</summary>
    public static readonly QueryFilter DynamicsOnly = new(QueryMobility.Dynamics);
}
