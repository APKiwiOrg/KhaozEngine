namespace KhaozEngine.Physics.Bepu;

/// <summary>One planar dispatch owns its raw identity before invoking the geometry-only kernel.
/// There is no door that accepts a bare kernel certificate for attachment to another source.</summary>
internal sealed class ConvexPlanarDispatch(InstalledSweepLeaf leaf, ConvexContactIdentity identity)
{
    internal readonly struct Evidence
    {
        internal ConvexPlanarDispatch? Owner { get; }
        internal ConvexLeafEvidence Local { get; }
        internal ConvexQueryOperation Operation { get; }
        Evidence(ConvexPlanarDispatch owner, ConvexLeafEvidence local, ConvexQueryOperation operation)
        { Owner = owner; Local = local; Operation = operation; }

        internal static bool TryEvaluate(ConvexPlanarDispatch owner, in ConvexQueryRequest request,
            ref ConvexQueryWork work, ConvexQueryScratch scratch, out Evidence evidence)
        {
            evidence = default;
            if (!ConvexFeatureKernel.TryEvaluate(request, owner.Leaf, ref work, scratch, out var local)) return false;
            evidence = new(owner, local, request.Operation);
            return true;
        }
    }

    InstalledSweepLeaf Leaf { get; } = leaf;

    internal readonly struct InitialEvidence
    {
        internal ConvexPlanarDispatch? Owner { get; }
        internal ConvexInitialEvidence Local { get; }
        internal ConvexQueryOperation Operation { get; }
        InitialEvidence(ConvexPlanarDispatch owner, ConvexInitialEvidence local)
        { Owner = owner; Local = local; Operation = local.Pair.Request.Operation; }

        internal static bool TryEvaluate(ConvexPlanarDispatch owner, in ConvexQueryRequest request,
            ConvexCertificateFraction fraction, ref ConvexQueryWork work, ConvexQueryScratch scratch, out InitialEvidence evidence)
        {
            evidence = default;
            if (!ConvexPlanarContacts.TryClassify(request, owner.Leaf, fraction, ref work, scratch, out var local)) return false;
            evidence = new(owner, local);
            return true;
        }
    }

    internal bool TryClassifyInitial(in ConvexQueryRequest request, ConvexCertificateFraction fraction,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out InitialEvidence evidence) =>
        InitialEvidence.TryEvaluate(this, request, fraction, ref work, scratch, out evidence);

    internal bool TryPublishInitial(in InitialEvidence evidence, out ConvexInitialEvidence relation, out ConvexContactIdentity rawIdentity)
    {
        relation = default; rawIdentity = default;
        if (!ReferenceEquals(evidence.Owner, this) || !evidence.Local.IsAlive || evidence.Operation != evidence.Local.Pair.Request.Operation) return false;
        relation = evidence.Local; rawIdentity = identity; return true;
    }

    internal bool TryEvaluate(in ConvexQueryRequest request, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out Evidence evidence) => Evidence.TryEvaluate(this, request, ref work, scratch, out evidence);

    internal bool TryPublish(in Evidence evidence, out ConvexDistanceResult result)
    {
        result = default;
        if (!ReferenceEquals(evidence.Owner, this) || evidence.Operation != ConvexQueryOperation.Distance || !evidence.Local.IsComplete) return false;
        var local = evidence.Local;
        result = new(true, true, local.LowerDistance, local.UpperDistance, local.QueryWitness,
            local.TargetWitness, local.Normal, identity);
        return true;
    }

    internal bool TryPublishSweep(in Evidence evidence, out ConvexSweepResult result)
    {
        result = default;
        if (!ReferenceEquals(evidence.Owner, this) || evidence.Operation != ConvexQueryOperation.Sweep || !evidence.Local.IsComplete) return false;
        var local = evidence.Local;
        if (local.SweepStatus == ConvexSweepStatus.Clear)
        {
            result = new(ConvexSweepStatus.Clear, local.LowerDistance, null, 0, null, default);
            return true;
        }
        if (local.SweepStatus != ConvexSweepStatus.Hit) return false;
        SweepHit hit = new(local.UpperDistance, local.TargetWitness, local.Normal,
            identity.Dynamic ? null : new StaticHandle(identity.BodyHandle));
        result = new(ConvexSweepStatus.Hit, local.LowerDistance, local.UpperDistance,
            local.CertifiedErrorMetres, hit, identity);
        return true;
    }
}
