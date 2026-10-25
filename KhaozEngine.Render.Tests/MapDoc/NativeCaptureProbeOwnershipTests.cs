using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeCaptureProbeOwnershipTests
{
    [Fact]
    public void IndependentLightProbes_AdmitTheirLivePeakInsteadOfTheirCumulativeScenes()
    {
        const int count = 24;
        const long allowance = 32_768;
        // Each one-light scene owns 512 + 1144 + 128 + 256 + 128 bytes.
        // A probe fits 16 KiB. Live rows and both 37-slot indexes fit another 8 KiB.
        const long sceneBytes = 2168;
        Assert.True(count * sceneBytes > allowance);
        Assert.True(16_384 + count * 128 + 2 * 37 * 40 < allowance);
        using var fixture = NativeSceneFixtures.Create("empty");
        fixture.Document.Bounds = new() { MinX = 0, MinZ = 0, MaxX = 1536, MaxZ = 64 };
        fixture.Document.PlayableBounds = fixture.Document.Bounds;
        fixture.Document.BuildingIntent = new(1, [], [], Enumerable.Range(0, count).Select(i =>
            new MapLocalLightDoc(1, "probe-" + i, null, new(32 + 64 * i, 0, 32), Vector3.One, 1, .25f, true)).ToArray(), []);
        using var f = new ResidencyFixture(fixture.Document, fixture.Assets);
        var scope = MapCaptureDependencies.Scope(ResidencyFixture.Window(), new(0, -2, 0, 1536, 2, 64)) with
        { Channels = MapSceneChannels.Annotations, MotionPaddingMetres = 0 };
        var state = f.StateWithLimits(new(MaxPreparedBytes: allowance), scope);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        using (admission.Enter())
        {
            var bounds = state.Source.Data.Metadata.Single(m => m.Key.Id == "probe-0").Bounds;
            var probe = MapSceneAcquisition.Run(state.Source, MapCaptureDependencies.Scope(scope, bounds), admission);
            Assert.NotNull(probe.Scene);
            using var receipt = probe.Scene.PreparationOwnership!;
            Assert.Equal(sceneBytes, receipt.Bytes);
            Assert.InRange(admission.PeakBytes, sceneBytes, 16_384);
        }
        Assert.Equal(0, admission.RetainedBytes);
        var template = CaptureDependencyFixtures.Request();
        var request = template with
        {
            Source = state.Source,
            Closure = state.Closure,
            Scope = scope,
            Framing = new(new(WorldFrame.Origin, new(768, 8, 32)), new(WorldFrame.Origin, new(768, 0, 32)),
                true, 64, 1536, 64, .1f, 32),
            Policy = template.Policy with
            { Render = state.Policy, Lighting = new(Vector3.UnitY, Color.White, 1, new() { Mode = ShadowMode.Off }, 0) }
        };
        var result = MapCapturePreparation.Prepare(request);
        Assert.True(result.Status == MapSceneStatus.CompleteForScope,
            result.Capacity?.Message ?? string.Join(" | ", result.Findings));
        Assert.Equal(count * 2, result.Witness.RequiredScopes.Count);
        Assert.True(result.Witness.Scene.CompleteForScope);
        Assert.NotEmpty(result.Witness.LightingDigest);
    }
}
