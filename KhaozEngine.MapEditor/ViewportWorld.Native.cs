using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

public sealed partial class ViewportWorld
{
    NativeMapView? _nativeView;
    MapNativeRenderContext? _nativeContext;
    MapRenderSourceState? _nativeState, _nativePendingState;
    MapNativeRenderContext? _nativePendingContext;
    MapFrameState _nativeFrame;
    public IMapNativeRenderHost? NativeRenderHost { get; set; }
    public string NativeDocumentPath { get; set; } = "";
    public string NativeStyleId { get; set; } = "";
    public Func<MapFramePoint>? NativeObserver { get; set; }
    public Func<string?>? NativeObserverSpaceId { get; set; }
    public MapRoofMode NativeRoofMode { get; set; } = MapRoofMode.AlwaysVisible;
    internal bool IsNative => _nativeView is not null;
    internal MapNativeRenderContext? NativeContext => _nativeContext;
    bool TryBuildNative(MapDocument document, Vector3 focus)
    {
        if (document.ResolverIdentity is null) return false;
        var context = (NativeRenderHost ?? throw new MapDocumentException("NativeRenderHostRequired"))
            .Resolve(document, NativeDocumentPath, NativeStyleId);
        if (NativeObserver is null) throw new MapDocumentException("NativeObserverRequired");
        _nativeState = NativeState(context, focus);
        _nativeContext = context; _doc = document;
        _nativeView = new(_scene, new(_nativeState, new MapScene3DSink(_scene)));
        _nativeFrame = Frame(focus, 0); _built = true; return true;
    }
    static MapRenderSourceState NativeState(MapNativeRenderContext context, Vector3 focus)
    {
        const float radius = 32;
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(focus.X - radius, focus.Z - radius), new(focus.X + radius, focus.Z + radius),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new()),
            new(focus.X - radius, -500, focus.Z - radius, focus.X + radius, 500, focus.Z + radius), MapSceneChannels.All, 1);
        return new(context.Source, context.Closure, context.Extents, context.RenderPolicy, [scope]);
    }
    MapFrameState Frame(Vector3 focus, double time) => new(WorldFrame.Origin, NativeObserver!(), NativeRoofMode, time, 0,
        new(WorldFrame.Origin, focus));
    bool UpdateNative(Vector3 focus, float dt)
    {
        if (_nativeView is null) return false;
        var next = NativeState(_nativeContext!, focus);
        // A window changes only after the focus leaves its accepted 64 metre domain.
        var b = _nativeState!.Windows[0].DrawBounds;
        if (_nativePendingState is null && (focus.X < b.MinX + 8 || focus.X > b.MaxX - 8 || focus.Z < b.MinZ + 8 || focus.Z > b.MaxZ - 8))
        { _nativePendingState = next; _nativePendingContext = _nativeContext; _nativeView.Replace(next); }
        _nativeFrame = Frame(focus, _nativeFrame.TimeSeconds + dt);
        AcceptNative(_nativeView.Update(_nativeFrame)); return true;
    }
    bool DrawNative() { if (_nativeView is null) return false; _nativeView.Draw(_nativeFrame); return true; }
    public MapPresentationHit? PickNative(MapPresentationRay ray, MapPresentationChannel channel) => _nativeView?.Pick(ray, channel, _nativeFrame);
    public bool RefreshNative(MapDocument document, MapNativeEditEffects? effects = null)
    {
        if (_nativeView is null) return false;
        var context = NativeRenderHost!.Resolve(document, NativeDocumentPath, NativeStyleId);
        var state = NativeState(context, _nativeFrame.Focus.Local);
        _nativePendingState = state; _nativePendingContext = context;
        var transition = effects is null ? _nativeView.Replace(state) : _nativeView.Invalidate(state, effects);
        if (transition.Status != MapSceneStatus.Incomplete && transition.Status != MapSceneStatus.CompleteForScope)
        { _nativePendingState = null; _nativePendingContext = null; throw new MapDocumentException("NativeRenderReplacementRefused"); }
        _doc = document; return true;
    }
    void AcceptNative(MapRenderReadiness readiness)
    {
        if (_nativePendingState is not { } pending || readiness.Transition.PendingWitness is not null) return;
        var accepted = readiness.Transition.AcceptedWitness;
        if (readiness.Transition.Status == MapSceneStatus.CompleteForScope && accepted?.SourceGeneration == pending.Source.SourceGeneration && accepted.PolicyDigest == pending.Policy.Digest)
        { _nativeState = pending; _nativeContext = _nativePendingContext; }
        _nativePendingState = null; _nativePendingContext = null;
    }
    void DisposeNative() { _nativeView?.Dispose(); _nativeView = null; _nativeState = null; _nativeContext = null; _nativePendingState = null; _nativePendingContext = null; }
}

public partial class MapEditorScene
{
    bool? _nativeDriveEnvironment;
    MapNativeRenderContext? _nativeLightingApplied;
    void BuildViewport()
    {
        _viewport.NativeRenderHost = _options.NativeRenderHost;
        _viewport.NativeDocumentPath = _options.DocumentPath;
        _viewport.NativeStyleId = _options.NativeStyleId;
        _viewport.NativeObserver = _options.NativeObserver;
        _viewport.NativeObserverSpaceId = _options.NativeObserverSpaceId;
        _viewport.Build(_document.Doc, _document.Registry); _controller.Field = _viewport.Field;
    }
    void ApplyViewportEnvironment(Scene3D scene)
    {
        if (!_viewport.IsNative) { ApplyEnvironment(scene.Post); return; }
        if (_nativeDriveEnvironment != _options.DriveEnvironment)
        { _viewport.RefreshNative(_document.Doc); _nativeDriveEnvironment = _options.DriveEnvironment; }
        if (_options.DriveEnvironment && _viewport.NativeContext is { } context && !ReferenceEquals(context, _nativeLightingApplied))
        { context.Lighting.ApplyTo(scene); _nativeLightingApplied = context; }
        if (!_options.DriveEnvironment) _nativeLightingApplied = null;
    }
    void InvalidateViewportPlacements()
    { if (!_viewport.RefreshNative(_document.Doc, _document.LastNativeEffects)) _viewport.InvalidatePlacements(); }
}
