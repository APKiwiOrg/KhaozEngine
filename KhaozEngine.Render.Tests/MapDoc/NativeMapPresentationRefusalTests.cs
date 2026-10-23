using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapPresentationRefusalTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(false, 10)]
    [InlineData(false, 17)]
    [InlineData(true, 0)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(true, 10)]
    [InlineData(true, 17)]
    public void TruncatedReplacement_RefusesOnceAndKeepsAcceptedDraws(bool selection, int length)
    {
        using var fixture = ResidencyFixture.Placement(NativeRenderAssetFixtures.Presentation());
        fixture.Document.Placements = [new() { Id = "visible", AssetId = "explicit", Kind = "prop", Y = 0, NumericId = 1 }];
        var state = fixture.State(ResidencyFixture.Window());
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var acceptedState = residency.DesiredState;
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var draws = ResidencyFixture.Draw(residency).Draws;
        Assert.NotEmpty(draws);
        int uploads = device.Begins;

        byte[] box = [0x4c, 0x43, 0x45, 0x4b, 1, 4, 0, 0, 0x80, 0x3f, 0, 0, 0x80, 0x3f, 0, 0, 0x80, 0x3f];
        var closure = NativeRenderAssetFixtures.Presentation(new Dictionary<string, byte[]>
        { [selection ? "selection" : "explicit.coll"] = box[..length] });
        using var replacement = new ResidencyFixture(fixture.Document, closure);
        replacement.Document.NativeAssets = closure.Roots.ToList();
        residency.RequestReplacement(replacement.State(ResidencyFixture.Window())); scheduler.RunAll();

        Assert.Equal(MapSceneStatus.Invalid, residency.Update().Transition.Status);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.Null(residency.CurrentTransition.PendingWitness);
        Assert.Same(acceptedState, residency.DesiredState);
        Assert.Equal(draws, ResidencyFixture.Draw(residency).Draws);
        Assert.Equal(uploads, device.Begins); Assert.Equal(0, device.Releases);
        Assert.Equal(MapSceneStatus.Invalid, residency.Update().Transition.Status);
        Assert.Equal(0, residency.Lifetime.WorkerCount);
        Assert.Equal(draws, ResidencyFixture.Draw(residency).Draws);
    }
}
