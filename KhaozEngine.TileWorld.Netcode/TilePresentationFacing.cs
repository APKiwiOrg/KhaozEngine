using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The physical glide direction until the displayed body lands, then its authoritative facing.</summary>
internal static class TilePresentationFacing
{
    public static TileDirection Direction(in TileMoveState state, float stepFraction) =>
        state.IsStepping && stepFraction < 1f
            ? TileRoute.Direction(state.StepFrom, state.Tile)
            : state.Facing;

    // Prediction can normalize StepFrom before the final inter-tick segment reaches its endpoint. That segment
    // keeps the departed step's direction. Correction offsets never count as a commanded step or delay aim.
    public static (TileDirection Direction, bool Landed) Local(in TileMoveState state, Vector2 remainingMovement)
    {
        bool landed = TilePresenter.StepFraction(state) >= 1f && remainingMovement == Vector2.Zero;
        if (landed) return (state.Facing, true);
        if (state.IsStepping) return (Direction(state, 0f), false);
        return (TileRoute.Direction(default,
            new TileCoord(Math.Sign(remainingMovement.X), Math.Sign(remainingMovement.Y), 0)), false);
    }
}
