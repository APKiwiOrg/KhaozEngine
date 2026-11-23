using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Groups coincident lift bases and accumulates the highest surface each section must clear.</summary>
internal sealed class OutfitLiftSections(int capacity, LiftFootprint? normalFootprint)
{
    readonly Dictionary<SectionKey, int> indexByBasis = new();
    readonly Section[] sections = new Section[capacity];
    readonly List<ClaimPoint>? unitDirections = normalFootprint is null ? null : new();
    readonly Dictionary<(int Section, int Face), float>? heightByFace = normalFootprint is null ? null : new();

    internal int GetOrAdd(Vector3 direction, float bearing)
    {
        var key = new SectionKey(new LiftVectorBits(direction), LiftVectorBits.Bits(bearing));
        if (!indexByBasis.TryGetValue(key, out int index))
        {
            index = indexByBasis.Count;
            indexByBasis.Add(key, index);
            sections[index] = new(bearing, float.NegativeInfinity);
            if (unitDirections is not null)
            {
                var authored = new ClaimPoint(direction);
                unitDirections.Add(authored * (1 / Math.Sqrt(authored.LengthSquared)));
            }
        }
        return index;
    }

    internal void Include(int index, float height) =>
        sections[index].HeightBelow = MathF.Max(sections[index].HeightBelow, height);

    internal void IncludeFaceHeight(ReadOnlySpan<int> corners, int face, float height)
    {
        IncludeFaceHeight(corners[0], face, height);
        if (corners[1] != corners[0])
            IncludeFaceHeight(corners[1], face, height);
        if (corners[2] != corners[0] && corners[2] != corners[1])
            IncludeFaceHeight(corners[2], face, height);
    }

    void IncludeFaceHeight(int section, int face, float height)
    {
        var key = (section, face);
        if (!heightByFace!.TryGetValue(key, out float previous) || height > previous)
            heightByFace[key] = height;
    }

    internal void ResolveDistances(float clearance)
    {
        for (int index = 0; index < indexByBasis.Count; index++)
        {
            ref Section section = ref sections[index];
            if (section.HeightBelow != float.NegativeInfinity)
                section.Distance = MathF.Max(0, section.HeightBelow + clearance - section.Bearing);
        }

        if (heightByFace is null)
            return;

        foreach (var (key, height) in heightByFace)
        {
            ref Section section = ref sections[key.Section];
            Vector3 normal = normalFootprint!.NormalForBodyTriangle(key.Face)!.Value;
            double projection = ClaimPoint.Dot(unitDirections![key.Section], new ClaimPoint(normal));
            float distance = (float)((height + clearance - section.Bearing) / Math.Max(projection, 0.5));
            section.Distance = MathF.Max(section.Distance, distance);
        }
    }

    internal float Distance(int index) => sections[index].Distance;

    readonly record struct SectionKey(LiftVectorBits Direction, int Bearing);

    struct Section(float bearing, float heightBelow)
    {
        internal readonly float Bearing = bearing;
        internal float HeightBelow = heightBelow;
        internal float Distance;
    }
}

internal readonly record struct LiftVectorBits(int X, int Y, int Z)
{
    internal LiftVectorBits(Vector3 value) : this(Bits(value.X), Bits(value.Y), Bits(value.Z)) { }

    // Negative zero is the same as zero for positions, directions and bearings.
    internal static int Bits(float value) => BitConverter.SingleToInt32Bits(value == 0 ? 0 : value);
}
