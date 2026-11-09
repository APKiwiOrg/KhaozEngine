using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Source submissions, normalized through independently checked canonical batch correspondences.
/// No emitted native draw selects an expected row or supplies its frame values.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneExpectedFrames
{
    static readonly MapResourceStamp SourceStamp = new("source", "source", "source", "source", 0);
    internal static bool InScope(LegacyScenePlacement placement, MapSceneScope scope)
    {
        var p = placement.Bounds; var b = scope.DrawBounds; double pad = scope.MotionPaddingMetres + .0001;
        return p.MaxX >= b.MinX - pad && p.MinX <= b.MaxX + pad && p.MaxZ >= b.MinZ - pad && p.MinZ <= b.MaxZ + pad;
    }
    internal static IReadOnlyList<SceneExpectedSubmission> Capture(ShippedSceneInputs inputs, LegacySceneInventory source,
        LegacySceneFrame frame, SceneProbe probe, MapSceneScope scope, IReadOnlyList<SceneSourceFoliageGeneration> foliage,
        IReadOnlyDictionary<MapRenderKey, MapSubmissionAnchor> terrain, IReadOnlyDictionary<MapRenderKey, MapSubmissionAnchor> water)
    {
        var result = new List<SceneExpectedSubmission>();
        foreach (var draw in frame.Draws)
        {
            if (draw.Key is not { } key) throw new InvalidOperationException("Source placement lost its semantic identity.");
            var anchor = Anchor(draw.World.Translation);
            var local = draw.World; local.Translation -= new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
            var channels = draw.ShadowOnly ? MapDrawChannels.Shadow | MapDrawChannels.Sky | MapDrawChannels.Physical :
                AllChannels & (draw.CastShadows ? AllChannels : ~MapDrawChannels.Shadow);
            var rigid = new MapRigidFrame(Color.White, Material.None, draw.Dissolve, 0, default, draw.InvertShadow, draw.Complement, MotionKey.None);
            result.Add(new(Record(key, channels, anchor, local, frame.Focus, rigid: rigid), new(draw.Mesh, draw.Maps)));
        }
        foreach (var part in terrain)
            result.Add(new(Record(part.Key, AllChannels, part.Value, Matrix4x4.Identity, frame.Focus,
                rigid: new(Color.White, Material.None, 0, 0, default, false, 0, MotionKey.None))));
        foreach (var light in source.Lights)
        {
            var owner = source.Placements.Single(p => p.Id == light.SourceObjectId);
            if (!InScope(owner, scope)) continue;
            var mapping = inputs.Manifest.Lights.Single(l => l.SourceObjectId == light.SourceObjectId && l.SourceProfileId == light.SourceProfileId);
            var position = Vector3.Transform(light.Offset, owner.World); var anchor = Anchor(position);
            var local = position - new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
            long staticKey = BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(mapping.NativeLightId)));
            var shadow = ShippedSceneLightShadows.Resolve(light, owner, anchor, staticKey);
            var descriptor = new MapLightFrame(new(light.Colour.X, light.Colour.Y, light.Colour.Z), light.Radius, light.Intensity,
                shadow, inputs.Manifest.Placements.Single(p => p.SourceObjectId == light.SourceObjectId).NativePlacementId);
            result.Add(new(Record(new(MapRenderKind.Light, mapping.NativeLightId, "light", 0),
                MapDrawChannels.Colour | (light.CastShadows ? MapDrawChannels.Shadow : 0), anchor, Matrix4x4.CreateTranslation(local), frame.Focus, light: descriptor)));
        }
        var look = Water(inputs);
        foreach (var part in water) result.Add(new(Record(part.Key, MapDrawChannels.Colour, part.Value, Matrix4x4.Identity, frame.Focus, water: new(look))));
        foreach (var generation in foliage.Where(g => g.Rows.Count > 0))
        {
            var points = generation.Rows.Select(r => r.Instance.Position).ToArray();
            var anchor = Anchor(points.Aggregate(Vector3.Min));
            bool shadows = generation.Rows.Any(r => GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, r.Instance.ModelId).CastShadows);
            result.Add(new(Record(generation.Key, MapDrawChannels.Colour | (shadows ? MapDrawChannels.Shadow : 0), anchor,
                Matrix4x4.Identity, frame.Focus, foliage: Foliage(inputs, probe, anchor, false))));
        }
        foreach (var placement in source.Placements.Where(p => inputs.SourcePolicy.AnimatedAssets.Contains(p.ArchetypeId) && InScope(p, scope)))
        {
            var mapping = inputs.Manifest.Placements.Single(p => p.SourceObjectId == placement.Id);
            var anchor = Anchor(placement.World.Translation);
            bool shadows = GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, placement.ArchetypeId).CastShadows;
            result.Add(new(Record(new(MapRenderKind.Foliage, mapping.NativePlacementId, "instances", 0),
                MapDrawChannels.Colour | (shadows ? MapDrawChannels.Shadow : 0), anchor, Matrix4x4.Identity, frame.Focus,
                foliage: Foliage(inputs, probe, anchor, true))));
        }
        return result;
    }
    const MapDrawChannels AllChannels = MapDrawChannels.Colour | MapDrawChannels.Shadow | MapDrawChannels.Camera | MapDrawChannels.Sky | MapDrawChannels.Selection | MapDrawChannels.Physical;
    static MapSubmissionAnchor Anchor(Vector3 point) => new((long)Math.Floor(point.X), (long)Math.Floor(point.Y), (long)Math.Floor(point.Z));
    static MapDrawRecord Record(MapRenderKey key, MapDrawChannels channels, MapSubmissionAnchor anchor, Matrix4x4 matrix, MapFramePoint focus,
        MapRigidFrame? rigid = null, MapLightFrame? light = null, MapWaterFrame? water = null, MapFoliageFrame? foliage = null) =>
        new(new(key, SourceStamp), channels, anchor, matrix, Local(focus, anchor), rigid, light, water, foliage);
    static Vector3 Local(MapFramePoint f, MapSubmissionAnchor a)
    {
        float Axis(long origin, float value, long anchor) => new MapExactValue(checked(origin - anchor), 1).Add(MapExactValue.FromSingle(value)).ToSingle();
        return new(Axis(f.Frame.X * 128L, f.Local.X, a.X), Axis(0, f.Local.Y, a.Y), Axis(f.Frame.Z * 128L, f.Local.Z, a.Z));
    }
    static MapFoliageFrame Foliage(ShippedSceneInputs inputs, SceneProbe probe, MapSubmissionAnchor anchor, bool explicitlyPlaced)
    {
        var o = inputs.SourcePolicy.Foliage;
        var options = new MapFoliageFrameOptions(explicitlyPlaced ? inputs.SourcePolicy.Placements.DefaultDrawRadius : o.DrawRadius, o.DensityRadius, o.FadeBandWidth,
            explicitlyPlaced ? 1 : o.QualityDensity, explicitlyPlaced ? 1 : o.DistantDensity, o.InstanceFadeBandWidth, o.FadeMode, o.WindFadeBladePixels);
        FoliageInteractor Interactor(int i)
        {
            if (i >= o.Interactors.Count) return default;
            var item = o.Interactors[i];
            return item with { Position = Local(ShippedSceneInputs.Point(item.Position.X, item.Position.Y, item.Position.Z), anchor) };
        }
        return new(options, new(o.WindDirection, o.WindStrength, o.WindSpeed, o.WindSpatialFrequency, o.Interactors.Count,
            Interactor(0), Interactor(1), Interactor(2), Interactor(3)), probe.TimeSeconds, probe.WindSeed);
    }
    static MapFrozenWaterLook Water(ShippedSceneInputs inputs)
    {
        var settings = inputs.SourcePolicy.Water.ResolveInto(new WaterSettings(), new WaterSettings());
        var result = new WaterLook();
        foreach (var field in typeof(WaterLook).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object? value = typeof(WaterSettings).GetProperty(field.Name)?.GetValue(settings) ?? typeof(WaterSettings).GetField(field.Name)?.GetValue(settings);
            field.SetValue(result, value ?? throw new InvalidOperationException("Unknown source water policy member."));
        }
        return MapFrozenWaterLook.Capture(result);
    }
}
