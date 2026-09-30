namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The physical glide direction until the displayed body lands, then its authoritative facing.</summary>
internal static class TilePresentationFacing
{
    public static TileDirection Direction(in TileMoveState state, float stepFraction) =>
        state.IsStepping && stepFraction < 1f
            ? TileRoute.Direction(state.StepFrom, state.Tile)
            : state.Facing;
}
