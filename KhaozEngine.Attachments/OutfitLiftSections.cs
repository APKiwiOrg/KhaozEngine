using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Groups coincident lift bases and accumulates the highest surface each section must clear.</summary>
internal sealed class OutfitLiftSections(int capacity)
{
    readonly Dictionary<SectionKey, int> indexByBasis = new();
    readonly Section[] sections = new Section[capacity];

    internal int GetOrAdd(Vector3 direction, float bearing)
    {
        var key = new SectionKey(new LiftVectorBits(direction), LiftVectorBits.Bits(bearing));
        if (!indexByBasis.TryGetValue(key, out int index))
        {
            index = indexByBasis.Count;
            indexByBasis.Add(key, index);
            sections[index] = new(bearing, float.NegativeInfinity);
        }
        return index;
    }

    internal void Include(int index, float height) =>
        sections[index].HeightBelow = MathF.Max(sections[index].HeightBelow, height);

    internal void ResolveDistances(float clearance)
    {
        for (int index = 0; index < indexByBasis.Count; index++)
        {
            ref Section section = ref sections[index];
            if (section.HeightBelow != float.NegativeInfinity)
                section.Distance = MathF.Max(0, section.HeightBelow + clearance - section.Bearing);
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
