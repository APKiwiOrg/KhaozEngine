using System.Linq;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapNativeAcquisitionOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedNativeClosure_KeepsOnlySelectedCopies(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var common = PrefabFixtures.Patch("common", 32, 32, 125);
        common.Key = new("common", 10, 0);
        common.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Exterior, null, null, [], [], [], []));
        var inputs = Enumerable.Range(0, 3).Select(i => PrefabFixtures.Patch("floor-" + i, 32, 32, 125)).ToArray();
        for (int i = 0; i < inputs.Length; i++)
        {
            var patch = inputs[i];
            patch.Key = new(patch.Key.SurfaceId, i * 2, 0);
            patch.Records.Add(new MapSpaceFootprint("domain-" + i, new("room", common.Key), patch.Key, [0],
                new(MapBoundKind.SupportFloor, patch.Key.SurfaceId, null), new(MapBoundKind.OpenTop, null, null)));
        }
        foreach (var patch in inputs.Append(common))
        {
            fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface(patch.Key.SurfaceId, MapSurfaceRole.SupportFloor));
            fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        }
        var source = NativeSceneFixtures.Open(fixture, tiled);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 180_000));
        var diagnostics = admission.ObserveAcquisition();
        var boundaries = new List<MapAcquisitionBoundary>();
        diagnostics.Boundary = boundary =>
        {
            if (boundary.Stage == MapAcquisitionStage.NativeQuery && !boundary.Entering) boundaries.Add(boundary);
        };
        using var active = admission.Enter();
        using var collector = admission.BeginPreparation();
        var captured = MapSceneAcquisition.AcquireNative(source.Data, inputs.Select(p => p.Key).ToHashSet(), admission, new());
        Assert.Equal(4, captured.Patches.Count);
        long expected = 128 + 128L * captured.Refs.Count + 8L * captured.Refs.Capacity + 64L * captured.Patches.Count;
        foreach (var patch in captured.Patches.Values)
            expected += 4L * patch.Heights.Length + 8L * patch.Cells.Length + 8L * patch.Presence.Length +
                8L * patch.Records.Capacity + patch.Records.OfType<MapSpaceFootprint>().Sum(f => 4L * f.SlotCells.Count);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(new long[] { 25_420, 38_132, 50_844 }, boundaries.Select(b => b.RetainedBytes));
        Assert.Equal(new[] { 2, 3, 4 }, boundaries.Select(b => b.Receipts));
        var calls = diagnostics.Callers[MapAcquisitionCaller.NativeKey];
        Assert.Equal(3, calls.Scopes);
        Assert.Equal(2, calls.RepeatedSources);
        Assert.Equal(2, calls.RepeatedKeys);
    }

}
