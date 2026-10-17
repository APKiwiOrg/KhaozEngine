using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Bounds = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The proposal refusals SG-6 keeps, decided on installed polygon geometry without the backend sweep. A
/// downward proposal refuses when its raised start lies inside geometry or when it stops above the reach band.
/// Each convex leaf is a solid. A mesh triangle is a plate that counts only while its front faces up, since
/// proposals cull mesh back faces. Curved elements above the band top and the third guard refusal, a stop in the
/// band on members that support nothing at or below the band top, are outside the model and refuse to judge.</summary>
internal static class FootSupportGuardOracle
{
    // Face vertices in binary64 lie within rounding of their exact values. The margin widens prefilters and turns
    // a knife-edge support decision into a refusal to judge. It never decides a refusal.
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

    /// <summary>Throws when the third guard refusal could decide the read: a proposal stops in the band, no member it
    /// touches certainly supports at or below the band top, and the stop is not certainly below the selected
    /// support, which a lower refusal cannot override. Members certainly supporting there are cleared first, so
    /// ordinary floors need no sweep.</summary>
    internal static void RequireModelled(SupportFootprintScene scene, in SupportFootprintRequest request,
        IReadOnlyList<FootprintOracleMember> members, in FootprintOracleSupport selected)
    {
        SupportFootprintRequest copy = request;
        var doubtful = members.Where(member => !Supports(member, members, copy)).ToList();
        if (doubtful.Count == 0) return;
        if (doubtful.Any(member => member.Face is null))
            throw new NotSupportedException("The guard oracle does not model a curved member that may stop a proposal " +
                $"without support at or below the band top {request.BandUpper}.");
        foreach (SupportFootprintProposal proposal in new[] { request.AxisProposal, request.FootProposal })
        {
            List<(OracleFace Face, Bounds Contact)> contacts = Contacts(scene, proposal, request.BandLower);
            if (contacts.Count == 0) continue;
            R stop = contacts.Select(contact => contact.Contact.Lower).Aggregate(Max);
            R stopUpper = contacts.Select(contact => contact.Contact.Upper).Aggregate(Max);
            // A refusal below the band bottom lies in the sweep margin only and hides nothing.
            if (stopUpper < R.From(request.BandLower)) continue;
            if (selected.Status is SupportStatus.Walkable or SupportStatus.Steep &&
                SupportFootprintExactOracle.AsDouble(stopUpper) < selected.HeightLower - Margin) continue;
            bool supported = contacts.Where(contact => contact.Contact.Upper >= stop).Any(contact =>
                members.FirstOrDefault(member => member.Face is { } face && face.Static == contact.Face.Static &&
                    face.ElementId == contact.Face.ElementId) is { } member && Supports(member, members, copy));
            if (!supported)
                throw new NotSupportedException($"The guard oracle does not model the third guard refusal: a proposal " +
                    $"stops at {SupportFootprintExactOracle.AsDouble(stop)} in the band on members without support at " +
                    $"or below the band top {request.BandUpper}.");
        }
    }

    // Heights are the probe's lowest point. A solid refuses when its top contact is certainly above the band and the
    // probe touching its underside is not certainly above the start: the start lies inside it or the sweep stops on
    // it above the band. A solid wholly above the start is never reached by a downward sweep.
    static bool Refuses(SupportFootprintScene scene, SupportFootprintProposal proposal, double bandTop)
    {
        var probe = new Probe(proposal);
        R top = R.From(bandTop);
        foreach (var solid in Solids(scene))
        {
            if (!probe.MayReach(solid, bandTop)) continue;
            (IEnumerable<OracleFace> upper, IEnumerable<OracleFace> underside) = Sides(scene, solid);
            if (Highest(upper, proposal, mirror: false) is not { } above || !(above.Lower > top)) continue;
            if (Highest(underside, proposal, mirror: true) is not { } below || !probe.CertainlyAboveStart(below)) return true;
        }
        return false;
    }

    // Every top contact a downward proposal can stop on at or above the band bottom, skipping solids wholly above
    // its start.
    static List<(OracleFace Face, Bounds Contact)> Contacts(SupportFootprintScene scene, SupportFootprintProposal proposal,
        double bandBottom)
    {
        var probe = new Probe(proposal);
        var contacts = new List<(OracleFace, Bounds)>();
        foreach (var solid in Solids(scene))
        {
            if (!probe.MayReach(solid, bandBottom)) continue;
            (IEnumerable<OracleFace> upper, IEnumerable<OracleFace> underside) = Sides(scene, solid);
            if (Highest(underside, proposal, mirror: true) is { } below && probe.CertainlyAboveStart(below)) continue;
            foreach (OracleFace face in upper)
                if (Contact(face, proposal, mirror: false) is { } contact) contacts.Add((face, contact));
        }
        return contacts;
    }

    static IEnumerable<IGrouping<(StaticHandle Static, int Leaf), OracleFace>> Solids(SupportFootprintScene scene) =>
        scene.Faces.GroupBy(face => (face.Static, scene.Meshes.Contains(face.Static) ? face.ElementId : face.ElementId / 256));

    // The faces a downward sweep reads from above and the faces whose underside it touches from below. A mesh
    // triangle is hit from either side only while its front faces up.
    static (IEnumerable<OracleFace> Upper, IEnumerable<OracleFace> Underside) Sides(SupportFootprintScene scene,
        IGrouping<(StaticHandle Static, int Leaf), OracleFace> solid)
    {
        if (!scene.Meshes.Contains(solid.Key.Static))
            return (solid.Where(face => face.Normal.Y > 0), solid.Where(face => face.Normal.Y < 0));
        OracleFace[] front = solid.Where(face =>
            SupportFootprintExactOracle.Normal(SupportFootprintExactOracle.Vertices(face)).Y.Numerator.Sign > 0).ToArray();
        return (front, front);
    }

