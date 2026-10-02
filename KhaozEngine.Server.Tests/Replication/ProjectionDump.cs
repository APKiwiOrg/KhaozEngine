using System;
using System.Collections.Generic;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>One retained component frame, copied out of a projection for comparison.</summary>
internal readonly record struct ProjectionEntry(long NetId, ushort TypeId, byte[] Payload);

/// <summary>
/// Test oracle over the projection seams. It reads only <c>EntriesForTest</c> and <c>BackingArraysForTest</c>, never
/// the retention or publication code a test checks.
/// </summary>
internal static class ProjectionDump
{
    /// <summary>Every frame of <paramref name="projection"/>, ordered by net id then type id, payloads copied.</summary>
    public static IReadOnlyList<ProjectionEntry> Of(ReplicationProjection projection)
    {
        var entries = new List<ProjectionEntry>();
        foreach ((long netId, ushort typeId, ReadOnlyMemory<byte> payload) in projection.EntriesForTest())
            entries.Add(new ProjectionEntry(netId, typeId, payload.ToArray()));
        for (int i = 1; i < entries.Count; i++)
        {
            ProjectionEntry a = entries[i - 1], b = entries[i];
            Assert.True(a.NetId < b.NetId || (a.NetId == b.NetId && a.TypeId < b.TypeId),
                $"EntriesForTest out of order at {i}: ({a.NetId},{a.TypeId}) then ({b.NetId},{b.TypeId}).");
        }
        return entries;
    }

    /// <summary>Compares membership, type ids and payload bytes, zero-byte and opaque frames included.</summary>
    public static void AssertEqual(IReadOnlyList<ProjectionEntry> expected, IReadOnlyList<ProjectionEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].NetId, actual[i].NetId);
            Assert.Equal(expected[i].TypeId, actual[i].TypeId);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
        }
    }

    /// <summary>The sum of <c>Length</c> over the distinct backing arrays of <paramref name="projections"/>, by
    /// reference identity: the exact expected retained-byte figure.</summary>
    public static int DistinctBackingBytes(IEnumerable<ReplicationProjection> projections)
    {
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        int total = 0;
        foreach (ReplicationProjection projection in projections)
            foreach (byte[] backing in projection.BackingArraysForTest())
                if (seen.Add(backing)) total += backing.Length;
        return total;
    }

    /// <summary>The sum of <c>Length</c> over distinct arrays, by reference identity.</summary>
    public static int DistinctLength(IEnumerable<byte[]> arrays)
    {
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        int total = 0;
        foreach (byte[] array in arrays)
            if (seen.Add(array)) total += array.Length;
        return total;
    }
}
