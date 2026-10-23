using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneCapacityDiagnosticsTests
{
    [Fact]
    public void Capture_ReportsTileDemandAndAllowance()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var result = MapSceneSource.Capture(document, fixture.Assets, new(MaxTiles: 0));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxTiles", 1, 0), result.Capacity);
        Assert.Contains("MaxTiles", Assert.Single(result.Findings));
        Assert.Contains("requested 1", result.Findings[0]);
        Assert.Contains("allowance 0", result.Findings[0]);
    }

    [Fact]
    public void SpatialSelection_ReportsFirstExcessResource()
    {
        using var fixture = NativeSceneFixtures.Create("growth");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets, new(MaxSelectedResources: 0)).Source!;
        var result = source.Acquire(MapSceneScopeTests.Window());
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxSelectedResources", 1, 0), result.Capacity);
        Assert.Contains("MaxSelectedResources", Assert.Single(result.Findings));
        Assert.Contains("requested 1", result.Findings[0]);
        Assert.Contains("allowance 0", result.Findings[0]);
    }

    [Fact]
    public void Preparation_ReportsAggregateDemandAndAllowance()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 3));
        admission.ReserveBytes(2);
        var error = Assert.Throws<MapSceneCapacityException>(() => admission.ReserveBytes(2));
        Assert.Equal("MaxPreparedBytes", error.LimitName);
        Assert.Equal(4, error.Requested);
        Assert.Equal(3, error.Allowance);
        Assert.Contains("MaxPreparedBytes", error.Message);
        Assert.Contains("requested 4", error.Message);
        Assert.Contains("allowance 3", error.Message);
    }
}
