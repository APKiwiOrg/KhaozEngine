using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapPrecisionTests
{
    [Fact]
    public void FarOriginHlodRenderAndPick_MeetPrecisionContract()
    {
        var realization = NativeOriginRealizationFixtures.Create(); using var fixture = realization.Scene;
        Assert.Equal(FarFoliageGenerationFixtures.Pins(32000), FarFoliageGenerationFixtures.Capture(realization.Samples));
        var frozenFarSampleKeys = realization.Samples.Samples.Select(s => s.Key).ToArray();
        var frozenFarMatrixBits = realization.Samples.Samples.SelectMany(s => FarFoliageGenerationFixtures.MatrixBits(s.Transform)).ToArray();
        using var host = new NativeUploadTestScene(); host.Scene.CameraOverride = realization.Framing.Camera; var state = NativeOriginRealizationFixtures.State(realization);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency);
        var ready = view.SettleForCapture(1024);
        Assert.Equal(KhaozEngine.MapDoc.Scene.MapSceneStatus.CompleteForScope, ready.Transition.Status);
        Assert.Equal(0, ready.Pending);
        var plan = Assert.Single(residency.AcceptedPlans);
        Assert.NotEmpty(plan.Inventory.Placements.Clusters);
        Assert.NotEmpty(plan.Inventory.Water.Cells);
        var generated = plan.Inventory.Foliage.GeneratedIdentities.ToArray();
        Assert.Equal(frozenFarSampleKeys, generated);
        var point = realization.PickOrigin;
        var frame = new MapFrameState(point.Frame, point, MapRoofMode.AlwaysVisible, 12, 17, point);
        var recorder = new MapDrawRecorder(); residency.Record(frame, recorder); var draws = recorder.Snapshot();
        var registered = Assert.Single(draws.Resources, r => r.Payload is MapFoliageResource);
        var payload = (MapFoliageResource)registered.Payload;
        Assert.Equal(frozenFarMatrixBits, payload.WorldTransforms.ToArray().SelectMany(FarFoliageGenerationFixtures.MatrixBits));
        Assert.Equal(realization.Samples.Samples.Select(s => s.ThinningRank), payload.Ranks.ToArray());
        Assert.True(residency.TryGetHandle(registered.Reference, out var handle));
        var packed = host.Scene.ResourceOf(new(handle.Value)).PreparedFoliage!;
        var bytes = packed.PackedInstances.Span;
        double maxAdditionalFoliageSubmissionErrorMetres = 0, maxExactToLocalErrorMetres = 0;
        MapFoliageSampleKey[]? originAKeys = null;
        foreach (var origin in new[] { NativeOriginRealizationFixtures.OriginA, NativeOriginRealizationFixtures.OriginB })
        {
            host.Scene.RenderOrigin = new((float)origin.X, (float)origin.Y, (float)origin.Z); host.Scene.CameraCut();
            host.Scene.Begin(); view.Draw(frame); host.Render();
            Assert.Equal(new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z), host.Scene.CurrentFrameView.RenderOrigin);
            var originalBits = payload.WorldTransforms.ToArray().SelectMany(FarFoliageGenerationFixtures.MatrixBits).ToArray();
            Assert.Equal(frozenFarMatrixBits, originalBits);
            var originKeys = plan.Inventory.Foliage.GeneratedIdentities.ToArray();
            if (originAKeys is null) { originAKeys = originKeys; Assert.Equal(frozenFarSampleKeys, originAKeys); }
            else Assert.Equal(originAKeys, originKeys);
            for (int i = 0; i < packed.ExpandedParts.Count; i++)
            {
                var sample = realization.Samples.Samples[packed.ExpandedParts[i].SourceRow];
                var original = FarFoliageGenerationFixtures.MatrixBits(sample.Transform).ToArray();
                for (int c = 0; c < 16; c++)
                {
                    float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 96 + c * 4)..]);
                    if (c is < 12 or 15) Assert.Equal(original[c], BitConverter.SingleToInt32Bits(value));
                    else
                    {
                        double anchor = c == 12 ? packed.Anchor.X : c == 13 ? packed.Anchor.Y : packed.Anchor.Z;
                        double offset = c == 12 ? origin.X : c == 13 ? origin.Y : origin.Z;
                        float reference = (float)((double)BitConverter.Int32BitsToSingle(original[c]) - offset);
                        float submitted = value + (float)(anchor - offset);
                        maxAdditionalFoliageSubmissionErrorMetres = Math.Max(maxAdditionalFoliageSubmissionErrorMetres, Math.Abs((double)submitted - reference));
                    }
                }
            }
            var renderOrigin = host.Scene.CurrentFrameView.RenderOrigin;
            NativeMapGoldenAssertions.FarLightDraw(draws.Draws, renderOrigin);
            NativeMapGoldenAssertions.FarLightSubmitted(host.Scene, renderOrigin);
            var hit = view.Pick(new(point, -Vector3.UnitY, 64), MapPresentationChannel.Physical, frame);
            Assert.NotNull(hit); Assert.Equal(6, hit.Value.Distance, 8); Assert.Equal(new(.375f, 0, .375f), hit.Value.LocalPoint);
        }
        var pose = NativeMapPlacements.Pose(plan.Scene, plan.Scene.Placements.Single(p => p.PlacementId == "yawed-prop"));
        foreach (var cluster in plan.Inventory.Placements.Clusters)
        {
            var vertices = cluster.MergedMesh.Vertices; var source = cluster.Request.Layer.HlodSourceMeshes!["B"].Vertices;
            var (s, c) = Math.SinCos(.371);
            for (int i = 0; i < source.Length; i++)
            {
                var p = source[i].Position;
                double ex = 32000.375 + 1.2 * (c * p.X + s * p.Z), ey = 2.5 + 1.2 * p.Y, ez = -31999.625 + 1.2 * (-s * p.X + c * p.Z);
                var a = cluster.Request.Anchor; var actual = vertices[i].Position;
                maxExactToLocalErrorMetres = Math.Max(maxExactToLocalErrorMetres, Math.Abs(actual.X - (ex - a.X)));
                maxExactToLocalErrorMetres = Math.Max(maxExactToLocalErrorMetres, Math.Abs(actual.Y - (ey - a.Y)));
                maxExactToLocalErrorMetres = Math.Max(maxExactToLocalErrorMetres, Math.Abs(actual.Z - (ez - a.Z)));
            }
        }
        Assert.Equal(.371, pose.YawRadians); Assert.Equal(1.2, pose.Scale);
        Assert.InRange(maxExactToLocalErrorMetres, 0d, .0001d);
        Assert.InRange(maxAdditionalFoliageSubmissionErrorMetres, 0d, .0001d);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFarLight_CannotPassThePrecisionProof(bool omitEmitter)
    {
        var realization = NativeOriginRealizationFixtures.Create(); using var fixture = realization.Scene;
        using var host = new NativeUploadTestScene(); host.Scene.CameraOverride = realization.Framing.Camera;
        var scheduler = new ResidencyScheduler(); var sink = new MapScene3DSink(host.Scene);
        using var residency = new MapRenderResidency(NativeOriginRealizationFixtures.State(realization), sink, null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var point = realization.PickOrigin; var frame = new MapFrameState(point.Frame, point, MapRoofMode.AlwaysVisible, 12, 17, point);
        foreach (var origin in new[] { NativeOriginRealizationFixtures.OriginA, NativeOriginRealizationFixtures.OriginB })
        {
            var renderOrigin = new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z);
            host.Scene.RenderOrigin = renderOrigin; host.Scene.Begin(); sink.BeginFrame();
            residency.Record(frame, omitEmitter ? new WithoutFarLight(sink) : sink); host.Render();
            var recorder = new MapDrawRecorder(); residency.Record(frame, recorder); var draws = recorder.Snapshot().Draws;
            if (omitEmitter) Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => NativeMapGoldenAssertions.FarLightSubmitted(host.Scene, renderOrigin));
            else Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => NativeMapGoldenAssertions.FarLightDraw(draws.Where(d => d.Resource.Key.OwnerId != "far-light"), renderOrigin));
        }
    }
    sealed class WithoutFarLight(IMapDrawSink sink) : IMapDrawSink
    {
        public void Register(MapPreparedResource resource) => sink.Register(resource);
        public void Write(in MapDrawRecord draw) { if (draw.Resource.Key.OwnerId != "far-light") sink.Write(draw); }
    }

    [Theory]
    [InlineData(0, 0, -500)]
    [InlineData(0, 0, 500)]
    [InlineData(-32000, 32000, -500)]
    [InlineData(32000, -32000, 500)]
    public void SparseTerrainEnvelope_PreservesExactVerticesAndPicks(int x, int z, int y)
    {
        var closure = NativeRenderAssetFixtures.Create("foliage");
        var f = FarFoliageGenerationFixtures.Create(x, z, closure, y); using var fixture = new ResidencyFixture(f.Document, closure);
        var scope = NativeOriginRealizationFixtures.Scope(x, z, y);
        var state = fixture.State(scope);
        var policy = ResidencyFixture.Policy(materials: ["paint"], terrain: new System.Collections.Generic.Dictionary<ushort, string> { [10] = "paint" });
        state = state with { Policy = policy, Windows = [scope with { Channels = KhaozEngine.MapDoc.Scene.MapSceneChannels.Terrain }] };
        var scheduler = new ResidencyScheduler(); using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans); var frame = WorldFrame.Nearest(x, z);
        var point = new MapFramePoint(frame, new(.375f, y + 6, .375f));
        var hit = new MapPresentationPicker(plan).Pick(new(point, -Vector3.UnitY, 64), MapPresentationChannel.Physical,
            new(frame, point, MapRoofMode.AlwaysVisible, 12, 17, point));
        Assert.NotNull(hit); Assert.Equal(6, hit.Value.Distance, 8); Assert.Equal(new(.375f, y, .375f), hit.Value.LocalPoint);
        Assert.All(plan.Inventory.Terrain.CanonicalFaces, face => Assert.Equal(y, face.Triangle.A.Y.ToDouble()));
    }
}
