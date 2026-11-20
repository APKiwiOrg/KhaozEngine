using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Facts a water capture verifies about its own columns when it is built. Every column of one domain must
/// carry the same surface bits, and the domain count is recorded against the lease's distinct-domain bound. The
/// boundary reach certificate declines when either fact does not hold, so it never relies on a fact the capture
/// did not check.</summary>
internal readonly record struct TileWaterCaptureFacts(bool OneLevelPerDomain, int DomainCount)
{
    /// <summary>A capture whose facts were not verified. Every certificate over it declines.</summary>
    internal static TileWaterCaptureFacts Unverified => new(false, 0);

    internal bool Hold => OneLevelPerDomain && DomainCount <= MovementQueryLease.MaxWaterDomainsPerLease;

    internal static TileWaterCaptureFacts Verify(ReadOnlySpan<TileWaterColumn> columns)
    {
        var levels = new Dictionary<MovementDomainKey, int>();
        bool oneLevel = true;
        foreach (TileWaterColumn column in columns)
        {
            int bits = BitConverter.SingleToInt32Bits(column.Maximum.Y);
            if (!levels.TryAdd(column.Domain, bits) && levels[column.Domain] != bits) oneLevel = false;
        }
        return new(oneLevel, levels.Count);
    }
}
