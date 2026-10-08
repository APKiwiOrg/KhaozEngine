using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    readonly record struct DeferredMovementBasis(PlayerMoveState State, int AcknowledgedSequence, bool First);
    DeferredMovementBasis? deferredMovementBasis;

    bool RetryDeferredMovementBasis() => deferredMovementBasis is not { } pending ||
        ApplyMovementBasis(pending.State, pending.AcknowledgedSequence, pending.First);

    bool ApplyMovementBasis(in PlayerMoveState basis, int acknowledgedSequence, bool first)
    {
        ReconciliationResult reconciliation;
        using (var read = simulator.BeginExplicitRead(basis))
        {
            if (read?.BasisValid == false)
            {
                // Retain the latest authority as unclassified input. Do not publish it, replay from
                // a discarded future pose, or manufacture a default-origin avatar while data loads.
                bool initial = first || deferredMovementBasis?.First == true;
                deferredMovementBasis = new(basis, acknowledgedSequence, initial);
                return false;
            }
            first |= deferredMovementBasis?.First == true;
            deferredMovementBasis = null;
            if (first)
            {
                if (seededPredictionOnce) prediction.Reseed(basis);
                else { prediction.Reset(basis); seededPredictionOnce = true; }
            }
            reconciliation = prediction.Reconcile(authoritativeTick++, basis, acknowledgedSequence);
        }
        // Consumer notifications may move physics or dispose the client. The read is already closed.
        RecordCorrection(reconciliation.PositionError);
        lastReconcileError = reconciliation.PositionError;
        if (reconciliation.Teleported)
        {
            LocalTeleportEpoch++;
            LocalTeleported?.Invoke();
        }
        return true;
    }
}
