using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Canonical ordered integer endpoints, delta encoded without replaying the bake recipe.</summary>
internal static class TerrainCandidateCodec
{
    internal static byte[] Encode(IReadOnlyList<NavLink> links, int width, int height)
    {
        var writer = new NavBakeWriter(); Put(writer, (ulong)links.Count);
        long previousFrom = 0, previousTo = 0, cells = checked(width * height);
        foreach (var link in links)
        {
            long from = checked(link.FromLayer * cells + link.FromZ * (long)width + link.FromX);
            long to = checked(link.ToLayer * cells + link.ToZ * (long)width + link.ToX);
            Put(writer, Zig(from - previousFrom)); Put(writer, Zig(to - previousTo)); writer.WriteUInt8((byte)link.Kind);
            previousFrom = from; previousTo = to;
        }
        return writer.ToArray();
    }

    internal static NavLink[] Decode(ReadOnlySpan<byte> bytes, IReadOnlyList<NavTraversalLayer> layers, int expected)
    {
        var reader = new NavBakeReader(bytes);
        ulong count = Get(ref reader);
        if (count != (ulong)expected || count > (ulong)(reader.Remaining / 3)) throw Corrupt();
        var budget = new DescentCaptureIndexBudget(); budget.Reserve(bytes.Length, 1); budget.Reserve(expected, 64);
        int width = layers[0].Width, height = layers[0].Height;
        long cells = checked(width * height), bound = checked(cells * layers.Count), from = 0, to = 0;
        var result = new NavLink[expected]; var unique = new HashSet<NavLink>();
        for (int i = 0; i < result.Length; i++)
        {
            from = checked(from + Unzig(Get(ref reader))); to = checked(to + Unzig(Get(ref reader)));
            if (from < 0 || to < 0 || from >= bound || to >= bound || !reader.TryReadUInt8(out byte kind) ||
                kind is not ((byte)NavLinkKind.Stair) and not ((byte)NavLinkKind.Drop)) throw Corrupt();
            int fl = (int)(from / cells), tl = (int)(to / cells), f = (int)(from % cells), t = (int)(to % cells);
            var link = new NavLink(fl, f % width, f / width, tl, t % width, t / width) { Kind = (NavLinkKind)kind };
            if (from == to || !layers[fl].IsAccepted(link.FromX, link.FromZ) ||
                !layers[tl].IsAccepted(link.ToX, link.ToZ) || !unique.Add(link)) throw Corrupt();
            result[i] = link;
        }
        if (reader.Remaining != 0) throw Corrupt();
        return result;
    }

    static ulong Zig(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));
    static long Unzig(ulong value) => unchecked((long)(value >> 1) ^ -((long)value & 1));
    static void Put(NavBakeWriter writer, ulong value)
    {
        while (value >= 128) { writer.WriteUInt8((byte)((value & 127) | 128)); value >>= 7; }
        writer.WriteUInt8((byte)value);
    }
    static ulong Get(ref NavBakeReader reader)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (!reader.TryReadUInt8(out byte b) || shift == 63 && b > 1) throw Corrupt();
            value |= (ulong)(b & 127) << shift;
            if (b < 128)
            {
                if (shift != 0 && b == 0) throw Corrupt();
                return value;
            }
        }
        throw Corrupt();
    }
    static InvalidDataException Corrupt() => new("Invalid stored terrain candidate list.");
}
