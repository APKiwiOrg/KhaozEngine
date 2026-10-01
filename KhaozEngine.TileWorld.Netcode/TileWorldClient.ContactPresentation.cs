using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldClient
{
    TileCombatContactPresentationSettings? combatContactPresentation;
    long acceptedMovementServerTick = -1;

    void InitializeContactPresentation(TileWorldClientConfig options)
    {
        combatContactPresentation = options.CombatContactPresentation;
        if (combatContactPresentation is not { } settings) return;
        if (!options.CombatPreparationEnabled)
            throw new ArgumentException("CombatContactPresentation requires CombatPreparationEnabled.", nameof(options));
        settings.Validate();
    }

    /// <summary>Reads a local or remote combat body without advancing presentation. Disabled contact returns
    /// the exact raw pose and zero velocities. False for unjoined, unknown or removed bodies.</summary>
    public bool TryGetCombatBodyPresentation(long netId, out TileCombatBodyPresentation presentation)
    {
        presentation = default;
        if (!IsJoined || acceptedMovementServerTick < 0) return false;
        TilePose pose;
        if (netId == LocalNetId)
        {
            if (!seeded || !View.TryGetEntity(netId, out var local) || !World.TryGet(local, out TileMoveState _))
                return false;
            pose = LocalPose;
        }
        else if (!latestTiles.ContainsKey(netId) || !TryGetRemotePose(netId, out pose)) return false;
        presentation = new TileCombatBodyPresentation(pose, Vector3.Zero, Vector3.Zero, Vector3.Zero,
            acceptedMovementServerTick, false, TileCombatContactLimits.None);
        return true;
    }

    /// <summary>Owned contact diagnostics from the most recent completed presentation frame.</summary>
    public IReadOnlyList<TileCombatContactImpact> CombatContactImpacts => Array.Empty<TileCombatContactImpact>();

    /// <summary>The cumulative number of measured contact misses. Disabled contact reports zero.</summary>
    public long CombatContactMissCount => 0;

    /// <summary>Transfers a combat body controller to a visible presentation successor when one is available.</summary>
    public bool TryTransferCombatBodyPresentation(long previousNetId, long successorNetId) => false;

    void ClearContactPresentation()
    {
        combatContactPresentation = null;
        acceptedMovementServerTick = -1;
    }
}
