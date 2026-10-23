namespace KhaozEngine.TileWorld;

/// <summary>One frame's roof rule, shared by ordinary roofs and retained prop layers.</summary>
internal readonly record struct TileRoofVisibilityRule(RoofVisibility Mode, bool Indoors, int ObserverPlane,
    TileInteriorFill Interior)
{
    public bool AnyHiddenOn(int plane) => Mode switch
    {
        RoofVisibility.AlwaysVisible => false,
        RoofVisibility.AlwaysHidden => true,
        _ => Indoors && plane > ObserverPlane && Interior.Count > 0,
    };

    public bool IsHidden(TileRect footprint, int plane) =>
        AnyHiddenOn(plane) && (Mode == RoofVisibility.AlwaysHidden || Interior.Intersects(footprint));
}
