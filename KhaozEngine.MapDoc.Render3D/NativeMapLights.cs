using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Records every effective resident emitter. Receiver and shadow capacity is resolved by the device,
/// never by a nearest-light cap in scene composition. Shadow exclusion boxes use the submission anchor's space.</summary>
public static class NativeMapLights
{
    public static void Record(MapScopedScene scene, MapFrameState frame, IMapDrawSink sink) => Record(scene, frame, sink,
        new(scene.Witness.ClosureHash, scene.Witness.SourceGeneration, scene.Witness.ScopeDigest, "kemap/native-lights/1", 0));

    public static void Record(MapScopedScene scene, MapFrameState frame, IMapDrawSink sink, MapResourceStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(sink); ArgumentNullException.ThrowIfNull(stamp);
        foreach (var light in MapEffectiveAnnotations.Lights(scene))
        {
            var p = light.Position;
            var anchor = new MapSubmissionAnchor(p.X.Floor(), p.Y.Floor(), p.Z.Floor());
            var local = MapSubmissionGeometry.Offset(p, anchor);
            var shadow = light.CastShadows ? LightShadow.Static(BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(light.Id)))) : LightShadow.None;
            if (shadow.Requested && light.OwnerPlacementId is { } owner)
            {
                var placement = scene.Placements.SingleOrDefault(p => p.PlacementId == owner)
                    ?? throw new MapDocumentException("Light fixture owner is absent from its scoped closure.");
                var asset = scene.Source.Data.Assets.GetAsset(placement.AssetId);
                var bounds = asset.RenderBounds;
                if (asset.LodBounds is { } lod) bounds = new(Vector3.Min(bounds.Min, lod.Min), Vector3.Max(bounds.Max, lod.Max));
                var matrix = MapRenderTransform.ToLocal(NativeMapPlacements.Pose(scene, placement), anchor);
                var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                        (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z) * asset.SourceUnitsToMetres, matrix);
                    min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                }
                shadow = shadow.WithExclusionBox(min, max);
            }
            var reference = new MapResourceReference(new(MapRenderKind.Light, light.Id, "light", 0), stamp);
            var descriptor = new MapLightFrame(new Color(light.Colour.X, light.Colour.Y, light.Colour.Z), light.Radius, light.Intensity, shadow, light.OwnerPlacementId);
            string digest = MapPlacementDigest.Hash(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/native-light/1");
                w.WriteStringValue(light.Id); w.WriteStringValue(light.OwnerPlacementId);
                foreach (double value in new[] { p.X.ToDouble(), p.Y.ToDouble(), p.Z.ToDouble(), light.Colour.X, light.Colour.Y,
                    light.Colour.Z, light.Radius, light.Intensity }) w.WriteNumberValue(value);
                w.WriteBooleanValue(light.CastShadows); w.WriteEndArray();
            });
            sink.Register(new(reference, MapEffectiveAnnotations.Bounds(light), new MapLightResource(light), digest));
            sink.Write(new(reference, MapDrawChannels.Colour | (shadow.Requested ? MapDrawChannels.Shadow : MapDrawChannels.None),
                anchor, Matrix4x4.CreateTranslation(local), MapRenderTransform.FocusLocal(frame.Focus, anchor), null, descriptor, null, null));
        }
    }
}