    // The member's context as FootSupport.Guard reads it, certainly at or below the band top. A polygon must face up
    // and not be steep beside a joined walkable member. Its height is the lowest same-kind joined plane at the axis,
    // capped by its disc maximum. A curved member needs an upward witness at or below the band top.
    static bool Supports(FootprintOracleMember member, IReadOnlyList<FootprintOracleMember> members,
        in SupportFootprintRequest request)
    {
        double top = request.BandUpper - Margin, x = request.Axis.X, z = request.Axis.Y;
        if (member.Face is not { } face)
            return member.Witnesses.Any(witness => witness.Normal.Y > 0 && Math.Min(witness.Position.Y,
                witness.Position.Y + (witness.Normal.X * (witness.Position.X - x) +
                    witness.Normal.Z * (witness.Position.Z - z)) / witness.Normal.Y) <= top);
        if (!(face.Normal.Y > 0)) return false;
        bool walkable = face.Normal.Y >= request.CosMaxSlope;
        double low = member.HasDiscMaximum ? Math.Min(member.Plane, member.Maximum) : member.Plane;
        // A joined plane only lowers a walkable member's height, so one already low enough needs no join.
        if (walkable && low <= top) return true;
        foreach (FootprintOracleMember other in members)
        {
            if (ReferenceEquals(other, member) || other.Face is not { } joined || !(joined.Normal.Y > 0) ||
                !SupportFootprintOracle.Joined(face, joined, request.ContactBand)) continue;
            bool otherWalkable = joined.Normal.Y >= request.CosMaxSlope;
            if (!walkable && otherWalkable) return false;
            if (walkable == otherWalkable) low = Math.Min(low, other.Plane);
        }
        return low <= top;
    }

    // The highest resting contact of the probe's lower cap on the faces, or of its upper cap on their underside when
    // mirrored.
    static Bounds? Highest(IEnumerable<OracleFace> faces, SupportFootprintProposal proposal, bool mirror)
    {
        Bounds? highest = null;
        foreach (OracleFace face in faces)
            if (Contact(face, proposal, mirror) is { } contact)
                highest = highest is { } current
                    ? new Bounds(Max(current.Lower, contact.Lower), Max(current.Upper, contact.Upper)) : contact;
        return highest;
    }

    // Each face is oriented upward first, so a mirrored downward face is read from above.
    static Bounds? Contact(OracleFace face, SupportFootprintProposal proposal, bool mirror)
    {
        V[] vertices = SupportFootprintExactOracle.Vertices(face);
        vertices = mirror ? Array.ConvertAll(vertices, vertex => new V(vertex.X, -vertex.Y, vertex.Z)) : (V[])vertices.Clone();
        int sign = SupportFootprintExactOracle.Normal(vertices).Y.Numerator.Sign;
        if (sign == 0) return null;
        if (sign < 0) Array.Reverse(vertices);
        OracleVector[] approximate = Array.ConvertAll(vertices, vertex => new OracleVector(
            SupportFootprintExactOracle.AsDouble(vertex.X), SupportFootprintExactOracle.AsDouble(vertex.Y),
            SupportFootprintExactOracle.AsDouble(vertex.Z)));
        var oriented = new OracleFace(face.Static, face.ElementId, approximate) { ExactVertices = vertices };
        return SupportFootprintExactOracle.Contact(oriented, proposal);
    }

    // A round surface reaches its radius beyond its core segment. A cylinder's rim reaches its radius across the axis.
    static double Top(OracleTangent part) => part.Centre.Y + Math.Abs(part.Axis.Y) * part.HalfLength + part.Radius *
        (part.Surface == OracleSurface.Round ? 1 : Math.Sqrt(Math.Max(0, 1 - part.Axis.Y * part.Axis.Y)));

    static R Max(R a, R b) => a > b ? a : b;

    /// <summary>One proposal's start lowest point and extent, exact and in binary64 for prefilters.</summary>
    readonly struct Probe
    {
        readonly R start, height;
        readonly double lowest, span, x, z, reach;

        internal Probe(in SupportFootprintProposal proposal)
        {
            R radius = R.From(proposal.Probe.Radius), length = R.From(proposal.Probe.Length);
            start = R.From(proposal.Start.Position.Y) - length / new R(2, 1) - radius;
            height = length + radius + radius;
            span = (double)proposal.Probe.Length + 2 * (double)proposal.Probe.Radius;
            lowest = proposal.Start.Position.Y - span / 2;
            reach = proposal.Probe.Radius + Margin;
            (x, z) = (proposal.Start.Position.X, proposal.Start.Position.Z);
        }

        // Necessary conditions before any exact work. A top contact lies at or below the highest vertex, which must
        // reach the floor height. The probe touching the underside lies at or above the lowest vertex less the probe
        // height. Any contact needs the solid within the probe radius of the axis.
        internal bool MayReach(IEnumerable<OracleFace> solid, double floor)
        {
            IEnumerable<OracleVector> vertices = solid.SelectMany(face => face.Vertices);
            return vertices.Max(vertex => vertex.Y) >= floor - Margin &&
                vertices.Min(vertex => vertex.Y) - span <= lowest + Margin &&
                vertices.Min(vertex => vertex.X) <= x + reach && vertices.Max(vertex => vertex.X) >= x - reach &&
                vertices.Min(vertex => vertex.Z) <= z + reach && vertices.Max(vertex => vertex.Z) >= z - reach;
        }

        // The mirrored underside contact bounds the probe's top when it touches the solid from below.
        internal bool CertainlyAboveStart(Bounds below) => -below.Upper - height > start;
    }
}
