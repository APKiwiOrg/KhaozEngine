using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageFilterStorageTests
{
    [Fact]
    public void Full_pair_capacity_has_disjoint_records_and_queues_with_checked_addresses()
    {
        var host = new CoverageGpuHostUpload(new uint[64], 1, 3, 7, 3, 0, 3, 1);
        uint[] exact = CoverageGpuLayout.WorkHeader(host);
        var filtered = host with { FilterSupportCapacity = 3 };
        uint[] header = CoverageGpuLayout.WorkHeader(filtered);
        Assert.Equal(0u, exact[45]); Assert.Equal(0u, exact[47]);
        Assert.Equal(7u, header[45]); Assert.Equal(21u, header[47]);
        Assert.Equal(7u, header[49]); Assert.Equal(21u, header[51]);
        Assert.Equal(3u, header[56]);
        Assert.Equal(header[44] + 7u, header[46]);
        Assert.Equal(header[46] + 63u, header[48]);
        Assert.Equal(header[48] + 7u, header[50]);
        Assert.Equal(header[50] + 21u, CoverageGpuLayout.WorkWords(filtered));
        Assert.Equal(exact[42], header[42]);
        Assert.Equal(98u, CoverageGpuLayout.WorkWords(filtered) - CoverageGpuLayout.WorkWords(host));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.WorkHeader(host with { FilterSupportCapacity = int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageGpuLayout.WorkWords(host with { FilterSupportCapacity = -1 }));
    }
}
