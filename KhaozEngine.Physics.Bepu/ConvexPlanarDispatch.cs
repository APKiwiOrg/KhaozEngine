namespace KhaozEngine.Physics.Bepu;

/// <summary>One planar dispatch owns its raw identity before invoking the geometry-only kernel.
/// There is no door that accepts a bare kernel certificate for attachment to another source.</summary>
internal sealed class ConvexPlanarDispatch(InstalledSweepLeaf leaf, ConvexContactIdentity identity)
{
    internal readonly struct Evidence
    {
        internal ConvexPlanarDispatch? Owner { get; }
        internal ConvexLeafEvidence Local { get; }
        Evidence(ConvexPlanarDispatch owner, ConvexLeafEvidence local) { Owner = owner; Local = local; }

        internal static bool TryEvaluate(ConvexPlanarDispatch owner, in ConvexQueryRequest request,
            ref ConvexQueryWork work, ConvexQueryScratch scratch, out Evidence evidence)
        {
            evidence = default;
            if (!ConvexFeatureKernel.TryEvaluate(request, owner.Leaf, ref work, scratch, out var local)) return false;
            evidence = new(owner, local);
            return true;
        }
    }

    InstalledSweepLeaf Leaf { get; } = leaf;
    internal bool TryEvaluate(in ConvexQueryRequest request, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out Evidence evidence) => Evidence.TryEvaluate(this, request, ref work, scratch, out evidence);

    internal bool TryPublish(in Evidence evidence, out ConvexDistanceResult result)
    {
        result = default;
        if (!ReferenceEquals(evidence.Owner, this) || !evidence.Local.IsComplete) return false;
        var local = evidence.Local;
        result = new(true, true, local.LowerDistance, local.UpperDistance, local.QueryWitness,
            local.TargetWitness, local.Normal, identity);
        return true;
    }
}
