using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    /// <summary>The current renderable set: local player predicted, remotes from the replicated position (smoothly
    /// interpolated between the last two snapshots when <see cref="WorldClientConfig.InterpolateRemotes"/> is set -
    /// the default - else the raw latest). Each entry also carries the exact grounded flag + vertical velocity
    /// (local: predicted; remote: replicated <see cref="MovementState"/>) so an animator bridge reads true air state
    /// instead of finite-differencing the terrain-following position. Each call returns an independent list
    /// the caller can keep. Use <see cref="CopySnapshotTo"/> to fill an existing list.</summary>
    public IReadOnlyList<EntityRenderState> Snapshot()
    {
        var list = new List<EntityRenderState>();
        CopySnapshotTo(list);
        return list;
    }

    /// <summary>Clears and fills the caller's list with the same contents and order as <see cref="Snapshot"/>.
    /// The client keeps no reference to the list. Its capacity can be reused across presentation updates.</summary>
    public void CopySnapshotTo(List<EntityRenderState> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        var entities = view.EnumerateEntities();
        while (entities.MoveNext())
        {
            KeyValuePair<long, Entity> kv = entities.Current;
            if (!world.IsAlive(kv.Value)) continue;
            bool isLocal = kv.Key == LocalNetId;
            if (isLocal && !seededPredictionOnce && deferredMovementBasis.HasValue) continue;
            Vector3 pos;
            bool grounded;
            float verticalVelocity;
            bool swimming;
            WaterExcursionState waterExcursion;
            float climbRate;
            float facingYaw;
            // Local-only signals: a remote receives neither (see EntityRenderState).
            float stepCumulativeY = 0f;
            float landingImpact = 0f;
            if (isLocal)
            {
                // The local avatar's exact movement state is the predicted/reconciled state, converted to ABSOLUTE
                // world metres like every other entry in this list (see LocalRenderState for why that rule exists).
                PlayerMoveState rs = LocalRenderState;
                pos = rs.Position;
                grounded = rs.Grounded;
                verticalVelocity = rs.VerticalVelocity;
                swimming = rs.Swimming;
                waterExcursion = rs.Move.WaterExcursion;
                // Local: the predicted publication, before optional render interpolation.
                climbRate = rs.Move.ClimbRate;
                landingImpact = rs.Move.LandingImpactSpeed;
                facingYaw = rs.Move.FacingYaw;
                // The local step-smoothing accumulator (rides no wire): a mesh smoother diffs it to ease isolated steps
                // the continuous glide renders raw. Read from the predictor (not the rendered state, whose Move.StepDeltaY
                // is a per-tick event, not the running sum). Remotes leave it 0 (their singles ride position interpolation).
                stepCumulativeY = prediction.StepCumulativeY;
            }
            else
            {
                // Remotes: surface the replicated vertical state (MovementState rides alongside the position) so a
                // consumer animates jump/fall/swim from the authoritative flags instead of finite-differencing the
                // terrain-following position (which reads "airborne" the faster a remote moves over a slope, and
                // cannot tell a swimmer from a walker at all). Default grounded / not-swimming when a remote has no
                // MovementState yet, so it never spuriously starts airborne or swimming. MovementState is discrete
                // nearest-sampled to renderTime (E2), so its flags + climb rate ride the SAME delayed timeline as the
                // interpolated position - no flag/position skew.
                world.TryGet(kv.Value, out ReplicatedPosition rp);
                pos = rp.Value;
                bool hasMs = world.TryGet(kv.Value, out MovementState ms);
                grounded = hasMs ? ms.Grounded : true;
                verticalVelocity = ms.VerticalVelocity;
                swimming = hasMs && ms.Swimming;
                waterExcursion = ms.WaterExcursion;
                climbRate = hasMs ? MovementState.DecodeClimbRate(ms.ClimbRateQ) : 0f;
                facingYaw = hasMs ? MovementState.DecodeFacingYaw(ms.FacingYawQ) : 0f;
            }
            string? name = world.TryGet(kv.Value, out PlayerIdentity identity) ? identity.DisplayName : null;
            destination.Add(new EntityRenderState(new NetId(kv.Key), pos, isLocal, name, grounded, verticalVelocity,
                swimming, climbRate, stepCumulativeY, landingImpact, facingYaw, waterExcursion));
        }
    }
}
