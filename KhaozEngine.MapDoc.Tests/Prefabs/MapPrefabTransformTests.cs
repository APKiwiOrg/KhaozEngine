using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed partial class MapPrefabTransformTests
{
    [Theory]
    [InlineData(0.8f, 7.3999996f)]
    [InlineData(1.2f, 8.6f)]
    public void AllChildResources_UseOneTransformAndSharedEdge(float scale, float upperY)
    {
        var f = PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f));
        var effective = PrefabFixtures.Effective(f);
        var composed = MapEffectiveTransform.Compose(MapEffectiveTransform.From(f.Document.Prefabs!.Instances.Single().Transform),
            new MapTransform(Vector3.Zero, 0, 1));
        var transform = new MapResourceTransform(composed);
        var expected = transform.Point(new(new(4, 1), new(3, 1), new(-4, 1)));
        foreach (var corner in new[] { PrefabFixtures.SharedFloorCorner(effective),
            PrefabFixtures.SharedRoomCorner(effective), PrefabFixtures.SharedWaterCorner(effective) })
            Assert.Equal((expected.X, expected.Z), (corner.X, corner.Z));
        Assert.Equal(transform.Height(new(3, 1)), PrefabFixtures.StoreyUpper(effective));
        Assert.Equal(upperY, PrefabFixtures.StoreyUpper(effective).ToSingle());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecomposedOrigin_EqualsDirectGeometryAtNearAndFarSites(bool far)
    {
        var f = PrefabFixtures.Decomposed(far);
        var (direct, offset) = PrefabFixtures.DecomposedCorners(PrefabFixtures.Effective(f));
        Assert.Equal(direct, offset);
        if (far)
        {
            Assert.Equal(new MapExactValue(31460408117, 1048576), direct.X);
            Assert.Equal(new MapExactValue(-31463533353, 1048576), direct.Z);
            Assert.NotEqual(30002.982421875, direct.X.ToDouble());
            Assert.NotEqual(-30005.962890625, direct.Z.ToDouble());
        }
    }

    [Fact]
    public void FarFrame_SharedEdgesAgreeWithoutAbsoluteFloatRoundTrip()
    {
        var effective = PrefabFixtures.Effective(PrefabFixtures.Decomposed(true));
        var (direct, offset) = PrefabFixtures.DecomposedCorners(effective);
        var frame = WorldFrame.Nearest(30000.25f, -29999.75f);
        var kernel = new MapResourceTransform(new(0, 0, 0, 0, 1));
        Assert.Equal(kernel.ToFrame(direct, frame), kernel.ToFrame(offset, frame));
        Assert.Equal((float)(direct.X.ToDouble() - (long)frame.X * (long)WorldFrame.Grid), kernel.ToFrame(direct, frame).X);
        Assert.NotEqual((float)direct.X.ToDouble() - (long)frame.X * (long)WorldFrame.Grid, kernel.ToFrame(direct, frame).X);
    }

    [Theory]
    [InlineData("owner-placement", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("owner-transform", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
    [InlineData("floor", 3.5f, 0.75f, -3.25f, 4f, 2f, -4f)]
    [InlineData("light", 4f, 0.5f, -4f, 4.75f, 2.5f, -5.25f)]
    public void MaterializedTransformOverrides_MoveCapturedFramesExactly(string kind,
        float fx, float fy, float fz, float lx, float ly, float lz)
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        string before = MapDocumentFile.SaveText(f.Document);
        byte[] bytes = f.Assets.GetResource("hut").Bytes.ToArray();
        var changed = PrefabFixtures.WithMaterializedOverride(f, PrefabFixtures.MaterializedTransformOverride(kind));
        var r = PrefabFixtures.Effective(changed);
        var (floor, light) = PrefabFixtures.CapturedCoordinates(r);
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(fx), MapExactValue.FromSingle(fy), MapExactValue.FromSingle(fz)), floor);
        Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(lx), MapExactValue.FromSingle(ly), MapExactValue.FromSingle(lz)), light);
        Assert.Equal(new[] { "captured-floor" }, PrefabFixtures.CapturedSupportOwners(r));
        Assert.Equal(new[] { "captured-light" }, r.BuildingIntent.Lights.Select(x => x.Id));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(bytes, f.Assets.GetResource("hut").Bytes.ToArray());
        Assert.Equal(f.Document.NumericIdHighWaterMark, changed.Document.NumericIdHighWaterMark);
        var reloaded = new PrefabFixture(MapDocumentFile.LoadText(MapDocumentFile.SaveText(changed.Document)), f.Assets);
        Assert.Equal((floor, light), PrefabFixtures.CapturedCoordinates(PrefabFixtures.Effective(reloaded)));
        Assert.NotEqual(PrefabFixtures.Effective(f).Digest, r.Digest);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(-450f)]
    public void MissingYChild_IsSeatedOnceOnTheEffectiveFloor(float parentY)
    {
        var f = PrefabFixtures.SupportBoundChild(parentY);
        string before = MapDocumentFile.SaveText(f.Document);
        var r = PrefabFixtures.Effective(f);
        var item = r.Placements.Single(p => p.PlacementId == "low-item");
        Assert.Equal(parentY, item.Transform.Position.Y);
        Assert.Equal(parentY, r.PlacementSupports.Single(p => p.PlacementId == "low-item").Support.WorldY);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }

    [Theory]
    [InlineData(false, 0.8f)]
    [InlineData(false, 1.2f)]
    [InlineData(true, 0.8f)]
    [InlineData(true, 1.2f)]
    public void PlacedSlope_SupportMembershipAndBoundsSucceedWithWideIntermediates(bool far, float scale)
    {
        var f = PrefabFixtures.Sloped(far, scale);
        var r = PrefabFixtures.Effective(f);
        var view = r.CompleteView();
        var compiled = MapEffectiveGeometry.Patch(view, new("a/floor", 0, 0));
        var face = compiled.Faces.First();
        var t = compiled.ExactTriangle(face);
        var x = t.A.X.Add(t.B.X).Add(t.C.X).Divide(new(3, 1));
        var z = t.A.Z.Add(t.B.Z).Add(t.C.Z).Divide(new(3, 1));
        var expected = t.A.Y.Add(t.B.Y).Add(t.C.Y).Divide(new(3, 1));
        Assert.Equal(expected, MapSubdividedTriangle.Height(t, x, z));
        var world = new MapExactPoint(x, expected.Add(new(1, 1)), z);
        Assert.Equal(MapSupportStatus.Supported, new MapSupportQuery(view).SampleSurfaceExact(world, "a/floor").Status);
        Assert.Equal(MapMembershipStatus.Resolved, new MapSpaceMembership(view).QueryExact(world).Status);
        Assert.Equal(MapExactValue.FromSingle(far ? -450f : 5f), MapSpaceIntervals.AtExact(view, world).Lower);
        Assert.True(face.Normal.Y > 0.9f);
        var prism = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapPrismDomain>().Single();
        Assert.True(MapPrismGeometry.Compile(view, prism).Contains(new(x, z)));
        Assert.Equal(far ? -450d : 5d, MapPrismGeometry.Bounds(view, prism).MinY);
        Assert.Equal(PrefabFixtures.StoreyUpper(r).ToDouble(), MapPrismGeometry.Bounds(view, prism).MaxY);
    }
    [Fact]
    public void ExactQueries_FinalDenominatorRefusesWithoutIntermediateThrow()
    {
        var f = PrefabFixtures.Sloped(false, 1.2f);
        var instance = f.Document.Prefabs!.Instances.Single() with { Transform = new(Vector3.Zero, 0.371f, 1.2f) };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        f.Document.NativeResourceTransforms = f.Document.NativeResourceTransforms!.Select(t => t with { Transform = t.Transform with { X = 0, Y = 0, Z = 0 } }).ToArray();
        var view = PrefabFixtures.Effective(f).CompleteView();
        var point = new MapExactPoint(new(1, long.MaxValue), new(1, 1), new(-1, long.MaxValue));
        Assert.Equal(MapSupportStatus.NotRepresentable, new MapSupportQuery(view).SampleSurfaceExact(point, "a/floor").Status);
        var membership = new MapSpaceMembership(view).QueryExact(point);
        Assert.Equal(MapMembershipStatus.NotRepresentable, membership.Status);
        Assert.Equal(point, membership.ExactPoint);
    }

    [Fact]
    public void MaterializedRemoveRestoreAndUndo_RecomputeFromCapturedBase()
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var transform = PrefabFixtures.MaterializedTransformOverride("owner-transform");
        var moved = PrefabFixtures.WithMaterializedOverride(f, transform);
        var live = PrefabFixtures.Effective(moved);
        var removed = PrefabFixtures.WithMaterializedOverride(f, new MapPrefabRemoveOverride(transform.Child, transform));
        var instance = removed.Document.Prefabs!.Instances.Single();
        removed.Document.Prefabs = new(1, 1, new[] { instance with
        { Bindings = new(instance.Bindings.Entries.Select(b => b.Child == transform.Child ? b with { Removed = true } : b).ToArray()) } }, []);
        var absent = PrefabFixtures.Effective(removed);
        Assert.Empty(absent.Placements);
        Assert.Empty(absent.Surfaces.Refs);
        Assert.Empty(absent.BuildingIntent.Lights);
        Assert.Equal(live.Digest, PrefabFixtures.Effective(PrefabFixtures.WithMaterializedOverride(f, transform)).Digest);
        Assert.Equal(absent.Digest, PrefabFixtures.Effective(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(removed.Document)), f.Assets)).Digest);
        var original = PrefabFixtures.CapturedCoordinates(PrefabFixtures.Effective(f));
        Assert.Equal(new MapExactPoint(new(4, 1), new(1, 2), new(-4, 1)), original.Floor);
        Assert.Equal(new MapExactPoint(new(4, 1), new(2, 1), new(-4, 1)), original.Light);
        var reconciled = MapPrefabMaterializationOverrides.Reconcile(f.Assets.GetPrefab("hut"), new[] { PrefabFixtures.MaterializedTransformOverride("floor") });
        Assert.Equal(new MapTransform(Vector3.Zero, 0, 1), reconciled.EffectiveAsset.Children.Single(c => c.Key.Key == "captured-floor").LocalTransform);
        Assert.Equal(f.Document.NumericIdHighWaterMark, removed.Document.NumericIdHighWaterMark);
        Assert.Equal(f.Document.Prefabs!.Instances.Single().Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)), instance.Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)));
    }

    [Fact]
    public void MaterializedOverride_CannotHideCorruptCapturedPin()
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var asset = f.Assets.GetPrefab("hut");
        asset = asset with
        {
            MaterializedAttachments = asset.MaterializedAttachments.Select(r => r with
            { Children = r.Children.Select(c => c with { SourceDigest = new string('0', 64) }).ToArray() }).ToArray()
        };
        Assert.Throws<MapDocumentException>(() => MapPrefabMaterializationOverrides.Reconcile(asset, new[] { PrefabFixtures.MaterializedTransformOverride("floor") }));
    }

    [Fact]
    public void MaterializedValueOverride_PreservesBinary64FramesAndRefreshesDescriptorDigest()
    {
        var f = PrefabFixtures.MaterializedOverrideFixture();
        var asset = f.Assets.GetPrefab("hut");
        asset = asset with
        {
            MaterializedAttachments = asset.MaterializedAttachments.Select(r => r with
            { PlacementFrame = r.PlacementFrame with { X = 4.0000000001234 } }).ToArray()
        };
        var floor = (MapPrefabSurfaceChild)asset.Children.Single(c => c.Key.Key == "captured-floor");
        var patch = floor.Patches.Single().Clone(); Array.Fill(patch.Heights, 75);
        var reconciled = MapPrefabMaterializationOverrides.Reconcile(asset, new MapPrefabOverride[] { new MapPrefabReplaceOverride(floor.Key, floor with { Patches = new[] { patch } }) });
        var row = reconciled.EffectiveAsset.MaterializedAttachments.Single(r => r.Kind == MapPrefabAttachmentKind.Support);
        Assert.Equal(4.0000000001234, row.PlacementFrame.X);
        Assert.Equal(asset.MaterializedAttachments[0].Children[0].OwnerLocalPose, row.Children[0].OwnerLocalPose);
        Assert.NotEqual(asset.MaterializedAttachments[0].Children[0].SourceDigest, row.Children[0].SourceDigest);
    }

    [Theory]
    [InlineData(false, 12.98320505044867, -9.33866186836002)]
    [InlineData(true, 30002.98320505045, -30005.96366186836)]
    public void CompositionQuantumAndBackendErrors_AreIndependentAndBounded(bool far, double goldenX, double goldenZ)
    {
        var f = PrefabFixtures.Decomposed(far);
        var parent = MapEffectiveTransform.From(f.Document.Prefabs!.Instances.Single().Transform);
        var composed = MapEffectiveTransform.Compose(parent, new MapTransform(new(4, 0, -4), 0, 1));
        const double u = 1.1102230246251565e-16;
        double gamma = 8 * u / (1 - 8 * u);
        double arithmeticBound = gamma * (Math.Abs(parent.X) + Math.Abs(parent.Z) + 8 * parent.Scale);
        var (sin, cos) = Math.SinCos(parent.YawRadians);
        double libmDeviation = Math.Max(Math.Abs(sin - 0.3625475707927528451325483864631543), Math.Abs(cos - 0.931965267009599378742012328460918));
        Assert.InRange(libmDeviation, 0, 2 * u);
        Assert.InRange(arithmeticBound + libmDeviation * 8 * parent.Scale, 0, 1e-9);
        Assert.InRange(Math.Abs(composed.X - goldenX), 0, arithmeticBound + libmDeviation * 8 * parent.Scale);
        Assert.InRange(Math.Abs(composed.Z - goldenZ), 0, arithmeticBound + libmDeviation * 8 * parent.Scale);
        var r = PrefabFixtures.Effective(f);
        var (point, offset) = PrefabFixtures.DecomposedCorners(r);
        Assert.Equal(point, offset);
        Assert.InRange(Math.Abs(point.X.ToDouble() - goldenX), 0, 0.000000476837158203125 + arithmeticBound);
        Assert.InRange(Math.Abs(point.Z.ToDouble() - goldenZ), 0, 0.000000476837158203125 + arithmeticBound);
        var backend = r.Placements.Single(p => p.PlacementId == "a/deck").Transform.Position;
        double halfUlp = (MathF.BitIncrement(backend.X) - backend.X) / 2d;
        Assert.InRange(Math.Abs(backend.X - composed.X), 0, halfUlp);
        if (far) Assert.True(Math.Abs(backend.X - composed.X) > 0.000000476837158203125);
    }

    [Fact]
    public void HeightSeam_QuantumAndBinary32ErrorsRemainSeparateAtInstalledLimit()
    {
        var kernel = new MapResourceTransform(new(0, 999999, 0, 0, 1.2f));
        var height = kernel.Height(new(3, 10));
        var quantized = new MapExactValue(1048575328911, 1048576);
        const double golden = 999999.36000001430511474609375;
        Assert.InRange(Math.Abs(quantized.ToDouble() - golden), 0, 0.000000476837158203125);
        Assert.Equal(999999.375f, height.ToSingle());
        Assert.InRange(Math.Abs(height.ToDouble() - quantized.ToDouble()), 0, 0.03125);
    }

}
