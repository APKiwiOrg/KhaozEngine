using System;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipTightnessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("flat", 1, 2)]
    [InlineData("short", 1, 2)]
    [InlineData("long", 1, 2)]
    [InlineData("subdivided", 1, 18)]
    [InlineData("transformed", 1, 18)]
    [InlineData("mixed", 5, 10)]
    [InlineData("face-free", 1, 0)]
    [InlineData("opening", 2, 2)]
    [InlineData("portal", 1, 2)]
    [InlineData("prism", 2, 4)]
    [InlineData("fragmented", 2, 4)]
    [InlineData("clipped", 2, 4)]
    public void WholeMembership_UsesIndependentSimultaneousInventoryForBothDomains(string shape, long slots, long faces)
    {
        using var native = MapMembershipIntervalFixtures.Create(shape);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        Assert.NotNull(scene);
        long inventory = MapMembershipInventoryModel.SimultaneousSubtotal(scene);
        long bound = checked(2 * inventory + MapMembershipInventoryModel.FixedKernelAllowance);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000000));
        using var empty = NativeSceneFixtures.Create("empty");
        var emptyScene = NativeSceneFixtures.Open(empty, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var prior = MapRoofEvaluationBuffer.Prepare(emptyScene, admission).Buffer!;
        // Empty final graph is the header less 2816 bytes of preparation-only control allowance.
        long priorBytes = MapMembershipIntervalTests.EmptyHeader - 2816;
        Assert.Equal(priorBytes, prior.RetainedBytes);
        using var historical = admission.Workspace();
        historical.Reserve(50000000); historical.Release(50000000);
        using var window = admission.LimitPreparation(100000000, "MembershipWindow", 73);
        window.ChargeExternal(29);
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission, true);
        Assert.NotNull(result.Counts);
        var counts = result.Counts;
        output.WriteLine($"{shape}: inventory subtotal {inventory}, kernel {MapMembershipInventoryModel.FixedKernelAllowance}, " +
            $"factor-two limit {bound}, root {counts.Membership.Root.AdmittedPeakBytes}, " +
            $"window {counts.Membership.BindingWindow?.AdmittedPeakBytes}, validation {counts.Validation.Root.AdmittedPeakBytes}, " +
            $"construction {counts.Construction.Root.AdmittedPeakBytes}");
        try
        {
            Assert.NotNull(result.Buffer);
            Assert.Equal(MapMembershipInventoryModel.Header(scene), counts.HeaderBytes);
            Assert.False(counts.CompleteAggregatePredictionAvailable);
            Assert.Equal(slots, counts.UniqueTargetSlots);
            Assert.Equal(faces, counts.PhysicalFaces);
            if (shape == "fragmented")
            {
                Assert.Equal(281531, inventory);
                Assert.Equal(66, counts.ValidationRawPieces);
                Assert.Equal(261, counts.ValidationVertices);
            }
            Assert.Equal(priorBytes + 29, counts.Membership.Root.BaselineBytes);
            Assert.Equal(102, counts.Membership.BindingWindow!.BaselineBytes);
            Assert.Equal(priorBytes - 73,
                counts.Membership.Root.AdmittedPeakBytes - counts.Membership.BindingWindow.AdmittedPeakBytes);
            Assert.Equal(0m, counts.Membership.Root.RefusedBytes);
            Assert.Equal(new[] { counts.Header, counts.Validation, counts.Planner, counts.Construction, counts.Publication }
                .Max(stage => stage.Root.AdmittedPeakBytes), counts.Membership.Root.AdmittedPeakBytes);
            Assert.True(counts.Membership.Root.AdmittedPeakBytes <= bound,
                $"Root {counts.Membership.Root.AdmittedPeakBytes} exceeds 2 * {inventory} + {MapMembershipInventoryModel.FixedKernelAllowance}.");
            Assert.True(counts.Membership.BindingWindow.AdmittedPeakBytes <= bound);
            Assert.Equal(50000000 + priorBytes, admission.PeakBytes);
        }
        finally
        {
            result.Buffer?.PreparationOwnership?.Dispose();
            prior.PreparationOwnership!.Dispose();
            scene.PreparationOwnership?.Dispose();
        }
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void EqualSparseSelection_DoesNotPredictNormalizedCoefficientOutput()
    {
        var shortCounts = Prepare("short");
        var longCounts = Prepare("long");
        Assert.Equal(1, shortCounts.UniqueTargetSlots);
        Assert.Equal(2, shortCounts.PhysicalFaces);
        Assert.Equal(1, shortCounts.BoundAssociations);
        Assert.Equal(shortCounts.UniqueTargetSlots, longCounts.UniqueTargetSlots);
        Assert.Equal(shortCounts.PhysicalFaces, longCounts.PhysicalFaces);
        Assert.Equal(shortCounts.BoundAssociations, longCounts.BoundAssociations);
        Assert.True(longCounts.NormalizedCoefficientBytes > shortCounts.NormalizedCoefficientBytes);
    }

    [Fact]
    public void EqualSparseSelection_DoesNotPredictHeightClippedValidationOutput()
    {
        var flat = Prepare("prism");
        var clipped = Prepare("clipped");
        Assert.Equal(flat.UniqueTargetSlots, clipped.UniqueTargetSlots);
        Assert.Equal(flat.PhysicalFaces, clipped.PhysicalFaces);
        Assert.Equal(flat.BoundAssociations, clipped.BoundAssociations);
        Assert.NotEqual(flat.ValidationRawPieces, clipped.ValidationRawPieces);
        Assert.NotEqual(flat.ValidationVertices, clipped.ValidationVertices);
    }

    static MapRoofPreparationCounts Prepare(string shape)
    {
        using var native = MapMembershipIntervalFixtures.Create(shape);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var result = MapRoofEvaluationBuffer.Prepare(scene, new(new(MaxPreparedBytes: 100000000)), true);
        Assert.NotNull(result.Buffer);
        result.Buffer.PreparationOwnership!.Dispose();
        return result.Counts!;
    }
}
