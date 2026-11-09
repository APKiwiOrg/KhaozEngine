using System;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Storage;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class CapacityDemandGateMatrixTests
{
    [Theory]
    [InlineData("MaxSelectedResources", "Placement")]
    [InlineData("MaxChunkDescriptors", "Foliage")]
    [InlineData("MaxPlacements", "Placement")]
    [InlineData("MaxPatchScopes", "scope")]
    [InlineData("MaxPaintTargets", "paint")]
    [InlineData("MaxSourceUtf8Bytes", "bytes")]
    public void SceneAdmission_OneAcceptedThenOneRefusedIncrement(string limit, string operation)
    {
        var limits = new MapSceneLimits(MaxSelectedResources: limit == "MaxSelectedResources" ? 1 : 100, MaxPatchScopes: 1);
        var foliage = new MapFoliageContextLimits(MaxChunkDescriptors: 1, MaxPlacements: 1, MaxPaintTargets: 1, MaxSourceUtf8Bytes: 1);
        var admission = new MapSceneAdmission(limits, foliageLimits: foliage);
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        Action<int> run = index =>
        {
            if (operation == "bytes") admission.BeforeInputBytes(1);
            else if (operation == "scope") admission.BeforeScope(source);
            else if (operation == "paint") admission.BeforePaintTarget(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else admission.BeforeResource(new(operation, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };
        using var probe = new CapacityDemandProbe();
        run(0);
        Assert.Throws<MapSceneCapacityException>(() => run(1));
        var rows = probe.For(limit).ToArray();
        Assert.Equal(2, rows.Length);
        AssertDemand(rows[0], 0, 1, 1, false);
        AssertDemand(rows[1], 1, 1, 1, true);
    }

    [Theory]
    [InlineData("CanReservePreparation.Faces")]
    [InlineData("MaxPreparedFaces.1")]
    public void PreparationFaceProbes_UseTheAdmittedBaseline(string site)
    {
        var admission = new MapSceneAdmission(new(MaxPreparedFaces: 3));
        Assert.True(admission.TryReservePreparation(0, 2));
        using var probe = new CapacityDemandProbe();
        if (site == "CanReservePreparation.Faces")
        {
            Assert.True(admission.CanReservePreparation(0, 1));
            Assert.False(admission.CanReservePreparation(0, 2));
        }
        else
        {
            Assert.Null(admission.PreparationRefusal(0, 1));
            Assert.NotNull(admission.PreparationRefusal(0, 2));
        }
        var rows = probe.For("MaxPreparedFaces").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("MapSceneAdmission." + site, Read<string>(r, "Site")));
        AssertDemand(rows[0], 2, 1, 3, false);
        AssertDemand(rows[1], 2, 2, 3, true);
        Assert.Equal(2, admission.PreparedFaces);
    }

    static void AssertDemand(object row, decimal current, decimal increment, decimal allowance, bool refused)
    {
        Assert.Equal(current, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(increment, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(current + increment, Read<decimal>(row, "Requested"));
        Assert.Equal(allowance, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
    }
}
