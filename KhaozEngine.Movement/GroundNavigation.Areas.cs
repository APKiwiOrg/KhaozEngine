using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigation
{
    internal GroundNavigation? AreaExclusionSource { get; }

    /// <summary>Creates an immutable query view that additionally refuses these captured area bits.
    /// The baked graph and captured columns are shared. Zero returns this instance. Views accumulate exclusions
    /// and never widen the baked profile, including when an excluded bit is required by that profile.
    /// Terrain ownership and preparation follow the source, including later evidence rebinding.
    /// Search nodes, edges and supported-walk segments check the complete captured footprint.
    /// Terrain links also check excluded areas across their full certified shell.</summary>
    public GroundNavigation ExcludingAreas(uint excludedAreas)
        => excludedAreas == 0 ? this : new GroundNavigation(this, excludedAreas);

    private GroundNavigation(GroundNavigation source, uint excludedAreas)
    {
        Graph = source.Graph;
        _tuning = source._tuning;
        _swim = source._swim;
        _footprint = source._footprint.ExcludingAreas(excludedAreas);
        Aquatic = source.Aquatic;
        MaxDropMetres = source.MaxDropMetres;
        AllowTerrainDescents = source.AllowTerrainDescents;
        Identity = source.Identity;
        AreaExclusionSource = source.AreaExclusionSource ?? source;
        Planner = new ProfilePlanner(this, new GridPathPlanner(Graph.Space, Graph,
            link => ContactTerrainAreaExclusions.Allows(this, link)));
    }
}
