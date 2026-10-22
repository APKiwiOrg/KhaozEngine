using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Literal independent seam fixtures. Mutation never changes the expected inventory.</summary>
internal static class ShippedSceneGuardDraws
{
    internal static MapDrawSet Create()
    {
        var stamp = new MapResourceStamp("invented-closure", "invented-source", "invented-scope", "invented-policy", 7);
        var mesh = new GltfMesh([new(new(0, 0, 0), Vector3.UnitZ, Vector4.One), new(new(1, 0, 0), Vector3.UnitZ, Vector4.One), new(new(0, 1, 0), Vector3.UnitZ, Vector4.One)], new uint[] { 0, 1, 2 });
        var bounds = new KhaozEngine.MapDoc.Editing.MapBox3(0, 0, 0, 1, 1, 0);
        MapPreparedResource Resource(MapRenderKind kind, string owner, MapResourcePayload payload, int lod = 0) => new(new(new(kind, owner, "part/0", lod), stamp), bounds, payload, "invented-payload");
        var basePart = Resource(MapRenderKind.Placement, "column", new MapRigidResource(mesh, default));
        var farPart = Resource(MapRenderKind.Placement, "column", new MapRigidResource(mesh, default), 1);
        var roof = Resource(MapRenderKind.Roof, "canopy", new MapRigidResource(mesh, default));
        var light = Resource(MapRenderKind.Light, "emitter", new MapLightResource(new(1, "emitter", "column",
            new(new(0, 1), new(1, 1), new(0, 1)), Vector3.One, 2, 3, true)));
        var water = Resource(MapRenderKind.Water, "pool", new MapWaterResource(mesh, new byte[] { 1, 2, 3, 4 }));
        var a = new MapFoliageModelBinding("model-a", [basePart.Reference], FoliageMotionProfile.Blade, true);
        var b = new MapFoliageModelBinding("model-b", [farPart.Reference], FoliageMotionProfile.Blade, false);
        var foliage = Resource(MapRenderKind.Foliage, "cover", new MapFoliageResource(default,
            new[] { Matrix4x4.Identity, Matrix4x4.Identity }, new float[] { .25f, .25f }, new[] { 0, 1 }, new[] { a, b }));
        var rigid = new MapRigidFrame(Color.White, Material.None, .5f, .02f, Color.White, false, 0, MotionKey.None);
        MapDrawRecord Draw(MapPreparedResource resource) => new(resource.Reference, MapDrawChannels.Colour | MapDrawChannels.Shadow, default, Matrix4x4.Identity, new(2, 3, 4),
            resource.Payload is MapRigidResource ? rigid : null, resource.Payload is MapLightResource ? new(Color.White, 3, 2, LightShadow.Static(17).WithExclusionBox(Vector3.Zero, Vector3.One), "column") : null,
            resource.Payload is MapWaterResource ? new(MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = .3f })) : null,
            resource.Payload is MapFoliageResource ? new(new(100, 80, 10, 1, .5f, 2, GroundCoverFadeMode.HeightScale, 2),
                new(Vector2.UnitX, .3f, 1, .1f, 0, default, default, default, default), 2, 17) : null);
        return new(new[] { basePart, farPart, roof, light, water, foliage }, new[] { Draw(basePart), Draw(farPart) with { Rigid = rigid with { DissolveComplement = 1 } }, Draw(roof), Draw(light), Draw(water), Draw(foliage) });
    }
    internal static MapDrawSet Mutate(MapDrawSet original, string variant)
    {
        var resources = original.Resources.ToList(); var draws = original.Draws.ToList();
        int At(MapRenderKind kind) => resources.FindIndex(r => r.Reference.Key.Kind == kind);
        int DrawAt(MapRenderKind kind) => draws.FindIndex(r => r.Resource.Key.Kind == kind);
        void Draw(MapRenderKind kind, Func<MapDrawRecord, MapDrawRecord> mutation) { int i = DrawAt(kind); draws[i] = mutation(draws[i]); }
        if (variant == "missing-part") { resources.RemoveAt(0); draws.RemoveAt(0); }
        else if (variant == "duplicate-resource") resources.Add(resources[0]);
        else if (variant == "changed-mesh")
        {
            var resource = resources[0]; var mesh = ((MapRigidResource)resource.Payload).Mesh; mesh.Vertices[0].Position.X += .1f;
            resources[0] = resource with { Payload = new MapRigidResource(mesh, default) };
        }
        else if (variant == "changed-roof") Draw(MapRenderKind.Roof, d => d with { Channels = MapDrawChannels.Shadow });
        else if (variant == "changed-water")
        { int i = At(MapRenderKind.Water); var p = (MapWaterResource)resources[i].Payload; resources[i] = resources[i] with { Payload = new MapWaterResource(p.Mesh, new byte[] { 1, 2, 3, 5 }) }; }
        else if (variant == "changed-foliage" || variant is "model-indices" or "model-parts" or "model-profile" or "model-casting")
        {
            int i = At(MapRenderKind.Foliage); var p = (MapFoliageResource)resources[i].Payload; var models = p.Models.ToArray();
            if (variant == "model-parts") models[0] = new(models[0].ModelId, models[1].Parts, models[0].Motion, models[0].CastShadows);
            if (variant == "model-profile") models[0] = new(models[0].ModelId, models[0].Parts, new FoliageMotionProfile(models[0].Motion.BendScale, models[0].Motion.MaxLean, 2, models[0].Motion.Shiver), models[0].CastShadows);
            if (variant == "model-casting") models[0] = new(models[0].ModelId, models[0].Parts, models[0].Motion, !models[0].CastShadows);
            resources[i] = resources[i] with
            {
                Payload = new MapFoliageResource(p.Anchor, p.WorldTransforms, p.Ranks,
                variant == "model-indices" ? new[] { 1, 0 } : p.ModelIndices, models),
                Bounds = variant == "changed-foliage" ? resources[i].Bounds with { MaxX = 2 } : resources[i].Bounds
            };
        }
        else if (variant is "dissolve" or "complement" or "shadow-inversion") Draw(MapRenderKind.Placement, d => d with
        { Rigid = variant == "dissolve" ? d.Rigid!.Value with { Dissolve = .25f } : variant == "complement" ? d.Rigid!.Value with { DissolveComplement = 1 } : d.Rigid!.Value with { InvertShadowDissolve = true } });
        else if (variant.StartsWith("light-", StringComparison.Ordinal)) Draw(MapRenderKind.Light, d => d with
        {
            Light = variant switch
            {
                "light-colour" => d.Light!.Value with { Colour = Color.Black },
                "light-radius" => d.Light!.Value with { Radius = 4 },
                "light-intensity" => d.Light!.Value with { Intensity = 3 },
                "light-exclusion" => d.Light!.Value with { Shadow = LightShadow.Static(17).WithExclusionBox(Vector3.One, new(2)) },
                _ => throw new ArgumentException("unknown invented light mutation")
            }
        });
        else if (variant == "swell") Draw(MapRenderKind.Water, d => d with { Water = new(MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = .4f })) });
        else if (variant is "wind" or "interactor" or "foliage-shadow") Draw(MapRenderKind.Foliage, d => variant == "foliage-shadow" ? d with { Channels = MapDrawChannels.Colour } : d with
        { Foliage = d.Foliage!.Value with { State = variant == "wind" ? d.Foliage.Value.State with { WindSpeed = 2 } : d.Foliage.Value.State with { InteractorCount = 1, I0 = new() { Position = Vector3.One, Radius = 1 } } } });
        else if (variant == "focus") Draw(MapRenderKind.Placement, d => d with { LocalFocus = new(3, 3, 4) });
        else throw new ArgumentException("unknown invented scene mutation");
        return new(resources, draws);
    }
}
