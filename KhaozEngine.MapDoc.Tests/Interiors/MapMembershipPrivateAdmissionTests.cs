using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipPrivateAdmissionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DensePrivateScratch_IsAdmittedWithColdOrSharedFootprintPages(bool shared)
    {
        using var fixture = MembershipAdmissionFixtures.Create();
        var patch = fixture.Document.Surfaces.Patches.Values.Single();
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new());
        using var window = admission.LimitPreparation(100000000, "window");
        var binding = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        var scope = MapMembershipIntervalTests.Scope();
        var complete = source.Acquire(scope with
        { Surfaces = scope.Surfaces with { LocalMin = new(-1, -65), LocalMax = new(65, 1) } }).Scene!;
        MapPreparationReservation? first = null;
        if (shared)
        {
            using var preparation = admission.BeginPreparation();
            using var build = new MapMembershipBuild(admission, preparation, new(), complete);
            var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
            var product = MapMembershipProductCache.Acquire(build, footprint);
            first = preparation.Complete(product.SharedBytes);
        }
        MapRoofEvaluationBuffer? second = null;
        try
        {
            MapRoofEvaluationBufferResult result;
            try { result = MapRoofEvaluationBuffer.Prepare(complete, admission); }
            catch (InvalidOperationException) when (ReportAdmission()) { throw; }
            Assert.NotNull(result.Buffer);
            second = result.Buffer;
            Assert.Equal(4096, second.Counts.UniqueTargetSlots);
            Assert.Equal(shared ? 1 : 0, second.Counts.FootprintCacheHits);
            long footprintBytes = MembershipAdmissionFixtures.Footprint();
            long privateBytes = MembershipAdmissionFixtures.Private();
            Assert.Equal(privateBytes, second.Counts.PrivateCapacityBytes);
            Assert.Equal(footprintBytes + 512, second.Counts.SharedCapacityBytes);
            Assert.Equal(privateBytes + footprintBytes + 512, second.RetainedBytes);
            Assert.Equal(privateBytes + footprintBytes + 512 + 256, admission.RetainedBytes);
            Assert.Equal(privateBytes + footprintBytes + 512 + 256, binding.Total);
            long intervalBound = 2 * (privateBytes + footprintBytes + 512 + 256) + 98304;
            Assert.True(second.Counts.Membership.Root.AdmittedPeakBytes <= intervalBound);
            Assert.True(second.Counts.Membership.BindingWindow!.AdmittedPeakBytes <= intervalBound);
        }
        finally { second?.PreparationOwnership?.Dispose(); first?.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);

        bool ReportAdmission()
        {
            var build = MapMembershipBuild.Current!;
            long direct = build.Product.InventoryBytes(new(ReferenceEqualityComparer.Instance), includeDependencies: false);
            output.WriteLine($"Private reachable {build.Counts.PrivateCapacityBytes}, private admitted {direct}, " +
                $"shared reachable {build.Counts.SharedCapacityBytes}, shared lease {build.Product.Bytes - direct}.");
            return true;
        }
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(65552)]
    public void PrivateArrayAdmission_CancellationPreservesTheEarlierSharedOwner(long arrayBytes)
    {
        using var fixture = MembershipAdmissionFixtures.Create();
        var scope = MapMembershipIntervalTests.Scope();
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(scope with
        { Surfaces = scope.Surfaces with { LocalMin = new(-1, -65), LocalMax = new(65, 1) } }).Scene!;
        var admission = new MapSceneAdmission(new());
        using var window = admission.LimitPreparation(100000000, "window");
        var binding = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        using var first = Warm(scene, admission);
        long baseline = MembershipAdmissionFixtures.Footprint() + 512 + 256;
        admission.RequestingBytes = requested =>
        {
            if (MapMembershipBuild.Current?.Counts.HeaderBytes > 0 &&
                requested - admission.RetainedBytes - admission.WorkspaceBytes == arrayBytes)
                throw new OperationCanceledException();
        };
        Assert.Throws<OperationCanceledException>(() => MapRoofEvaluationBuffer.Prepare(scene, admission));
        admission.RequestingBytes = null;
        Assert.Equal(baseline, admission.RetainedBytes);
        Assert.Equal(baseline, binding.Total);
        Assert.Equal(0, admission.WorkspaceBytes);
        first.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, binding.Total);
    }

    static MapPreparationReservation Warm(MapScopedScene scene, MapSceneAdmission admission)
    {
        using var preparation = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, preparation, new(), scene);
        var footprint = scene.Surfaces.Patches.Values.Single().Records.OfType<MapSpaceFootprint>().Single();
        var product = MapMembershipProductCache.Acquire(build, footprint);
        return preparation.Complete(product.SharedBytes);
    }
}
