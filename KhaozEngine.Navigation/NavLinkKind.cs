namespace KhaozEngine.Navigation;

/// <summary>
/// What kind of transition a <see cref="NavLink"/> is, so the planner and follower can treat a same-grid
/// vertical hop differently from an ordinary walked link. New values may be added over time.
/// </summary>
public enum NavLinkKind
{
    /// <summary>The default: a link crossed by ordinary ground steering between its two endpoints, such as
    /// the directed stair connections a climber walks between dungeon floors. The follower steers across it
    /// with no special state.</summary>
    Stair,

    /// <summary>A same-grid vertical hop: two standable cells whose rise exceeds the step budget but stays
    /// within a jump budget, joined across a blocked rim. Its landing waypoint is marked <see cref="NavWaypointKind.Hop"/>
    /// and the follower surfaces <see cref="PathFollowState.Hopping"/>.</summary>
    Hop,
}
