namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    readonly CapsuleSweepEvidence _sweepEvidence;

    // Called immediately after the existing EnterMutation increment. A skipped hook leaves a
    // generation gap, so this operation cannot carry stale records into a new valid generation.
    CapsuleSweepEvidence.Mutation BeginSweepMutation() => _sweepEvidence.BeginMutation(_queryGeneration);

    void CompleteSweepMutation(CapsuleSweepEvidence.Mutation token, int? changedStatic = null, bool refreshStatics = false)
    {
        if (!token.CanCarry)
        {
            _sweepEvidence.CompleteMutation(token, _queryGeneration, recordsComplete: false);
            return;
        }
        bool complete = true;
        if (refreshStatics)
        {
            foreach (int staticId in _handles.Keys)
                if (!ObserveStaticSweepBounds(staticId)) { complete = false; break; }
        }
        else if (changedStatic is int changedId) complete = ObserveStaticSweepBounds(changedId);
        // Static/constraint changes can wake bodies and transfer their broad-phase leaves too.
        // This audit is mutation work. Certified queries never rescan the registered world.
        if (complete)
        {
            foreach (int dynamicId in _dynamics.Keys)
                if (!ObserveDynamicSweepBounds(dynamicId)) { complete = false; break; }
        }
        complete = complete && SweepRecordCountsMatch();
        _sweepEvidence.CompleteMutation(token, _queryGeneration, complete);
    }
}
