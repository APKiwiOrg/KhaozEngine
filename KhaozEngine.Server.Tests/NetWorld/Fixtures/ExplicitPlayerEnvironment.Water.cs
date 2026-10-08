using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed partial class ExplicitPlayerEnvironment
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementDomainKey Lake = new("world", "lake");
    const float Error = 0.00001f;
    MovementWaterInterval Interval => new(FloorY, SurfaceY!.Value, SurfaceY.Value, true, "bed", "surface");

    MovementWaterPoint SampleWater(in MovementBodyQuery body) =>
        SurfaceY is { } surface && body.Feet.Y >= FloorY && body.Feet.Y < surface
            ? new(MovementAvailability.Known, Room, Lake, true, 1, Interval)
            : new(MovementAvailability.Known, Room, null, false, 1, null);

    // Finite uniform slab inside the declared 32 m room. This fixture certifies the complete affine
    // capsule interval against both horizontal planes. It supplies no native topology or sampling.
    MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
        Span<MovementDomainContact> contacts)
    {
        if (SurfaceY is not { } top)
        {
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, Identity);
        }
        double centre = query.Body.Centre.Y, dy = query.Delta.Y;
        double half = query.Body.HalfHeight + (double)MovementQueryLease.CoverageSkinMetres;
        var cuts = new SortedSet<float> { 0, 1 };
        if (dy != 0)
        {
            foreach (double plane in new double[] { FloorY - half, FloorY + half, top - half, top + half })
            {
                double t = (plane - centre) / dy;
                if (t > 0 && t < 1) cuts.Add((float)t);
            }
        }
        float[] times = cuts.ToArray();
        var outputSpans = new List<MovementCoverageSpan>();
        var outputContacts = new List<MovementDomainContact>();
        MovementMediumSweepQuery path = query;
        for (int i = 0; i < times.Length; i++)
        {
            Append(times[i], times[i]);
            if (i + 1 < times.Length) Append(times[i], times[i + 1]);
        }
        if (spans.Length < outputSpans.Count || contacts.Length < outputContacts.Count)
            return new(MovementAvailability.CapacityExceeded, 0, outputSpans.Count, 0, outputContacts.Count, Error, Identity);
        outputSpans.ToArray().CopyTo(spans);
        outputContacts.ToArray().CopyTo(contacts);
        return new(MovementAvailability.Known, outputSpans.Count, outputSpans.Count,
            outputContacts.Count, outputContacts.Count, Error, Identity);

        void Append(float start, float end)
        {
            double middle = centre + dy * ((double)start + end) * 0.5;
            bool wet = middle + half >= FloorY - Error && middle - half <= top + Error;
            bool dry = middle - half < FloorY + Error || middle + half > top - Error;
            int first = outputContacts.Count;
            if (wet)
            {
                double a = centre + dy * start, b = centre + dy * end;
                double raw = path.Body.HalfHeight;
                bool point = a + raw >= FloorY - Error && a - raw <= top + Error;
                bool overlap = Math.Max(a, b) + raw >= FloorY - Error && Math.Min(a, b) - raw <= top + Error;
                Vector3 at = path.Body.Centre + path.Delta * start;
                outputContacts.Add(new(Lake, Room, Interval, new(at.X, at.Z),
                    a >= (FloorY + top) * 0.5 ? Vector3.UnitY : -Vector3.UnitY, start, "slab", 0,
                    point ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent,
                    overlap ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent));
            }
            outputSpans.Add(new(start, end, first, outputContacts.Count - first, dry));
        }
    }
}
