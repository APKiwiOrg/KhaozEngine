using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapAcquisitionOwnershipTests
{
    [Fact]
    public void NativeInput_RetiresCopiedMetadataBeforeResolvingAndKeepsItsPatch()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 8, 8, 0);
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var diagnostics = admission.ObserveAcquisition();
        long retained = -1;
        diagnostics.Boundary = boundary =>
        {
            if (boundary.Stage == MapAcquisitionStage.ResolverGeometry && boundary.Entering)
                retained = boundary.RetainedBytes;
        };
        var scene = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission).Scene!;
        // Patch backing plus the native subset row, three hash slots and one descriptor reference.
        Assert.Equal(81 * 4 + 64 * 8 + 8 + 128 + 3 * 32 + 8, retained);
        Assert.Single(scene.SurfaceView.Witness.Present);
        scene.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedView_RetainsFactsWithoutTheConsumedPayload(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 32, 32, 125);
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        using var document = tiled ? NativeSceneFixtures.OpenTiled(fixture) : MapDocumentSource.FromDocument(fixture.Document);
        var source = tiled ? document.SceneSurfaces : MapDocumentSurfaceSource.Capture(fixture.Document);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        using var collector = admission.BeginPreparation();
        var view = MapScopedSurfaces.Acquire(source, MapSceneScopeTests.Window().Surfaces, []);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        Assert.InRange(admission.RetainedBytes, 12_556, 20_000);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.True(view.TryAcquiredPatch(patch.Key, out var captured, out _));
        Assert.Equal(125, captured!.Heights[0]);
    }

    [Fact]
    public void SequentialSeats_ReleaseQueryGeometryBeforeTheNextSelection()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 8, 8, 125);
        Array.Fill(patch.Cells, new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe));
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var diagnostics = admission.ObserveAcquisition();
        using var active = admission.Enter();
        using var collector = admission.BeginPreparation();
        for (int i = 0; i < 3; i++)
        {
            var answer = MapResolverV2.SeatSupport(new(new(1, 1), default, new(-1, 1)),
                new(MapSupportBindingKind.Surface, "floor", null, null, null, null), source, [], new());
            Assert.Equal(MapSupportStatus.Supported, answer.Status);
            Assert.Equal(1.25f, answer.WorldY);
            Assert.Equal(96L * (i + 1), admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
        }
        Assert.Equal(3, diagnostics.Callers[MapAcquisitionCaller.Seat].Scopes);
        Assert.Equal(2, diagnostics.Callers[MapAcquisitionCaller.Seat].RepeatedKeys);
        Assert.True(diagnostics.Stages[MapAcquisitionStage.Seat].WorkspaceHighWater > 0);
    }

    [Fact]
    public void ReleasedQueries_DoNotRefundCumulativeScopeWork()
    {
        using var fixture = NativeSceneFixtures.Create("terrain");
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        var admission = new MapSceneAdmission(new(MaxPatchScopes: 1, MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        using (admission.BeginPreparation())
            Assert.Equal(MapAcquireStatus.Complete, MapScopedSurfaces.Acquire(source, MapSceneScopeTests.Window().Surfaces, []).Status);
        Assert.Equal(0, admission.RetainedBytes);
        var error = Assert.Throws<MapSceneCapacityException>(() => MapScopedSurfaces.Acquire(source, MapSceneScopeTests.Window().Surfaces, []));
        Assert.Equal(new MapSceneCapacity("MaxPatchScopes", 2, 1), error.Capacity);
    }
}
