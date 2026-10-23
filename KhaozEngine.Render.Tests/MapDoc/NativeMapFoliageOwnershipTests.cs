using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapFoliageOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingWindows_ShareOneInstanceUploadAndSubmissionUntilLastMembership(bool generated)
    {
        using var fixture = generated ? ResidencyFixture.Foliage() : ExplicitPlants();
        var state = State(fixture, generated);
        var a = state.Windows.Single();
        var b = a with { DrawBounds = a.DrawBounds with { MinX = -1, MaxX = 5 } };
        state = state with { Windows = [a, b] };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);

        Assert.Single(device.Live.Values, k => k.PartId == "instances");
        Assert.Single(ResidencyFixture.Draw(residency).Draws);
        var registrations = residency.AcceptedPlans.SelectMany(p => p.Resources).Where(r => r.Payload is MapFoliageResource).ToArray();
        Assert.Equal(2, registrations.Length);
        Assert.All(registrations, r => Assert.Equal(registrations[0].PayloadDigest, r.PayloadDigest));
        Assert.True(residency.TryGetHandle(registrations[0].Reference, out var first));
        Assert.True(residency.TryGetHandle(registrations[1].Reference, out var second));
        Assert.Equal(first, second);
        int uploads = device.Begins;

        residency.SetWindows([b]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(uploads, device.Begins);
        Assert.Equal(0, device.Releases);
        Assert.Single(ResidencyFixture.Draw(residency).Draws);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Empty(device.Live); Assert.Equal(uploads, device.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAnotherGroup_DoesNotReplaceUnchangedInstanceOwner(bool generated)
    {
        using var fixture = generated ? ResidencyFixture.Foliage() : ExplicitPlants();
        if (generated) fixture.Document.Placements = [new() { Id = "other", AssetId = "A", Kind = "plant", X = 6, Y = 0, NumericId = 1 }];
        else fixture.Document.Placements.Add(new() { Id = "other", AssetId = "A", Kind = "plant", X = 6, Y = 0, NumericId = 2 });
        fixture.Document.NumericIdHighWaterMark = 2;
        var state = State(fixture, generated);
        var scope = state.Windows.Single() with { DrawBounds = new(-4, -500, -4, 12, 500, 4) };
        state = state with { Windows = [scope] };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var unchanged = Assert.Single(residency.AcceptedPlans.Single().Resources,
            r => r.Payload is MapFoliageResource && r.Reference.Key.OwnerId != "other");
        Assert.True(residency.TryGetHandle(unchanged.Reference, out var handle));
        int uploads = device.Begins;

        fixture.Document.Placements.Single(p => p.Id == "other").X = 7;
        var replacement = State(fixture, generated) with { Windows = [scope] };
        residency.RequestReplacement(replacement); ResidencyFixture.Complete(residency, scheduler);
        var current = Assert.Single(residency.AcceptedPlans.Single().Resources,
            r => r.Payload is MapFoliageResource && r.Reference.Key.OwnerId == unchanged.Reference.Key.OwnerId);
        Assert.Equal(unchanged.PayloadDigest, current.PayloadDigest);
        Assert.True(residency.TryGetHandle(current.Reference, out var retained));
        Assert.Equal(handle, retained);
        Assert.Equal(uploads + 1, device.Begins);
        Assert.Equal(1, device.Releases);
    }

    static ResidencyFixture ExplicitPlants()
    {
        var fixture = ResidencyFixture.Placement(NativeRenderAssetFixtures.Create("foliage"));
        fixture.Document.Placements = [new() { Id = "plant", AssetId = "A", Kind = "plant", Y = 0, NumericId = 1 }];
        return fixture;
    }

    static MapRenderSourceState State(ResidencyFixture fixture, bool generated)
    {
        var state = generated ? fixture.FoliageState() : fixture.State(ResidencyFixture.Window());
        var foliage = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale }), fixture.Closure.Assets.ToDictionary(a => a.Id,
            a => new MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, 0)));
        return state with
        {
            Windows = state.Windows.Select(s => s with { Channels = s.Channels | MapSceneChannels.Placements }).ToArray(),
            Policy = ResidencyFixture.Policy(foliage: foliage, animated: new HashSet<string> { "A" })
        };
    }
}
