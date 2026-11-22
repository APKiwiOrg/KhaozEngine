using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Shared;

/// <summary>Two FNV-1a digests over explicit, little-endian 32-bit fields in publication order.</summary>
internal sealed class PathPublicationDigest
{
    const ulong OffsetBasis = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;
    ulong _routes = OffsetBasis;
    ulong _calls = OffsetBasis;

    internal int Count { get; private set; }

    internal string Hex => $"{_routes:X16}/{_calls:X16}";

    internal void Append(NavPath path, int call)
    {
        Count++;
        Write(ref _calls, call);
        Write(ref _routes, (int)path.Status);
        Write(ref _routes, path.Waypoints.Count);
        foreach (NavWaypoint waypoint in path.Waypoints)
        {
            Write(ref _routes, BitConverter.SingleToInt32Bits(waypoint.Position.X));
            Write(ref _routes, BitConverter.SingleToInt32Bits(waypoint.Position.Y));
            Write(ref _routes, (int)waypoint.Kind);
            Write(ref _routes, waypoint.Layer);
        }
    }

    static void Write(ref ulong hash, int value)
    {
        uint bits = unchecked((uint)value);
        for (int i = 0; i < sizeof(int); i++)
        {
            hash = unchecked((hash ^ (bits & 0xff)) * Prime);
            bits >>= 8;
        }
    }

    static readonly int[] MixedQuotas = [17, 1, 64, 5, 256];

    internal static int Quota(int schedule, int zeroBasedCall) => schedule switch
    {
        0 => int.MaxValue,
        1 => 64,
        2 => MixedQuotas[zeroBasedCall % MixedQuotas.Length],
        _ => throw new ArgumentOutOfRangeException(nameof(schedule)),
    };
}
