using System;
using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>Cold full-capacity admission shared by resource replacement and batch construction.</summary>
internal static class CoverageGpuFilterReservation
{
    internal static CoverageGpuHostUpload ForSupports(CoverageGpuHostUpload host,
        ReadOnlySpan<CoverageSupportSurface> supports, CoverageGpuFilterPath path)
    {
        int capacity = 0;
        if (path != CoverageGpuFilterPath.Exact)
            foreach (var support in supports) capacity = Math.Max(capacity, support.InwardTriangles.Count);
        return host with { FilterSupportCapacity = capacity };
    }

    internal static CoverageGpuHostUpload[] Fit(CoverageGpuHostUpload[] requested)
    {
        if (requested.Sum(h => (long)CoverageGpuLayout.WorkWords(h)) <= CoverageGpuFilterPolicy.MaximumWorkWords)
            return requested;
        return requested.Select(h => h with { FilterSupportCapacity = 0 }).ToArray();
    }
}
