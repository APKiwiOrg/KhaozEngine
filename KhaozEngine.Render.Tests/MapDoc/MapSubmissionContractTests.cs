using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapSubmissionContractTests
{
    static MapResourceReference Reference(MapRenderKind kind, string id = "fixture") =>
        new(new(kind, id, "part", 0), new("closure", "source", "scope", "policy", 1));

    static readonly MapBox3 Bounds = new(0, 0, 0, 8, 8, 8);
    static MapPreparedResource Rigid() => new(Reference(MapRenderKind.Placement), Bounds,
        new MapRigidResource(new GltfMesh([new(Vector3.Zero, Vector3.UnitY, Vector4.One)], new uint[] { 0, 0, 0 }), default), "rigid");

    [Fact]
    public void CpuRecorder_RequiresNoDevice()
    {
        var recorder = new MapDrawRecorder();
        var prepared = Rigid();
        recorder.Register(prepared);
        var draw = new MapDrawRecord(prepared.Reference, MapDrawChannels.Colour | MapDrawChannels.Shadow,
            new(32000, 500, -32000), Matrix4x4.Identity, new(2, 1, 3),
            new(Color.White, Material.None, .5f, .1f, new Color(1, 0, 0), false, 0, MotionKey.From(4)), null, null, null);
        recorder.Write(in draw);
        var snapshot = recorder.Snapshot();
        Assert.Single(snapshot.Resources);
        Assert.Equal(draw, Assert.Single(snapshot.Draws));
        Assert.Throws<InvalidOperationException>(() => new MapDrawRecorder().Write(in draw));
        var wrong = draw with { Rigid = null };
        Assert.Throws<ArgumentException>(() => recorder.Write(in wrong));
        ((MapRigidResource)snapshot.Resources[0].Payload).Mesh.Vertices[0].Position = Vector3.One;
        Assert.Equal(Vector3.Zero, ((MapRigidResource)recorder.Snapshot().Resources[0].Payload).Mesh.Vertices[0].Position);
    }

    [Fact]
    public void FrameContract_PreservesTypedLightWaterFoliageAndCoverage()
    {
        var recorder = new MapDrawRecorder();
        var rigid = Rigid(); recorder.Register(rigid);
        var baseFrame = new MapRigidFrame(Color.White, Material.None, .5f, .1f, new Color(1, 0, 0), true, 0, MotionKey.From(3));
        foreach (float phase in new[] { 0f, 1f })
        {
            var draw = new MapDrawRecord(rigid.Reference, MapDrawChannels.Shadow, default, Matrix4x4.Identity,
                new(4, 5, 6), baseFrame with { DissolveComplement = phase }, null, null, null);
            recorder.Write(in draw);
        }
        Assert.Equal(new[] { 0f, 1f }, recorder.Snapshot().Draws.Select(f => f.Rigid!.Value.DissolveComplement));
        Assert.All(recorder.Snapshot().Draws, d => Assert.True(d.Rigid!.Value.InvertShadowDissolve));
        var red = new MapLightFrame(new Color(1, 0, 0), 3, 2, LightShadow.Static(7), "fixture");
        var blue = new MapLightFrame(new Color(0, 0, 1), 7, 4, LightShadow.Dynamic, null);
        Assert.NotEqual(red, blue);
        Assert.NotEqual(red, red with { Colour = new Color(0, 0, 1) });
        Assert.NotEqual(red, red with { Radius = 7 });
        Assert.NotEqual(red, red with { Intensity = 4 });
        var look = new WaterLook { SwellAmplitude = .04f };
        var water = new MapWaterFrame(MapFrozenWaterLook.Capture(look));
        look.SwellAmplitude = 2;
        Assert.Equal(.04f, water.Look.SwellAmplitude);
        Assert.NotEqual(water, new(MapFrozenWaterLook.Capture(look)));
        var options = new GroundCoverRenderOptions
        {
            WindStrength = .7f,
            Interactors = new[] { new FoliageInteractor(new(1, 2, 3), 4, .5f, .8f) }
        };
        var frozen = MapFrozenFoliageOptions.Capture(options);
        var frame = new MapFoliageFrame(MapFoliageFrameOptions.From(frozen), MapFoliageFrameState.From(frozen), 3, 9);
        Assert.Equal(.7f, frame.State.WindStrength);
        Assert.Equal(.8f, frame.State.I0.Agitation);
        Assert.NotEqual(frame, frame with { TimeSeconds = 4 });
        Assert.NotEqual(frame, frame with { WindSeed = 10 });
        Assert.NotEqual(frame, frame with { State = frame.State with { WindStrength = .8f } });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapFoliageFrameState(default, 0, 0, 0, 5, default, default, default, default));
    }

    [Fact]
    public void StableFoliagePolicy_ChangesRegistrationNotFrameOptions()
    {
        foreach (string id in new[] { "shrub-a", "shrub-b", "shrub-c", "shrub-d" })
        {
            var policy = new GroundCoverRenderOptions();
            var original = MapFrozenFoliageOptions.Capture(policy);
            policy.MotionProfiles = new System.Collections.Generic.Dictionary<string, FoliageMotionProfile>
            { [id] = new(.3f, .15f, .4f, .035f) };
            policy.ModelCastsShadows = new System.Collections.Generic.Dictionary<string, bool> { [id] = true };
            policy.UseGpuBatches = true;
            var changed = MapFrozenFoliageOptions.Capture(policy);
            Assert.Equal(MapFoliageFrameOptions.From(original), MapFoliageFrameOptions.From(changed));
            var before = new MapFoliageModelBinding(id, [Reference(MapRenderKind.Foliage)], FoliageMotionProfile.Blade, false);
            var after = new MapFoliageModelBinding(id, before.Parts, changed.MotionProfiles[id], changed.ModelCastsShadows[id]);
            Assert.NotEqual(before.Motion, after.Motion);
            Assert.NotEqual(before.CastShadows, after.CastShadows);
        }
        Assert.DoesNotContain(typeof(MapFoliageFrameOptions).GetProperties(), p =>
            p.Name is "MotionProfiles" or "ModelCastsShadows" or "CastsShadows" or "UseGpuBatches");
    }

    [Fact]
    public void MixedModelFixture_PinsContractCorrespondenceWithoutProductionPacking()
    {
        var parts = new[] { Reference(MapRenderKind.Foliage, "a-0"), Reference(MapRenderKind.Foliage, "a-1") };
        var transforms = Enumerable.Repeat(Matrix4x4.CreateTranslation(1, 0, 1), 4).ToArray();
        var resource = new MapFoliageResource(default, transforms, new float[] { .1f, .2f, .3f, .4f },
            NativeRenderAssetFixtures.ModelIndices,
            [new("A", parts, new(.3f, .15f, .4f, .035f), true),
             new("B", [Reference(MapRenderKind.Foliage, "b-0")], new(.6f, .2f, .1f, .07f), false)]);
        transforms[0] = Matrix4x4.Identity; parts[0] = default;
        Assert.Equal(1f, resource.WorldTransforms.Span[0].M41);
        Assert.Equal("a-0", resource.Models[0].Parts[0].Key.OwnerId);
        Assert.Equal(new[] { 0, 1, 0, 1 }, resource.ModelIndices.ToArray());
        Assert.Equal(new[] { 0, 2, 0, 2, 1, 3 }, NativeRenderAssetFixtures.ExpandedRows);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, NativeRenderAssetFixtures.ExpandedMeshes);
        Assert.Equal(6, NativeRenderAssetFixtures.ExpectedParameters.Length);
        Assert.Equal(new Vector4(.2f, -1, .5f, .25f), NativeRenderAssetFixtures.ExpectedParameters[4]);
        Assert.Equal(new Vector4(.6f, .2f, .1f, .07f), NativeRenderAssetFixtures.ExpectedMotion[4]);
        Assert.Equal(96, NativeRenderAssetFixtures.PackedStride);
        Assert.Equal(64, NativeRenderAssetFixtures.ParamsOffset);
        Assert.Equal(80, NativeRenderAssetFixtures.MotionOffset);
        Assert.Throws<ArgumentException>(() => new MapFoliageResource(default, transforms,
            new float[] { 0, 0, 0, 0 }, new[] { 0, 2, 0, 1 }, resource.Models));
        var source = NativeRenderAssetFixtures.FoliageSource();
        var swapped = NativeRenderAssetFixtures.FoliageSource(true);
        Assert.Equal(source.WorldTransforms.ToArray(), swapped.WorldTransforms.ToArray());
        Assert.Equal(source.Ranks.ToArray(), swapped.Ranks.ToArray());
        Assert.Equal(new[] { 1, 0, 1, 0 }, swapped.ModelIndices.ToArray());
        Assert.Equal(new[] { 1, 3, 1, 3, 0, 2 }, NativeRenderAssetFixtures.SwappedExpandedRows);
        Assert.Equal(4, NativeRenderAssetFixtures.ExpectedPatches.Where(p => p.CastShadows).Sum(p => p.Count));
        Assert.Equal(2, NativeRenderAssetFixtures.ExpectedPatches.Where(p => !p.CastShadows).Sum(p => p.Count));
        Assert.Equal(3, NativeRenderAssetFixtures.ExpectedPatches.Length);
        var meshes = NativeRenderAssetFixtures.FoliageParts();
        Assert.Equal(1, meshes[0].Mesh.Vertices.Max(v => v.Position.Y));
        Assert.Equal(-1, meshes[2].Mesh.Vertices.Min(v => v.Position.Y));
        Assert.Equal(2, meshes[2].Mesh.Vertices.Max(v => v.Position.Y) - meshes[2].Mesh.Vertices.Min(v => v.Position.Y));
        Assert.True(MemoryMarshal.TryGetArray(source.WorldTransforms, out var detached));
        detached.Array![detached.Offset] = Matrix4x4.Identity;
        Assert.Equal(1, source.WorldTransforms.Span[0].M41);
    }

    [Fact]
    public void FrozenPolicy_CopiesNestedCallerCollectionsAndEveryWaterOverride()
    {
        var motion = new System.Collections.Generic.Dictionary<string, FoliageMotionProfile> { ["A"] = new(.3f, .15f, .4f, .035f) };
        var casting = new System.Collections.Generic.Dictionary<string, bool> { ["A"] = true };
        var interactors = new[] { new FoliageInteractor(new(1, 2, 3), 4, .5f, .8f) };
        var policy = MapFrozenFoliageOptions.Capture(new() { MotionProfiles = motion, ModelCastsShadows = casting, Interactors = interactors });
        motion["A"] = default; casting["A"] = false; interactors[0] = default;
        Assert.Equal(.3f, policy.MotionProfiles["A"].BendScale);
        Assert.True(policy.ModelCastsShadows["A"]);
        Assert.Equal(.8f, policy.Interactors[0].Agitation);
        var mutable = new WaterLook();
        foreach (var field in typeof(WaterLook).GetFields())
        {
            Type type = Nullable.GetUnderlyingType(field.FieldType)!;
            object value = type == typeof(float) ? .04f : type == typeof(int) ? 3 : type == typeof(bool) ? true :
                type == typeof(Color) ? new Color(.1f, .2f, .3f) : WaterWaveSource.Procedural;
            field.SetValue(mutable, value);
        }
        var frozen = MapFrozenWaterLook.Capture(mutable);
        var roundTrip = frozen.ToWaterLook();
        foreach (var field in typeof(WaterLook).GetFields())
        {
            Assert.Equal(field.GetValue(mutable), field.GetValue(roundTrip));
            field.SetValue(mutable, null);
        }
        Assert.Equal(.04f, frozen.SwellAmplitude);
    }

    [Fact]
    public void Recorder_RecordsEveryTypedFrameAndDynamicField()
    {
        var recorder = new MapDrawRecorder();
        var light = new MapPreparedResource(Reference(MapRenderKind.Light), Bounds,
            new MapLightResource(new(1, "lamp", null, default, Vector3.UnitX, 2, 3, true)), "light");
        var mesh = ((MapRigidResource)Rigid().Payload).Mesh;
        var water = new MapPreparedResource(Reference(MapRenderKind.Water), Bounds, new MapWaterResource(mesh, new byte[] { 7 }), "water");
        var part = Rigid(); recorder.Register(part);
        var foliage = new MapPreparedResource(Reference(MapRenderKind.Foliage), Bounds,
            new MapFoliageResource(default, new[] { Matrix4x4.Identity }, new float[] { .4f }, new[] { 0 },
                [new("plant", [part.Reference], new(.3f, .15f, .4f, .035f), true)]), "foliage");
        recorder.Register(light); recorder.Register(water); recorder.Register(foliage);
        MapDrawRecord Light(MapLightFrame frame) => new(light.Reference, MapDrawChannels.Colour, default,
            Matrix4x4.Identity, new(1, 2, 3), null, frame, null, null);
        var red = new MapLightFrame(new(1, 0, 0), 3, 2, LightShadow.Static(7), "fixture");
        foreach (var frame in new[] { red, new(new(0, 0, 1), 7, 4, LightShadow.Dynamic, null),
            red with { Colour = new(0, 0, 1) }, red with { Radius = 7 }, red with { Intensity = 4 },
            red with { Shadow = LightShadow.Dynamic }, red with { FixtureOwnerId = "other" } })
        { var draw = Light(frame); recorder.Write(in draw); }
        var waterDraw = new MapDrawRecord(water.Reference, MapDrawChannels.Colour, default, Matrix4x4.Identity,
            new(1, 2, 3), null, null, new(MapFrozenWaterLook.Capture(new() { SwellAmplitude = .04f })), null);
        recorder.Write(in waterDraw);
        var changedWater = waterDraw with { Water = new(MapFrozenWaterLook.Capture(new() { SwellAmplitude = .05f })) };
        recorder.Write(in changedWater);
        var policy = MapFrozenFoliageOptions.Capture(new()
        {
            WindStrength = .7f,
            Interactors = new[] { new FoliageInteractor(new(1, 2, 3), 4, .5f, .8f) }
        });
        var first = new MapFoliageFrame(MapFoliageFrameOptions.From(policy), MapFoliageFrameState.From(policy), 1, 9);
        foreach (var frame in new[] { first, first with { TimeSeconds = 2 }, first with { WindSeed = 10 },
            first with { State = first.State with { WindDirection = Vector2.UnitY } },
            first with { State = first.State with { WindStrength = .8f } },
            first with { State = first.State with { WindSpeed = 3 } },
            first with { State = first.State with { WindSpatialFrequency = .9f } },
            first with { State = first.State with { InteractorCount = 0 } },
            first with { State = first.State with { I0 = first.State.I0 with { Agitation = .2f } } },
            first with { Options = first.Options with { DrawRadius = 100 } } })
        {
            var draw = new MapDrawRecord(foliage.Reference, MapDrawChannels.Colour | MapDrawChannels.Shadow,
                default, Matrix4x4.Identity, new(1, 2, 3), null, null, null, frame);
            recorder.Write(in draw);
        }
        var snapshot = recorder.Snapshot();
        Assert.Equal(red, snapshot.Draws[0].Light);
        Assert.Equal(7, snapshot.Draws[1].Light!.Value.Radius);
        Assert.Equal(4, snapshot.Draws[1].Light!.Value.Intensity);
        Assert.Equal(.04f, snapshot.Draws[7].Water!.Value.Look.SwellAmplitude);
        Assert.Equal(7, snapshot.Draws.Take(7).Distinct().Count());
        Assert.Equal(10, snapshot.Draws.Skip(9).Distinct().Count());
    }

    [Fact]
    public void Recorder_RefusesMissingFoliagePartsAtRegistration()
    {
        var foliage = new MapPreparedResource(Reference(MapRenderKind.Foliage), Bounds,
            new MapFoliageResource(default, new[] { Matrix4x4.Identity }, new float[] { .4f }, new[] { 0 },
                [new("plant", [Reference(MapRenderKind.Placement)], default, false)]), "missing");
        Assert.Throws<InvalidOperationException>(() => new MapDrawRecorder().Register(foliage));
    }
}
