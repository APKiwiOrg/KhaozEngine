using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    EditorNavigationController _navigation = null!;
    bool _navigationOwnsPointer;

    // This scene-boundary property remains private because later editor surfaces use the ownership policy without
    // exposing it as part of the public editor contract. It stays true on the release frame.
    bool NavigationOwnsPointer => _navigationOwnsPointer;

    void InitializeNavigation()
    {
        _navigation = new EditorNavigationController(_camera) { FlySpeed = _settings.FlySpeed };
        _navigationOwnsPointer = false;
    }

    void CancelNavigation()
    {
        if (_navigation is null) return;
        _navigationOwnsPointer = _navigation.IsNavigating;
        _navigation.Cancel();
    }

    /// <summary>Maintains aspect ratio and advances editor navigation from the current input snapshot.</summary>
    protected virtual void UpdateCamera(float dt)
    {
        InputState input = Manager!.Input;
        int width = input.Width;
        int height = input.Height;
        if (height > 0) _camera.AspectRatio = (float)width / height;

        bool wasNavigating = _navigation.IsNavigating;
        Pointer? pointer = Manager.Pointer;
        bool toolGesture = _controller.IsDragging || _controller.IsDrawing || _controller.IsSculpting
            || (pointer is not null && pointer.IsDown && !pointer.IsJustPressed);
        bool viewportEligible = input.WindowFocused
            && !input.IsCommandDown
            && !IsOverChrome(input.MousePosition)
            && !AnyEditorFocused
            && !toolGesture;
        Vector3? terrainHit = viewportEligible ? NavigationTerrainHit(input) : null;
        _navigation.Update(input, viewportEligible, terrainHit, dt);
        bool wheelNavigated = viewportEligible && float.IsFinite(input.ScrollDelta) && input.ScrollDelta != 0f;
        _navigationOwnsPointer = wasNavigating || _navigation.IsNavigating || wheelNavigated;
    }

    Vector3? NavigationTerrainHit(InputState input)
    {
        TerrainField? field = _controller.Field ?? _viewport.Field;
        if (field is null) return null;
        int width = input.Width > 0 ? input.Width : 1;
        int height = input.Height > 0 ? input.Height : 1;
        Ray ray = _camera.ScreenToRay(input.MousePosition, width, height);
        Vector3 direction = ray.Direction.LengthSquared() > 1e-12f
            ? Vector3.Normalize(ray.Direction)
            : _camera.Forward;
        float distance = MathF.Max(_camera.FarPlane, 25f);
        return EditorPicking.PickTerrain(field, ray.Origin, direction, distance, out Vector3 hit) ? hit : null;
    }

    void FocusSelection()
    {
        if (!TrySelectionFrame(out Vector3 pivot, out float radius)) return;
        float halfFov = _camera.FieldOfView * 0.5f;
        float fitDistance = radius / MathF.Max(0.1f, MathF.Tan(halfFov));
        _navigation.Frame(pivot, MathF.Max(5f, fitDistance * 1.25f));
    }

    bool TrySelectionFrame(out Vector3 pivot, out float radius)
    {
        EditorSelection selection = _document.Selection;
        TerrainField? field = _controller.Field ?? _viewport.Field;
        switch (selection.Kind)
        {
            case SelectionKind.Terrain:
            {
                MapBounds bounds = _document.Doc.Bounds;
                float x = (bounds.MinX + bounds.MaxX) * 0.5f;
                float z = (bounds.MinZ + bounds.MaxZ) * 0.5f;
                pivot = new Vector3(x, GroundHeight(field, x, z), z);
                radius = MathF.Max(bounds.MaxX - bounds.MinX, bounds.MaxZ - bounds.MinZ) * 0.5f;
                return FiniteFrame(pivot, radius);
            }
            case SelectionKind.Placement when Placement(selection.Id) is MapPlacement placement:
            {
                float height = MathF.Max(0.5f, KindHeight(placement.Kind) * MathF.Abs(placement.Scale));
                float ground = placement.Y ?? GroundHeight(field, placement.X, placement.Z);
                pivot = new Vector3(placement.X, ground + height * 0.5f, placement.Z);
                radius = MathF.Max(1f, height * 0.6f);
                return FiniteFrame(pivot, radius);
            }
            case SelectionKind.Spawn when Spawn(selection.Id) is MapSpawn spawn:
                pivot = new Vector3(spawn.X, GroundHeight(field, spawn.X, spawn.Z) + 0.75f, spawn.Z);
                radius = 1f;
                return FiniteFrame(pivot, radius);
            case SelectionKind.PlayerSpawn when PlayerSpawn(selection.Id) is MapPlayerSpawn playerSpawn:
                pivot = new Vector3(playerSpawn.X, GroundHeight(field, playerSpawn.X, playerSpawn.Z) + 0.75f,
                    playerSpawn.Z);
                radius = 1f;
                return FiniteFrame(pivot, radius);
            case SelectionKind.Feature when FeatureAt(SelectedIndex(selection.Id)) is MapFeature feature
                && FeatureGeometry.TryCenter(feature, out float featureX, out float featureZ):
                pivot = new Vector3(featureX, GroundHeight(field, featureX, featureZ), featureZ);
                radius = FeatureRadius(feature);
                return FiniteFrame(pivot, radius);
            case SelectionKind.Exclusion or SelectionKind.ScatterOverride or SelectionKind.Region
                when SelectedShape() is MapShapeDoc shape && ShapeGeometry.TryBounds(shape, out RectArea bounds):
                pivot = new Vector3((bounds.MinX + bounds.MaxX) * 0.5f,
                    GroundHeight(field, (bounds.MinX + bounds.MaxX) * 0.5f, (bounds.MinZ + bounds.MaxZ) * 0.5f),
                    (bounds.MinZ + bounds.MaxZ) * 0.5f);
                radius = MathF.Max(bounds.MaxX - bounds.MinX, bounds.MaxZ - bounds.MinZ) * 0.5f;
                return FiniteFrame(pivot, radius);
            default:
                pivot = Vector3.Zero;
                radius = 0f;
                return false;
        }
    }

    static float GroundHeight(TerrainField? field, float x, float z) => field?.SampleHeight(x, z) ?? 0f;

    static float FeatureRadius(MapFeature feature) => feature switch
    {
        LakeFeatureDoc lake => MathF.Abs(lake.Radius * lake.OuterFraction),
        FlattenFeatureDoc flatten => MathF.Abs(flatten.Radius),
        RimFeatureDoc rim => MathF.Abs(rim.OuterRadius),
        RidgeFeatureDoc ridge => MathF.Max(MathF.Abs(ridge.Width), 5f),
        _ => 5f,
    };

    static bool FiniteFrame(Vector3 pivot, float radius) =>
        float.IsFinite(pivot.X) && float.IsFinite(pivot.Y) && float.IsFinite(pivot.Z)
        && float.IsFinite(radius) && radius >= 0f;
}
