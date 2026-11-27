using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Test the real skins and rim over the same support motions used by the material solver.
internal sealed class ClothDetailedSweep(BoundClothSurface profile, ClothWorkLimits limits)
{
    private readonly Vector3[] _before = new Vector3[profile.Description.SourceVertexCount];
    private readonly Vector3[] _after = new Vector3[profile.Description.SourceVertexCount];
    private readonly ClothBounds[] _faceBounds = new ClothBounds[profile.Description.SourceTriangleCount];
    private readonly ClothBounds[] _nodeBounds = new ClothBounds[ClothDetailedTopology.NodeCapacity(profile.Description.SourceTriangleCount)];
    private readonly ClothContactWorkspace _work = new(limits);

    internal ClothStepResult Verify(BoundClothSurface surface, ModelVertex[][] opening, in Matrix4x4 openingModel,
        ModelVertex[][] closing, in Matrix4x4 closingModel, in ClothSubstep pose, ClothStepResult prior = default)
    {
        _work.Begin(prior.ContactChecks, prior.ContactCandidates);
        ClothStepStatus status = _work.Prepare(pose, closingOnly: false, allowCoarse: false);
        if (status == ClothStepStatus.Accepted)
        {
            for (int material = 0; material < surface.Materials.Length; material++)
            {
                ClothDetailedTopology tree = surface.Materials[material].DetailedTopology!;
                if (tree.Nodes.IsEmpty) continue;
                for (int i = 0; i < opening[material].Length; i++)
                {
                    _before[i] = Vector3.Transform(opening[material][i].Position, openingModel);
                    _after[i] = Vector3.Transform(closing[material][i].Position, closingModel);
                    if (!ChainMath.IsFinite(_before[i]) || !ChainMath.IsFinite(_after[i]))
                    {
                        status = ClothStepStatus.NonfiniteState;
                        break;
                    }
                }
                if (status != ClothStepStatus.Accepted) break;
                tree.Refit(_before, _after, _faceBounds, _nodeBounds);
                status = CheckView(tree, pose);
                if (status != ClothStepStatus.Accepted) break;
            }
        }
        return new ClothStepResult(status, 0, _work.Candidates - prior.ContactCandidates, _work.Checks - prior.ContactChecks, 0)
        {
            PartId = status == ClothStepStatus.Accepted ? null : _work.PartId,
            SweepRefinements = _work.SweepRefinements,
        };
    }

    private ClothStepStatus CheckView(ClothDetailedTopology tree, in ClothSubstep pose)
    {
        for (int i = 0; i < pose.ClosingSupports.Length; i++)
        {
            ClothSupport last = pose.ClosingSupports[i];
            if (_work.Replaced(last)) continue;
            _work.PartId = last.Identity == default ? null : last.Identity.PartId;
            var pair = new ClothCapsuleSweep(pose.OpeningSupports[i], last, pose.HeldPaths);
            ClothStepStatus status = CheckFacets(tree, pair.Bounds, pair, null);
            if (status != ClothStepStatus.Accepted) return status;
        }
        foreach (ClothPosedHull hull in _work.Hulls)
        {
            _work.PartId = hull.Identity.PartId;
            ClothStepStatus status = CheckFacets(tree, hull.SweptBounds, default, hull);
            if (status != ClothStepStatus.Accepted) return status;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus CheckFacets(ClothDetailedTopology tree, in ClothBounds support,
        in ClothCapsuleSweep capsule, ClothPosedHull? hull)
    {
        ReadOnlySpan<ClothDetailedTopology.Node> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Length;)
        {
            if (!_work.Visit()) return ClothStepStatus.ContactWork;
            ClothDetailedTopology.Node node = nodes[i];
            if (!_nodeBounds[i].Overlaps(support))
            {
                i = node.Skip;
                continue;
            }
            for (int faceIndex = node.First; faceIndex < node.First + node.Count; faceIndex++)
            {
                if (!_work.Visit()) return ClothStepStatus.ContactWork;
                if (!_faceBounds[faceIndex].Overlaps(support)) continue;
                if (!_work.Candidate()) return ClothStepStatus.ContactCapacity;
                ClothTriangle face = tree.Faces[faceIndex];
                var first = new ClothFacet(face, _before);
                var last = new ClothFacet(face, _after);
                ClothStepStatus status = hull is { } detailed
                    ? _work.CheckSweep(new ClothHullSweep(first, last, detailed), 0, out _, requireClearOpening: true)
                    : _work.CheckSweep(capsule.WithFacets(first, last), 0, out _, requireClearOpening: true);
                if (status != ClothStepStatus.Accepted) return status;
            }
            i++;
        }
        return ClothStepStatus.Accepted;
    }
}
