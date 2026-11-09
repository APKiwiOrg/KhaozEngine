using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed class MapPrismValidationPeakTests
{
    [Fact]
    public void NestedCompiler_RecordsItsSubdivisionPeakAndExternalObligations()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 200000));
        using var active = admission.Enter();
        using var earlier = admission.Workspace();
        earlier.Reserve(100000);
        earlier.Release(100000);
        earlier.Reserve(100);
        using var retained = earlier.Transfer(100);
        using var window = admission.LimitPreparation(200000);
        window.ChargeExternal(200);
        var counts = new MapRoofPreparationCounts();
        using (var validation = new MapPrismValidationWorkspace(admission, counts, 0))
        {
            using var compiler = admission.BeginPreparation();
            _ = MapSurfaceCompiler.Compile(surface, patch);
        }
        // Validation header, two face compiler envelopes, one subdivision table and pre-existing obligations.
        Assert.Equal(32768 + 2 * 1280 + 16 + 100 + 200, counts.ValidationPeakBytes);
        Assert.Equal(100000, admission.PeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(100, admission.RetainedBytes);
    }

    [Fact]
    public void FilteredFootprint_RecordsBothRangesWhileEarlierRawPiecesAreLive()
    {
        var (view, footprint) = BoundFaceContextFixtures.EightByEight();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 200000));
        var counts = new MapRoofPreparationCounts();
        using (var validation = new MapPrismValidationWorkspace(admission, counts, 0))
        {
            using var candidate = MapPrismValidationWorkspace.RawCandidate(3);
            MapPrismValidationWorkspace.PublishRaw(candidate, 3);
            using var second = MapPrismValidationWorkspace.RawCandidate(3);
            MapPrismValidationWorkspace.PublishRaw(second, 3);
            // A filtered cell still visits this helper, then releases it without another validator observation.
            using var filtered = MapPrismValidationWorkspace.FootprintCell(view, footprint, 7);
        }
        // Two retained triangles, the eight-vertex kernel, one floor cell and four half-metre ceiling cells.
        Assert.Equal(32768 + 2 * (1024 + 3 * 256) + 4096 + 8 * 4096 + 5 * 256, counts.ValidationPeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }
}
