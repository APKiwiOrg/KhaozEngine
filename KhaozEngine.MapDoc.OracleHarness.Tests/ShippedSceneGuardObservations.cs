using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneOracleObservation(MapDrawSet DrawSet,
    IReadOnlyList<MapFoliageGenerationKey> Generations, IReadOnlyList<MapFoliageSampleKey> Identities);

/// <summary>Changes only observed public producer output. Source inputs and the published plan stay intact.</summary>
internal static class ShippedSceneGuardObservations
{
    internal sealed class SuppressWrites(IMapDrawSink sink, MapRenderKind kind) : IMapDrawSink
    {
        public void Register(MapPreparedResource resource) => sink.Register(resource);
        public void Write(in MapDrawRecord draw) { if (draw.Resource.Key.Kind != kind) sink.Write(draw); }
    }
    internal static SceneOracleObservation Mutate(SceneOracleObservation value, string variant)
    {
        var resources = value.DrawSet.Resources.ToList(); var draws = value.DrawSet.Draws.ToList();
        if (variant == "extra-part")
        {
            var original = draws.First(d => d.Resource.Key.Kind == MapRenderKind.Placement);
            var reference = original.Resource with { Key = original.Resource.Key with { PartId = "extra-part" } };
            resources.Add(resources.Single(r => r.Reference == original.Resource) with { Reference = reference });
            draws.Add(original with { Resource = reference });
        }
        else if (variant == "missing-coincident") draws.RemoveAll(d => d.Resource.Key.OwnerId == "native-coincident");
        else if (variant is "roof-camera" or "roof-sky")
        {
            for (int i = 0; i < draws.Count; i++)
                if (draws[i].Resource.Key.Kind == MapRenderKind.Roof)
                    draws[i] = draws[i] with { Channels = variant == "roof-camera" ? draws[i].Channels | MapDrawChannels.Camera : draws[i].Channels & ~MapDrawChannels.Sky };
        }
        else if (variant is "missing-generation" or "missing-row")
        {
            int at = resources.FindIndex(r => r.Payload is MapFoliageResource);
            var resource = resources[at]; var payload = (MapFoliageResource)resource.Payload;
            if (variant == "missing-generation")
            {
                resources.RemoveAt(at); draws.RemoveAll(d => d.Resource == resource.Reference);
                string owner = resource.Reference.Key.OwnerId;
                var omitted = value.Generations.Single(g => owner == $"{g.LayerId}/{g.Chunk.X}/{g.Chunk.Z}");
                value = value with { Generations = value.Generations.Where(g => g != omitted).ToArray(), Identities = [] };
            }
            else
            {
                var transforms = payload.WorldTransforms.ToArray(); var ranks = payload.Ranks.ToArray(); var indices = payload.ModelIndices.ToArray();
                float minX = transforms.Min(t => t.M41), maxX = transforms.Max(t => t.M41), minZ = transforms.Min(t => t.M43), maxZ = transforms.Max(t => t.M43);
                int removed = Array.FindIndex(transforms, t => t.M41 > minX && t.M41 < maxX && t.M43 > minZ && t.M43 < maxZ);
                if (removed < 0) throw new InvalidOperationException("Public fixture needs an interior foliage row.");
                resources[at] = resource with
                {
                    Payload = new MapFoliageResource(payload.Anchor,
                    transforms.Where((_, i) => i != removed).ToArray(), ranks.Where((_, i) => i != removed).ToArray(),
                    indices.Where((_, i) => i != removed).ToArray(), payload.Models)
                };
                value = value with { Identities = value.Identities.Where((_, i) => i != removed).ToArray() };
            }
        }
        else if (variant.StartsWith("maps-", StringComparison.Ordinal))
        {
            int at = resources.FindIndex(r => r.Reference.Key.Kind == MapRenderKind.Foliage && r.Payload is MapRigidResource && r.Reference.Key.OwnerId == "native-reed");
            if (at < 0) at = resources.FindIndex(r => r.Reference.Key.Kind == MapRenderKind.Foliage && r.Payload is MapRigidResource);
            var mesh = (MapRigidResource)resources[at].Payload; var maps = mesh.Maps;
            var image = new DecodedImage(new byte[] { 11, 22, 33, 255 }, 1, 1);
            maps = variant switch
            {
                "maps-albedo" => new(image, maps.Normal, maps.Roughness, maps.AlphaCutoff),
                "maps-normal" => new(maps.Albedo, image, maps.Roughness, maps.AlphaCutoff),
                "maps-roughness" => new(maps.Albedo, maps.Normal, image, maps.AlphaCutoff),
                "maps-alpha" => new(maps.Albedo, maps.Normal, maps.Roughness, maps.AlphaCutoff + .25f),
                _ => throw new ArgumentException("Unknown public material mutation.")
            };
            resources[at] = resources[at] with { Payload = new MapRigidResource(mesh.Mesh, maps) };
        }
        else throw new ArgumentException("Unknown public observation mutation.");
        return value with { DrawSet = new(resources, draws) };
    }
}
