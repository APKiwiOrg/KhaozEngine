using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeRoofHlodOutlineTests
{
    [Theory]
    [InlineData(MapRoofMode.AlwaysVisible, 30, "roof-a", 3, .5f)]
    [InlineData(MapRoofMode.AlwaysVisible, 40, "roof-a", 1, 0)]
    [InlineData(MapRoofMode.Auto, 40, "roof-a", 0, 0)]
    [InlineData(MapRoofMode.Auto, 40, "roof-b", 2, 0)]
    [InlineData(MapRoofMode.AlwaysHidden, 30, "roof-b", 0, 0)]
    public void SelectedRoofOutline_UsesOnlyItsEffectiveColourParts(MapRoofMode mode, float distance, string id, int count, float dissolve)
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans); var sink = new MapDrawRecorder(); plan.Register(sink);
        plan.Inventory.Placements.RecordRoofOutline(id, NativeRoofHlodRecordingTests.Frame(mode, distance), sink);
        var rows = sink.Snapshot().Draws; Assert.Equal(count, rows.Count);
        Assert.All(rows, d => { Assert.Equal(id, d.Resource.Key.OwnerId); Assert.Equal(dissolve, d.Rigid!.Value.Dissolve); });
        if (count == 3) Assert.Equal(new[] { 0f, 0f, 1f }, rows.Select(d => d.Rigid!.Value.DissolveComplement));
    }

    [Fact]
    public void ProxyOutline_UsesCachedFlattenedSourceWithoutIndividualMaterialCutouts()
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans); var sink = new MapDrawRecorder(); plan.Register(sink);
        plan.Inventory.Placements.RecordRoofOutline("roof-a", NativeRoofHlodRecordingTests.Frame(), sink);
        var set = sink.Snapshot(); var draw = Assert.Single(set.Draws);
        Assert.Equal("outline-hlod", draw.Resource.Key.PartId);
        var source = Assert.IsType<MapRigidResource>(Assert.Single(set.Resources, r => r.Reference == draw.Resource).Payload);
        Assert.Equal(default, source.Maps); Assert.Equal(6, source.Mesh.Vertices.Length);
    }

    [Fact]
    public void OptionalOutlineConsumer_UsesSharedMeshOutlinePrimitives()
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        using var host = new NativeUploadTestScene(); var sink = new MapScene3DSink(host.Scene);
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), sink, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        host.Scene.Begin(); var group = host.Scene.BeginMeshOutline(Color.White, 2);
        sink.RecordRoofOutline(Assert.Single(residency.AcceptedPlans), "roof-a", NativeRoofHlodRecordingTests.Frame(distance: 30), group);
        Assert.Equal(3, host.Scene.MeshOutlinePartCount);
        Assert.Equal(.5f, host.Scene.MeshOutlineDissolveAt(0, 0));
        host.Render();
    }
}
