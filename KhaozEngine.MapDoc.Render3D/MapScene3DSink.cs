using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Publishes admitted prepared products. Frame writes only resolve handles and submit values.</summary>
public sealed partial class MapScene3DSink : IMapRenderDevice, IMapDrawSink, IMapRenderUploadDependencies
{
    readonly Scene3D _scene;
    readonly SceneResourceAccess _access;
    readonly Dictionary<(MapRenderKey, string, MapBox3), List<MapResourceReference>> _aliases = new();
    readonly Dictionary<MapResourceReference, MapRenderHandle> _references = new();
    readonly HashSet<MapResourceReference> _accepted = [];
    readonly HashSet<MapResourceReference> _dependencies = [];
    readonly HashSet<(MapRenderHandle, Matrix4x4, MapDrawChannels)> _drawn = [];
    readonly Dictionary<MapRenderHandle, MapBox3> _bounds = new();
    public MapSceneScopeRestriction? Restriction { get; set; }
    public MapScene3DSink(Scene3D scene) { _scene = scene ?? throw new ArgumentNullException(nameof(scene)); _access = new(scene); }
    public void BeginFrame() => _drawn.Clear();
    public void CompleteUploads() => _scene.CompleteResourceUploads();
    public bool CanBeginUpload(MapPreparedResource resource) => resource.Payload is not MapFoliageResource f ||
        f.Models.All(m => m.Parts.All(ResolvePart));
    bool ResolvePart(MapResourceReference reference) => _references.ContainsKey(reference);
    static (MapRenderKey, string, MapBox3) Identity(MapPreparedResource r) => (r.Reference.Key, r.PayloadDigest,
        r.Payload is MapRigidResource && r.Reference.Key.Kind == MapRenderKind.Foliage ? default : r.Bounds);
    public bool IsUploadDependency(MapPreparedResource resource) => _dependencies.Contains(resource.Reference);
    public SceneUploadDemand MeasureUpload(MapPreparedResource resource)
    {
        var data = Input(resource); return _scene.MeasureResourceUpload(data);
    }
    public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation)
    {
        var data = UploadInput(resource);
        return new Upload(this, resource, _scene.PrepareResourceUpload(data, reservation));
    }
    internal SceneUploadDescription UploadInput(MapPreparedResource resource)
    {
        var input = Input(resource);
        // Measurement borrows synchronously. BeginUpload has its full reservation before detaching these inputs.
        return input.Foliage is { } foliage ? input with
        {
            Foliage = foliage with
            { Transforms = foliage.Transforms.ToArray(), Ranks = foliage.Ranks.ToArray(), ModelIndices = foliage.ModelIndices.ToArray() }
        } : input;
    }
    SceneUploadDescription Input(MapPreparedResource resource)
    {
        switch (resource.Payload)
        {
            case MapRigidResource rigid: return new(rigid.BorrowMesh, rigid.BorrowMaps, null, default);
            case MapTerrainResource terrain:
                var m = terrain.Materials;
                var layers = m.Layers.Select(l => new SceneGroundLayerDescription(l.BorrowAlbedo?.Rgba, (KhaozEngine.Primitives.Color)l.Tint, l.TilesPerMetre)).ToArray();
                return new(terrain.BorrowMesh, null, null, default, new(m.Width, m.Height, layers, m.BaseSpecularStrength));
            case MapWaterResource water: return new(water.BorrowMesh, null, null, water.BorrowClipData);
            case MapFoliageResource foliage:
                var models = foliage.Models.Select(model => new SceneFoliageModelBinding(model.ModelId,
                    model.Parts.Select(p => _access.MeshOf(new(_references[p].Value))).ToArray(), model.Motion, model.CastShadows)).ToArray();
                var transforms = foliage.TransformSpace == MapFoliageTransformSpace.World ? foliage.BorrowWorldTransforms : foliage.BorrowAnchorLocalTransforms;
                return new(null, null, new(new(foliage.Anchor.X, foliage.Anchor.Y, foliage.Anchor.Z), transforms,
                    foliage.BorrowRanks, foliage.BorrowModelIndices, models, foliage.TransformSpace == MapFoliageTransformSpace.World
                        ? SceneFoliageTransformSpace.World : SceneFoliageTransformSpace.AnchorLocal), default);
            case MapLightResource: return new(null, null, null, default);
            default: throw new ArgumentException("Unsupported native resource.", nameof(resource));
        }
    }
    // Registration precedes upload, allowing dependency order to be decided without preparing or decoding.
    internal void Declare(IEnumerable<MapPreparedResource> resources, IReadOnlyDictionary<MapResourceReference, MapRenderHandle> reused)
    {
        foreach (var reference in _references.Keys.Where(r => !_accepted.Contains(r)).ToArray()) _references.Remove(reference);
        _dependencies.Clear(); _aliases.Clear();
        foreach (var resource in resources)
        {
            var id = Identity(resource);
            if (!_aliases.TryGetValue(id, out var aliases)) _aliases.Add(id, aliases = []);
            aliases.Add(resource.Reference);
            if (reused.TryGetValue(resource.Reference, out var handle)) _references[resource.Reference] = handle;
            if (resource.Payload is MapFoliageResource foliage)
                foreach (var part in foliage.Models.SelectMany(m => m.Parts)) _dependencies.Add(part);
        }
    }
    internal void AcceptReferences(IEnumerable<MapResourceReference> references)
    {
        _accepted.Clear(); _accepted.UnionWith(references);
        foreach (var reference in _references.Keys.Where(r => !_accepted.Contains(r)).ToArray()) _references.Remove(reference);
    }
    public void Register(MapPreparedResource resource)
    {
        if (!_references.ContainsKey(resource.Reference)) throw new InvalidOperationException("Native draw registration has no published resource.");
    }
    public void Write(in MapDrawRecord draw)
    {
        if (!_references.TryGetValue(draw.Resource, out var handle)) throw new InvalidOperationException("Unpublished native draw.");
        var channels = draw.Channels;
        if (Restriction is { } restriction && !restriction.ColourBoundsTouches(_bounds[handle]) && draw.Light is null)
            channels &= ~MapDrawChannels.Colour;
        if (!_drawn.Add((handle, draw.LocalTransform, channels))) return;
        var anchor = new RenderAnchor(draw.Anchor.X, draw.Anchor.Y, draw.Anchor.Z);
        if (draw.Rigid is { } rigid)
        {
            bool colour = (channels & MapDrawChannels.Colour) != 0, shadow = (channels & MapDrawChannels.Shadow) != 0;
            if (!colour && !shadow) return;
            var appearance = new RigidInstanceDraw(default, draw.LocalTransform)
            {
                Tint = rigid.Tint,
                Material = rigid.Material,
                Dissolve = rigid.Dissolve,
                DissolveEdgeWidth = rigid.EdgeWidth,
                DissolveEdgeColor = rigid.EdgeColour,
                InvertShadowDissolve = rigid.InvertShadowDissolve,
                DissolveComplement = rigid.DissolveComplement,
                Motion = rigid.Motion,
                CastsShadows = shadow,
                ShadowOnly = !colour && shadow
            };
            _access.DrawResourceAnchored(new(handle.Value), appearance, anchor);
        }
        else if (draw.Light is { } light)
        {
            _scene.AddLightAnchored(draw.LocalTransform.Translation, anchor, light.Colour, light.Radius, light.Intensity, light.Shadow);
        }
        else if (draw.Water is { } water && (channels & MapDrawChannels.Colour) != 0)
            _scene.DrawWater(new WaterSurfaceMesh(_access.MeshOf(new(handle.Value)), anchor, draw.LocalTransform, water.Look.PreparedLook));
        else if (draw.Foliage is { } foliage)
        {
            var o = foliage.Options; var s = foliage.State;
            var settings = new FoliageRenderSettings
            {
                DrawRadius = o.DrawRadius,
                DensityRadius = o.DensityRadius,
                FadeBandWidth = o.FadeBandWidth,
                QualityDensity = o.QualityDensity,
                DistantDensity = o.DistantDensity,
                InstanceFadeBandWidth = o.InstanceFadeBandWidth,
                WindFadeBladePixels = o.WindFadeBladePixels,
                WindDirection = s.WindDirection,
                WindStrength = s.WindStrength,
                WindSpeed = s.WindSpeed,
                WindSpatialFrequency = s.WindSpatialFrequency
            };
            Span<FoliageInteractor> interactors = stackalloc FoliageInteractor[4] { s.I0, s.I1, s.I2, s.I3 };
            _scene.DrawPreparedFoliage(new(handle.Value), draw.LocalFocus, settings, anchor, interactors[..s.InteractorCount], (channels & MapDrawChannels.Colour) != 0, (channels & MapDrawChannels.Shadow) != 0, foliage.TimeSeconds, foliage.WindSeed);
        }
    }
    public void Release(MapRenderHandle handle)
    {
        _scene.ReleaseResource(new(handle.Value)); _bounds.Remove(handle);
        _accepted.RemoveWhere(r => _references.TryGetValue(r, out var h) && h == handle);
        foreach (var key in _references.Where(p => p.Value == handle).Select(p => p.Key).ToArray()) _references.Remove(key);
    }
    sealed class Upload(MapScene3DSink sink, MapPreparedResource resource, SceneResourceUpload upload) : IMapRenderUpload
    {
        public bool Ready => upload.Ready;
        public long Advance(long transferBytes) => upload.Advance(transferBytes);
        public MapRenderHandle Publish()
        {
            var handle = new MapRenderHandle(upload.Publish().Value);
            if (sink._aliases.TryGetValue(Identity(resource), out var aliases))
                foreach (var reference in aliases) sink._references[reference] = handle;
            sink._references[resource.Reference] = handle; sink._bounds[handle] = resource.Bounds;
            return handle;
        }
        public void Dispose() => upload.Dispose();
    }
}
public sealed record MapSceneScopeRestriction(MapBox3 ColourBounds)
{
    internal bool ColourBoundsTouches(MapBox3 b) => ColourBounds.MinX <= b.MaxX && ColourBounds.MaxX >= b.MinX &&
        ColourBounds.MinY <= b.MaxY && ColourBounds.MaxY >= b.MinY && ColourBounds.MinZ <= b.MaxZ && ColourBounds.MaxZ >= b.MinZ;
}
