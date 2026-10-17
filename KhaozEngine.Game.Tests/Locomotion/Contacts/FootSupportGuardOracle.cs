using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Bounds = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The proposal refusals SG-6 keeps, decided on installed polygon geometry without the backend sweep. A
/// downward proposal refuses when its raised start lies inside geometry or when it stops above the reach band.
/// Each convex leaf is a solid and each mesh triangle a two-sided plate. Curved elements are outside the model,
/// so one that reaches above the band top refuses to judge.</summary>
internal static class FootSupportGuardOracle
{
    // Face vertices in binary64 lie within rounding of their exact values. The margin only widens a prefilter.
    const double Margin = 1e-6;

    internal static bool Refuses(SupportFootprintScene scene, in SupportFootprintRequest request)
    {
        foreach (OracleTangent part in scene.Curves)
            if (Top(part) + Margin > request.BandUpper)
                throw new NotSupportedException($"The guard oracle does not model curved element {part.ElementId} " +
                    $"of static {part.Static.Value} above the band top {request.BandUpper}.");
        return Refuses(scene, request.AxisProposal, request.BandUpper) ||
            Refuses(scene, request.FootProposal, request.BandUpper);
    }

    // Heights are the probe's lowest point. A solid refuses when its top contact is certainly above the band and the
    // probe touching its underside is not certainly above the start: the start lies inside it or the sweep stops on
    // it above the band. A solid wholly above the start is never reached by a downward sweep.
    static bool Refuses(SupportFootprintScene scene, SupportFootprintProposal proposal, double bandTop)
    {
        R radius = R.From(proposal.Probe.Radius), length = R.From(proposal.Probe.Length);
        R start = R.From(proposal.Start.Position.Y) - length / new R(2, 1) - radius;
        R top = R.From(bandTop);
        double probe = (double)proposal.Probe.Length + 2 * (double)proposal.Probe.Radius;
        double lowest = proposal.Start.Position.Y - probe / 2, reach = proposal.Probe.Radius + Margin;
        double x = proposal.Start.Position.X, z = proposal.Start.Position.Z;
        foreach (var solid in scene.Faces.GroupBy(face => (face.Static,
                     Leaf: scene.Meshes.Contains(face.Static) ? face.ElementId : face.ElementId / 256)))
        {
            // Necessary conditions before any exact work. A top contact lies at or below the highest vertex, the
            // probe touching the underside lies at or above the lowest vertex less the probe height, and a contact
            // needs the solid within the probe radius of its axis.
            IEnumerable<OracleVector> vertices = solid.SelectMany(face => face.Vertices);
            if (vertices.Max(vertex => vertex.Y) < bandTop - Margin ||
                vertices.Min(vertex => vertex.Y) - probe > lowest + Margin ||
                vertices.Min(vertex => vertex.X) > x + reach || vertices.Max(vertex => vertex.X) < x - reach ||
                vertices.Min(vertex => vertex.Z) > z + reach || vertices.Max(vertex => vertex.Z) < z - reach) continue;
            bool plate = scene.Meshes.Contains(solid.Key.Static);
            if (Highest(solid.Where(face => plate || face.Normal.Y > 0), proposal, mirror: false) is not { } above ||
                !(above.Lower > top)) continue;
            if (Highest(solid.Where(face => plate || face.Normal.Y < 0), proposal, mirror: true) is not { } below ||
                -below.Upper - (length + radius + radius) <= start) return true;
        }
        return false;
    }

    // The highest resting contact of the probe's lower cap on the faces, or of its upper cap on their underside when
    // mirrored. Each face is oriented upward first, so a mirrored downward face is read from above.
    static Bounds? Highest(IEnumerable<OracleFace> faces, SupportFootprintProposal proposal, bool mirror)
    {
        Bounds? highest = null;
        foreach (OracleFace face in faces)
        {
            V[] vertices = SupportFootprintExactOracle.Vertices(face);
            vertices = mirror ? Array.ConvertAll(vertices, vertex => new V(vertex.X, -vertex.Y, vertex.Z)) : (V[])vertices.Clone();
            int sign = SupportFootprintExactOracle.Normal(vertices).Y.Numerator.Sign;
            if (sign == 0) continue;
            if (sign < 0) Array.Reverse(vertices);
            OracleVector[] approximate = Array.ConvertAll(vertices, vertex => new OracleVector(
                SupportFootprintExactOracle.AsDouble(vertex.X), SupportFootprintExactOracle.AsDouble(vertex.Y),
                SupportFootprintExactOracle.AsDouble(vertex.Z)));
            var oriented = new OracleFace(face.Static, face.ElementId, approximate) { ExactVertices = vertices };
            if (SupportFootprintExactOracle.Contact(oriented, proposal) is not { } contact) continue;
            highest = highest is { } current
                ? new Bounds(Max(current.Lower, contact.Lower), Max(current.Upper, contact.Upper)) : contact;
        }
        return highest;
    }

    // A round surface reaches its radius beyond its core segment. A cylinder's rim reaches its radius across the axis.
    static double Top(OracleTangent part) => part.Centre.Y + Math.Abs(part.Axis.Y) * part.HalfLength + part.Radius *
        (part.Surface == OracleSurface.Round ? 1 : Math.Sqrt(Math.Max(0, 1 - part.Axis.Y * part.Axis.Y)));

    static R Max(R a, R b) => a > b ? a : b;
}
