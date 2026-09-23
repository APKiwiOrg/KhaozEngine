using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Reads per-cluster light lists out of a built cluster image, so tests assert on assignments rather than on
/// the layout that stores them. The oracle keeps the fixed seventeen-uvec4 layout for good.</summary>
internal static class PointLightClusterImage
{
    internal static int ClusterIndex(int x, int y, int z) =>
        (z * PointLightClusterBuilder.ClusterCountY + y) * PointLightClusterBuilder.ClusterCountX + x;

    internal static ReadOnlySpan<uint> Lights(PointLightClusterOracle oracle, int cluster, out bool overflow)
    {
        int offset = cluster * PointLightClusterOracle.ClusterStrideUInts;
        overflow = oracle.Image[offset + 1] != 0u;
        return oracle.Image.AsSpan(offset + PointLightClusterOracle.HeaderUInts, (int)oracle.Image[offset]);
    }

    /// <summary>A cluster's stored indices and overflow flag in the compact layout: one header uint per cluster, the
    /// count in the low 8 bits and the offset into the index region in the high 24. It also asserts the run lies
    /// inside the uploaded prefix, which is what the shader can see.</summary>
    internal static ReadOnlySpan<uint> Lights(PointLightClusterBuilder builder, int cluster, out bool overflow)
    {
        uint header = builder.Image[cluster];
        uint count = header & PointLightClusterBuilder.CountMask;
        overflow = count == PointLightClusterBuilder.OverflowCount;
        if (overflow || count == 0u) return ReadOnlySpan<uint>.Empty;
        int first = PointLightClusterBuilder.HeaderRegionUInts + (int)(header >> PointLightClusterBuilder.CountBits);
        Assert.True(first + (int)count <= builder.UsedUIntCount,
            $"cluster {cluster} reads indices {first} to {first + (int)count} past the uploaded prefix of "
            + $"{builder.UsedUIntCount}");
        return builder.Image.AsSpan(first, (int)count);
    }

    internal static bool Contains(PointLightClusterOracle oracle, int x, int y, int z, uint light) =>
        Lights(oracle, ClusterIndex(x, y, z), out _).IndexOf(light) >= 0;

    internal static bool Contains(PointLightClusterBuilder builder, int x, int y, int z, uint light) =>
        Lights(builder, ClusterIndex(x, y, z), out _).IndexOf(light) >= 0;

    internal static void AssertSameAssignment(PointLightClusterOracle oracle, PointLightClusterBuilder builder,
        string context)
    {
        Assert.True(oracle.Depth == builder.Depth, $"{context}: depth {builder.Depth}, brute force {oracle.Depth}");
        Assert.True(oracle.CameraForward == builder.CameraForward,
            $"{context}: forward {builder.CameraForward}, brute force {oracle.CameraForward}");
        Assert.True(oracle.OverflowedClusters == builder.OverflowedClusters,
            $"{context}: {builder.OverflowedClusters} overflowed clusters, brute force {oracle.OverflowedClusters}");
        Assert.True(oracle.LightReferenceCount == builder.LightReferenceCount,
            $"{context}: {builder.LightReferenceCount} references, brute force {oracle.LightReferenceCount}");
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            ReadOnlySpan<uint> expected = Lights(oracle, cluster, out bool expectedOverflow);
            ReadOnlySpan<uint> actual = Lights(builder, cluster, out bool actualOverflow);
            bool same = expectedOverflow == actualOverflow && (expectedOverflow || expected.SequenceEqual(actual));
            if (!same)
                Assert.Fail($"{context}: cluster {cluster} holds [{string.Join(",", actual.ToArray())}] with overflow "
                    + $"{actualOverflow}, brute force [{string.Join(",", expected.ToArray())}] with overflow "
                    + $"{expectedOverflow}");
        }
    }
}
