using System;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;
using S = KhaozEngine.Render3D.CoverageEnclosureSupport;

namespace KhaozEngine.Render3D;

/// <summary>Immutable interval faces, group maxima, full-target guards and scalar transition.</summary>
internal static class CoverageEnclosureCorrection
{
    internal static bool Apply(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        Span<V> host = stackalloc V[3];
        for (int slot = 0; slot < reference.ActiveCommands; slot++)
        {
            var frame = reference.Frames[slot];
            var command = commands[frame.Command];
            var support = command.Support.ReferenceData;
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                var face = topology.Triangles[t];
                if (face.Role != CoverageTriangleRole.Core || face.RegionIndex != command.RegionIndex) continue;
                I discrepancy = F.Point(0);
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = (int)topology.Indices[t * 3 + corner];
                    V position = scratch.After[vertex];
                    V actual = M.Transform(position, frame.HostToActualSheet), nominal = M.Transform(position, frame.HostToCoverage);
                    bool a = S.Query(support, actual, out I ah, out bool ac), n = S.Query(support, nominal, out I nh, out bool nc);
                    if (a && !ah.IsFinite || n && !nh.IsFinite)
                    { scratch.Unbounded(CoverageEnclosureSite.QueryHeight, vertex, t); return false; }
                    if (!ac || !nc) scratch.MayRefuse = true;
                    host[corner] = nominal;
                    V delta = M.Subtract(actual, nominal);
                    I loss = F.Add(F.Abs(delta.Z), F.Multiply(F.Point(support.MaxSlope), M.Length2(delta.X, delta.Y)));
                    discrepancy = F.Max(discrepancy, loss);
                }
                I maximum = CoverageEnclosureOverlay.Maximum(support, host, out int sheet);
                if (!maximum.IsFinite) { scratch.Unbounded(CoverageEnclosureSite.OverlayHeight, t, sheet); return false; }
                I deficit = F.Add(F.Add(maximum, F.Point(frame.ActualClearanceCoverage)), discrepancy);
                I correction = F.Point(0);
                if (deficit.Max > 0)
                {
                    correction = S.Correction(new(Math.Max(0, deficit.Min), deficit.Max), frame.CorrectionRate);
                    if (deficit.Min <= 0) correction = F.Hull(correction, F.Point(0));
                    for (int c = 0; c < 3; c++)
                        if (topology.Vertices[(int)topology.Indices[t * 3 + c]].Strength != 1) scratch.MayRefuse = true;
                }
                if (!deficit.IsFinite || !correction.IsFinite) { scratch.Unbounded(CoverageEnclosureSite.Correction, t); return false; }
                scratch.Faces[t] = correction; scratch.FaceGuards[t] = true;
            }
        }
        for (int g = 0; g < scratch.Groups.Length; g++)
        {
            int v = scratch.Representatives[g];
            if (v < 0) continue;
            I correction = F.Point(0);
            for (int i = scratch.Rows.CoreFaceOffsets[g]; i < scratch.Rows.CoreFaceOffsets[g + 1]; i++)
                correction = F.Max(correction, scratch.Faces[scratch.Rows.CoreFaces[i]]);
            var record = topology.Vertices[v];
            var frame = reference.Frames[reference.RegionCommands[record.RegionIndex]];
            var command = commands[frame.Command];
            I total = scratch.Full[g];
            if (correction.Min != 0 || correction.Max != 0)
            {
                I added = F.Add(total, correction);
                total = correction.Contains(0) ? F.Hull(total, added) : added;
                V moved = CoverageEnclosurePose.Move(scratch.Before[v], total, frame.Recess);
                scratch.Groups[g] = correction.Contains(0) ? M.Hull(scratch.Groups[g], moved) : moved;
                if (Width(correction) > Width(scratch.Full[g])) scratch.GroupSites[g] = CoverageEnclosureSite.Correction;
            }
            scratch.Full[g] = total;
            V delta = M.Subtract(scratch.Groups[g], scratch.Before[v]);
            I distance = M.Length(M.Transform(delta, frame.ActorLinear, true));
            if (!distance.IsFinite || !total.IsFinite) { scratch.Unbounded(CoverageEnclosureSite.FullTarget, v); return false; }
            if (distance.Max > command.MaxWorldRecession) scratch.MayRefuse = true;
            CoverageEnclosurePose.Caps(M.Transform(scratch.Groups[g], frame.HostToCoverage), command, frame, scratch);
            if (command.TransitionWeight == 0) scratch.Groups[g] = scratch.Before[v];
            else if (command.TransitionWeight != 1)
                scratch.Groups[g] = CoverageEnclosurePose.Move(scratch.Before[v], F.Multiply(F.Point(command.TransitionWeight), total), frame.Recess);
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup;
            int representative = scratch.Representatives[g];
            if (representative < 0) continue;
            int slot = reference.RegionCommands[topology.Vertices[representative].RegionIndex];
            scratch.After[v] = commands[reference.Frames[slot].Command].TransitionWeight == 0 ? scratch.Before[v] : scratch.Groups[g];
        }
        return true;
    }

    static double Width(I x) => x.Max - x.Min;
}
