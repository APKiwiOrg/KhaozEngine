using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSupportApiTests
{
    [Fact]
    public void Support_traversal_storage_is_internal_while_public_queries_remain_available()
    {
        // FR-3 keeps traversal storage out of the released consumer contract.
        var exported = typeof(CoverageSupportSurface).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.Name is "CoverageBvh" or "CoverageBvhNode");
        var members = typeof(CoverageSupportSurface).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(members, n => n is "SolidBvh" or "ProjectedBvh" or "EdgeBvh"
            or "PrecisePositions" or "ProjectedPositions" or "BoundarySourceVertices" or "InwardSourceIndices");
        Assert.Empty(typeof(CoverageCoordinate).GetDefaultMembers());
        Assert.Contains(typeof(CoverageSupportSurface).GetMethods(), m => m.Name == nameof(CoverageSupportSurface.TryGetHeightBounds));
    }
}
