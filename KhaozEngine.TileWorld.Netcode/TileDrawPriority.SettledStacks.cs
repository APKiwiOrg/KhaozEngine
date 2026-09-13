using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileDrawPriority
{
    /// <summary>The presentation rule applied by the next rebuild. The default is
    /// <see cref="TileDrawPriorityPolicy.OneBodyPerTile"/>, which preserves the original behavior.</summary>
    public TileDrawPriorityPolicy Policy { get; set; }

    /// <summary>
    /// Compares two settled non-local actors competing for one tile when <see cref="Policy"/> is
    /// <see cref="TileDrawPriorityPolicy.SettledStacksOnly"/>. A positive answer means the first actor wins and a
    /// negative answer means the second wins. Zero falls back to the higher net id, so equal game ranks remain
    /// stable. Null uses that net-id fallback for every pair.
    /// <para>The callback receives net ids only. Actor kinds, combat state, level and every other game-specific
    /// classification remain the caller's data and policy.</para>
    /// </summary>
    public Comparison<long>? SettledComparison { get; set; }

    /// <summary>
    /// Rebuilds from a caller's presentation roster with an explicit local-motion answer. This is the custom-roster
    /// door for <see cref="TileDrawPriorityPolicy.SettledStacksOnly"/>. Every remote whose presented step progress is
    /// below one is moving. Finite values are clamped to zero through one, and non-finite values are read as one.
    /// <para>When the current policy is <see cref="TileDrawPriorityPolicy.OneBodyPerTile"/>, this overload applies
    /// that policy. Both values of <paramref name="localMoving"/> preserve the local-tile claim because this overload
    /// has no departure tile. A caller that needs the exact leaving-tile claim uses the established overload.</para>
    /// </summary>
    /// <param name="localNetId">The local player's net id, or <see cref="NoLocalPlayer"/>.</param>
    /// <param name="localTile">The local player's committed tile.</param>
    /// <param name="localMoving">True while the local body is moving on the presentation timeline.</param>
    /// <param name="others">Every other actor, its committed tile, and its presented step progress.</param>
    /// <param name="dt">Seconds since the last rebuild. Ignored by the settled-stack policy because all of its
    /// weights are binary.</param>
    public void Rebuild(long localNetId, TileCoord localTile, bool localMoving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others, float dt)
    {
        if (Policy == TileDrawPriorityPolicy.SettledStacksOnly)
        {
            RebuildSettled(localNetId, localTile, localMoving, others);
            return;
        }
        Rebuild(localNetId, localTile, localMoving ? localTile : null, others, dt, snap: false);
    }

    void RebuildSettled(TileWorldClient client, in TileMoveState local,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others)
    {
        TileMoveState rendered = client.Prediction.RenderedState;
        Vector2 position = rendered.HasRenderOverride ? rendered.RenderPosition : rendered.Position;
        bool localMoving = local.IsStepping || position != new Vector2(local.Tile.X, local.Tile.Z);
        RebuildSettled(client.LocalNetId, local.Tile, localMoving, others);
    }

    void RebuildSettled(long localNetId, TileCoord localTile, bool localMoving,
        ReadOnlySpan<(long NetId, TileCoord Tile)> others)
    {
        Begin(winners, drawn);
        for (int i = 0; i < others.Length; i++)
            OfferSettled(localNetId, others[i].NetId, others[i].Tile, moving: false);
        FinishSettled(localNetId, localTile, localMoving);
        Advance(localNetId, others, dt: 0f, snap: true);
    }

    void RebuildSettled(long localNetId, TileCoord localTile, bool localMoving,
        ReadOnlySpan<(long NetId, TileCoord Tile, float StepProgress)> others)
    {
        Begin(winners, drawn);
        for (int i = 0; i < others.Length; i++)
        {
            float progress = float.IsFinite(others[i].StepProgress)
                ? Math.Clamp(others[i].StepProgress, 0f, 1f)
                : 1f;
            OfferSettled(localNetId, others[i].NetId, others[i].Tile, progress < 1f);
        }
        FinishSettled(localNetId, localTile, localMoving);
        Advance(localNetId, others, dt: 0f, snap: true);
    }

    void OfferSettled(long localNetId, long netId, TileCoord tile, bool moving)
    {
        if (netId == localNetId) return;
        if (moving)
        {
            drawn.Add(netId);
            return;
        }

        if (!winners.TryGetValue(tile, out long best) || Prefers(netId, best)) winners[tile] = netId;
    }

    bool Prefers(long first, long second)
    {
        int compared = SettledComparison?.Invoke(first, second) ?? 0;
        return compared > 0 || (compared == 0 && first > second);
    }

    void FinishSettled(long localNetId, TileCoord localTile, bool localMoving)
    {
        if (localNetId != NoLocalPlayer)
        {
            drawn.Add(localNetId);
            if (!localMoving) winners[localTile] = localNetId;
        }
        foreach (long netId in winners.Values) drawn.Add(netId);
    }
}
