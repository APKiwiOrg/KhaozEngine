using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The active editing tool. <see cref="Select"/> drives gizmo gestures on the selection; the place
/// modes ground-snap a click into an Add command; the draw modes rubber-band a disc (click-drag) or rect
/// (shift-drag) into an exclusion or a region; <see cref="BakeRegion"/> drags a rect on the ground to freeze
/// a scatter layer into placements; <see cref="EditFeature"/> is inspector-driven and takes no viewport
/// gesture.</summary>
public enum EditorToolMode
{
    /// <summary>Pick + transform-gizmo drag on the current selection.</summary>
    Select,
    /// <summary>Ground-snap a click into a new prop placement of <see cref="EditorToolController.PlaceKind"/>.</summary>
    PlacePlacement,
    /// <summary>Ground-snap a click into a new NPC spawn of <see cref="EditorToolController.SpawnArchetype"/>.</summary>
    PlaceSpawn,
    /// <summary>Rubber-band a scatter exclusion shape (disc on drag, rect on shift-drag).</summary>
    DrawExclusion,
    /// <summary>Rubber-band a named region shape (disc on drag, rect on shift-drag), auto-named region-N.</summary>
    DrawRegion,
    /// <summary>Terrain-feature parameter editing (inspector-driven, no viewport gesture).</summary>
    EditFeature,
    /// <summary>Drag a rect on the ground to freeze <see cref="EditorToolController.BakeLayer"/>'s scatter
    /// into placements. One shot: a completed bake returns to <see cref="Select"/>.</summary>
    BakeRegion,
}

/// <summary>Per-frame editor input, GPU-free and immutable: the pick ray (origin plus a caller-normalized
/// direction, so a returned pick T reads as a world distance), the pointer press/down/release edges, the shift
/// modifier, the delete/escape key edges, and the frame delta. A scene wires the window input into this struct;
/// the controller reads nothing else, so its whole policy is headless-testable frame by frame.</summary>
public readonly struct EditorFrameInput
{
    /// <summary>World-space pick ray origin (the camera eye).</summary>
    public Vector3 RayOrigin { get; }
    /// <summary>World-space pick ray direction, normalized by the caller so pick T reads as a world distance.</summary>
    public Vector3 RayDirection { get; }
    /// <summary>True on the frame the primary pointer button went down (press edge).</summary>
    public bool PointerPressed { get; }
    /// <summary>True while the primary pointer button is held.</summary>
    public bool PointerDown { get; }
    /// <summary>True on the frame the primary pointer button went up (release edge).</summary>
    public bool PointerReleased { get; }
    /// <summary>True while a shift modifier is held (switches the draw modes from disc to rect).</summary>
    public bool Shift { get; }
    /// <summary>True on the frame the delete key went down (removes the selection).</summary>
    public bool DeletePressed { get; }
    /// <summary>True on the frame the escape key went down (cancels the gesture and returns to Select).</summary>
    public bool EscapePressed { get; }
    /// <summary>Seconds elapsed this frame.</summary>
    public float Dt { get; }

    /// <summary>Builds a frame input. Every flag defaults to false and <paramref name="dt"/> to zero, so a test
    /// only names the edges it exercises.</summary>
    public EditorFrameInput(Vector3 rayOrigin, Vector3 rayDirection,
        bool pointerPressed = false, bool pointerDown = false, bool pointerReleased = false,
        bool shift = false, bool deletePressed = false, bool escapePressed = false, float dt = 0f)
    {
        RayOrigin = rayOrigin;
        RayDirection = rayDirection;
        PointerPressed = pointerPressed;
        PointerDown = pointerDown;
        PointerReleased = pointerReleased;
        Shift = shift;
        DeletePressed = deletePressed;
        EscapePressed = escapePressed;
        Dt = dt;
    }
}

/// <summary>The GPU-free per-frame editing policy: it reads the pick ray + pointer/keyboard edges from an
/// <see cref="EditorFrameInput"/> and the <see cref="Field"/> and emits reversible commands through the
/// <see cref="EditorDocument"/> choke point. Select mode picks the document (or grabs a transform-gizmo handle and
/// coalesces the drag into one undo step, sealed on release); the place modes ground-snap a click into an Add
/// command; the draw modes rubber-band a disc or rect into an exclusion or a region. Escape cancels any gesture and
/// returns to Select; Delete removes the selection. Holds no GPU state, so the whole surface is headless-testable.
/// </summary>
public sealed class EditorToolController
{
    /// <summary>Cap for the pick / ground raycasts, in world units (the ray direction is caller-normalized).</summary>
    const float PickDistance = 100_000f;

    /// <summary>Fallback world-space box height for a kit id absent from <see cref="HeightOf"/>.</summary>
    const float DefaultKindHeight = 2f;

    /// <summary>Smallest disc radius / rect edge a draw gesture commits, so a stray click makes no zero-size shape.</summary>
    const float MinDrawExtent = 0.05f;

    readonly EditorDocument _document;
    EditorToolMode _mode = EditorToolMode.Select;

    // Select-mode gizmo drag state.
    bool _dragging;
    GizmoDrag.GizmoHandle _dragHandle;
    GizmoDrag.DragGesture _drag;
    SelectionKind _dragKind;
    string _dragId = "";
    float? _dragStartY;

    // Draw-mode rubber-band state (shared by the draw modes and the bake-region rect gesture).
    bool _drawing;
    Vector3 _drawStart;
    bool _drawRect;

    // Explicit bake-layer override; null resolves to the document's first scatter layer (see BakeLayer).
    string? _bakeLayer;

    /// <summary>Creates the controller over the document it mutates. Set <see cref="Field"/> and
    /// <see cref="HeightOf"/> before the first <see cref="Update"/> that picks or places.</summary>
    public EditorToolController(EditorDocument document) =>
        _document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>The active tool. Setting it to a different value cancels any in-flight gesture and seals the
    /// undo stack (the Task 2 gesture barrier), so a later edit never coalesces across the tool switch.</summary>
    public EditorToolMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _dragging = false;
            _drawing = false;
            _document.SealGesture();
            _mode = value;
        }
    }

    /// <summary>The kit id a <see cref="EditorToolMode.PlacePlacement"/> click instances (palette-selected).</summary>
    public string PlaceKind { get; set; } = "";

    /// <summary>The archetype id a <see cref="EditorToolMode.PlaceSpawn"/> click stamps.</summary>
    public string SpawnArchetype { get; set; } = "";

    /// <summary>The scatter layer a <see cref="EditorToolMode.BakeRegion"/> rect gesture freezes. Defaults to the
    /// document's first scatter layer name (null when the document has none), and an explicit set overrides it.</summary>
    public string? BakeLayer
    {
        get => _bakeLayer ?? (_document.Doc.ScatterLayers.Count > 0 ? _document.Doc.ScatterLayers[0].Name : null);
        set => _bakeLayer = value;
    }

    /// <summary>The terrain field the ground-snap and picking read. Null before the world is built (the tools
    /// then no-op). The scene assigns it after each viewport build / rebuild.</summary>
    public TerrainField? Field { get; set; }

    /// <summary>Maps a placement kit id to its world-space box height, for picking placement AABBs. Defaults to a
    /// constant fallback; the scene points it at the viewport's manifest heights.</summary>
    public Func<string, float> HeightOf { get; set; } = _ => DefaultKindHeight;

    /// <summary>The gizmo's screen-constant world scale this frame, shared with the drawn mesh so the pickable
    /// handle region matches. The scene sets it from the camera distance each frame.</summary>
    public float GizmoScale { get; set; } = 1f;

    /// <summary>True while a Select-mode gizmo drag is in flight.</summary>
    public bool IsDragging => _dragging;

    /// <summary>True while a draw-mode rubber-band is in flight (press captured, release pending).</summary>
    public bool IsDrawing => _drawing;

    /// <summary>A one-line, mode-specific hint for the active tool, folding in <see cref="PlaceKind"/> and
    /// <see cref="SpawnArchetype"/> where they apply. The one-shot draw tools (exclusion, region, bake) say so.
    /// The scene renders this alongside the mode name in the status strip. Developer-tool text, so it is a raw
    /// string (the editor is not player-facing) and carries no em / en dashes or semicolons.</summary>
    public string ModeHint => _mode switch
    {
        EditorToolMode.Select => "Select. Click selects, drag the gizmo handles to move.",
        EditorToolMode.PlacePlacement => "Place placement. Click to place " + PlaceKind + ".",
        EditorToolMode.PlaceSpawn => "Place spawn. Click to place a " + SpawnArchetype + " spawn.",
        EditorToolMode.DrawExclusion => "Draw exclusion. Drag a disc, shift-drag a rect, scatter skips it. One shot.",
        EditorToolMode.DrawRegion => "Draw region. Drag out a named gameplay region. One shot.",
        EditorToolMode.EditFeature => "Edit feature. Select terrain features in the outline, edit in the inspector.",
        EditorToolMode.BakeRegion => "Bake region. Drag a rect to freeze scatter into placements. One shot.",
        _ => _mode.ToString(),
    };

    /// <summary>Advances the tool for one frame from <paramref name="input"/>. Global edges run first (Escape
    /// cancels and returns to Select, Delete removes the selection), then the per-mode gesture policy.</summary>
    public void Update(in EditorFrameInput input)
    {
        if (input.EscapePressed)
        {
            _dragging = false;
            _drawing = false;
            _document.SealGesture();
            _mode = EditorToolMode.Select;
            return;
        }

        if (input.DeletePressed) DeleteSelection();

        switch (_mode)
        {
            case EditorToolMode.Select: UpdateSelect(input); break;
            case EditorToolMode.PlacePlacement: UpdatePlacePlacement(input); break;
            case EditorToolMode.PlaceSpawn: UpdatePlaceSpawn(input); break;
            case EditorToolMode.DrawExclusion: UpdateDraw(input, region: false); break;
            case EditorToolMode.DrawRegion: UpdateDraw(input, region: true); break;
            case EditorToolMode.EditFeature: break;
            case EditorToolMode.BakeRegion: UpdateBake(input); break;
        }
    }

    // ---- Select ------------------------------------------------------------------------------------------

    void UpdateSelect(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (_dragging)
        {
            // The dragged object can vanish mid-gesture: a Delete edge earlier this same frame, or an undo
            // drain past the object's own Add. Cancel the drag cleanly instead of executing a move on the
            // vanished id (MovePlacementCommand/MoveSpawnCommand throw on a missing id). Guards both the
            // mid-drag frames and the release path, since ApplyDrag only runs below this check.
            if (!DragTargetExists())
            {
                _dragging = false;
                _document.SealGesture();
                return;
            }
            if (input.PointerDown && !input.PointerReleased) ApplyDrag(input);
            if (input.PointerReleased)
            {
                ApplyDrag(input);
                _document.SealGesture();
                _dragging = false;
            }
            return;
        }

        if (input.PointerPressed) BeginGestureOrSelect(input);
    }

    // True while the dragged element still exists in the document (drags only ever target placements/spawns).
    bool DragTargetExists() => _dragKind switch
    {
        SelectionKind.Placement => FindPlacement(_dragId) is not null,
        SelectionKind.Spawn => FindSpawn(_dragId) is not null,
        _ => false,
    };

    void BeginGestureOrSelect(in EditorFrameInput input)
    {
        if (TryGizmoTarget(out Vector3 gizmoPos, out SelectionKind kind, out string id,
                out float? startY, out float startYaw, out float startScale))
        {
            GizmoDrag.GizmoHandle handle = GizmoDrag.HitTest(gizmoPos, GizmoScale, input.RayOrigin, input.RayDirection);
            // Spawns have no yaw/scale in the model, so only the ground-plane translate handle applies.
            if (kind == SelectionKind.Spawn && handle != GizmoDrag.GizmoHandle.TranslateXZ)
                handle = GizmoDrag.GizmoHandle.None;

            if (handle != GizmoDrag.GizmoHandle.None)
            {
                // A grab starts a NEW gesture: seal so the drag's first command never coalesces into a
                // preceding same-object edit (e.g. an inspector scrub the frame before).
                _document.SealGesture();
                Vector3 startPoint = StartPointFor(handle, gizmoPos, input.RayOrigin, input.RayDirection);
                _drag = new GizmoDrag.DragGesture(handle, startPoint, gizmoPos, startYaw, startScale);
                _dragHandle = handle;
                _dragKind = kind;
                _dragId = id;
                _dragStartY = startY;
                _dragging = true;
                return;
            }
        }

        if (EditorPicking.Pick(_document.Doc, Field!, input.RayOrigin, input.RayDirection, PickDistance, HeightOf,
                out EditorPicking.PickResult r) && r.Kind != SelectionKind.None)
            _document.Selection.Set(r.Kind, r.Id);
        else
            _document.Selection.Clear();
    }

    // The gizmo world position + starting transform of the selection, or false when the selection carries no
    // gizmo (nothing / feature / exclusion / region). Placement Y respects the stored ground-snap mode.
    bool TryGizmoTarget(out Vector3 pos, out SelectionKind kind, out string id,
        out float? startY, out float startYaw, out float startScale)
    {
        pos = default; kind = SelectionKind.None; id = ""; startY = null; startYaw = 0f; startScale = 1f;
        EditorSelection sel = _document.Selection;
        if (sel.Kind == SelectionKind.Placement && FindPlacement(sel.Id) is { } p)
        {
            float groundY = p.Y ?? Field!.SampleHeight(p.X, p.Z);
            pos = new Vector3(p.X, groundY, p.Z);
            kind = SelectionKind.Placement; id = p.Id; startY = p.Y; startYaw = p.Yaw; startScale = p.Scale;
            return true;
        }
        if (sel.Kind == SelectionKind.Spawn && FindSpawn(sel.Id) is { } s)
        {
            pos = new Vector3(s.X, Field!.SampleHeight(s.X, s.Z), s.Z);
            kind = SelectionKind.Spawn; id = s.Id;
            return true;
        }
        return false;
    }

    // The world point the drag first grabs on the handle's constraint surface, chosen so the first-frame delta is
    // zero (no grab jump): the ray/vertical-axis closest approach for the up arrow, else the ray/ground-plane hit.
    static Vector3 StartPointFor(GizmoDrag.GizmoHandle handle, Vector3 gizmoPos, Vector3 origin, Vector3 dir)
    {
        if (handle == GizmoDrag.GizmoHandle.TranslateY)
        {
            var temp = new GizmoDrag.DragGesture(handle, gizmoPos, gizmoPos, 0f, 0f);
            float d0 = GizmoDrag.TranslateYDelta(temp, origin, dir);
            return gizmoPos + new Vector3(0f, d0, 0f);
        }
        return IntersectGroundPlane(origin, dir, gizmoPos.Y, out Vector3 hit) ? hit : gizmoPos;
    }

    void ApplyDrag(in EditorFrameInput input)
    {
        Vector3 origin = input.RayOrigin, dir = input.RayDirection;
        switch (_dragHandle)
        {
            case GizmoDrag.GizmoHandle.TranslateXZ:
            {
                Vector3 delta = GizmoDrag.TranslateXZDelta(_drag, origin, dir);
                float nx = _drag.ObjectStart.X + delta.X;
                float nz = _drag.ObjectStart.Z + delta.Z;
                if (_dragKind == SelectionKind.Placement)
                    _document.Execute(new MovePlacementCommand(_dragId, nx, nz, _dragStartY));
                else if (_dragKind == SelectionKind.Spawn)
                    _document.Execute(new MoveSpawnCommand(_dragId, nx, nz));
                break;
            }
            case GizmoDrag.GizmoHandle.TranslateY:
                if (_dragKind == SelectionKind.Placement)
                {
                    float ny = _drag.ObjectStart.Y + GizmoDrag.TranslateYDelta(_drag, origin, dir);
                    _document.Execute(new MovePlacementCommand(_dragId, _drag.ObjectStart.X, _drag.ObjectStart.Z, ny));
                }
                break;
            case GizmoDrag.GizmoHandle.YawRing:
                if (_dragKind == SelectionKind.Placement)
                {
                    float newYaw = _drag.ObjectStartYaw + GizmoDrag.YawDelta(_drag, origin, dir);
                    _document.Execute(new RotatePlacementCommand(_dragId, newYaw));
                }
                break;
            case GizmoDrag.GizmoHandle.Scale:
                if (_dragKind == SelectionKind.Placement)
                {
                    float newScale = _drag.ObjectStartScale * GizmoDrag.ScaleFactor(_drag, origin, dir);
                    _document.Execute(new ScalePlacementCommand(_dragId, newScale));
                }
                break;
        }
    }

    // ---- place -------------------------------------------------------------------------------------------

    void UpdatePlacePlacement(in EditorFrameInput input)
    {
        if (Field is null || !input.PointerPressed) return;
        if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;

        string id = UniqueName("placement", PlacementIdExists);
        _document.Execute(new AddPlacementCommand(new MapPlacement { Id = id, Kind = PlaceKind, X = p.X, Z = p.Z, Y = null }));
        _document.SealGesture();
        _document.Selection.Set(SelectionKind.Placement, id);
    }

    void UpdatePlaceSpawn(in EditorFrameInput input)
    {
        if (Field is null || !input.PointerPressed) return;
        if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;

        string id = UniqueName("spawn", SpawnIdExists);
        _document.Execute(new AddSpawnCommand(new MapSpawn { Id = id, ArchetypeId = SpawnArchetype, X = p.X, Z = p.Z }));
        _document.SealGesture();
        _document.Selection.Set(SelectionKind.Spawn, id);
    }

    // ---- draw (exclusion / region) ------------------------------------------------------------------------

    void UpdateDraw(in EditorFrameInput input, bool region)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p))
            {
                _drawStart = p;
                _drawRect = input.Shift;
                _drawing = true;
            }
            return;
        }

        if (!_drawing || !input.PointerReleased) return;
        _drawing = false;
        if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 end)) return;
        MapShapeDoc? shape = BuildShape(_drawStart, end, _drawRect);
        if (shape is null) return;

        if (region)
        {
            string name = UniqueName("region", RegionExists);
            _document.Execute(new AddRegionCommand(new MapRegion { Name = name, Shape = shape }));
            _document.SealGesture();
            _document.Selection.Set(SelectionKind.Region, name);
        }
        else
        {
            _document.Execute(new AddExclusionCommand(new MapExclusion { Shape = shape }));
            _document.SealGesture();
            int idx = _document.Doc.Exclusions.Count - 1;
            _document.Selection.Set(SelectionKind.Exclusion, idx.ToString(CultureInfo.InvariantCulture));
        }

        // One shot: a completed draw commits exactly one shape, then falls back to Select so the next click picks
        // it rather than starting another. A degenerate gesture returned above without committing and stays armed.
        _mode = EditorToolMode.Select;
    }

    // A disc centred on the press point (radius = XZ distance to the release), or a rect spanning the two XZ
    // corners on a shift-drag. Null when the gesture is smaller than one commit threshold.
    static MapShapeDoc? BuildShape(Vector3 start, Vector3 end, bool rect)
    {
        if (rect)
        {
            float minX = MathF.Min(start.X, end.X), maxX = MathF.Max(start.X, end.X);
            float minZ = MathF.Min(start.Z, end.Z), maxZ = MathF.Max(start.Z, end.Z);
            if (maxX - minX < MinDrawExtent || maxZ - minZ < MinDrawExtent) return null;
            return new RectShapeDoc { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ };
        }

        float dx = end.X - start.X, dz = end.Z - start.Z;
        float radius = MathF.Sqrt(dx * dx + dz * dz);
        if (radius < MinDrawExtent) return null;
        return new DiscShapeDoc { CenterX = start.X, CenterZ = start.Z, Radius = radius };
    }

    // ---- bake region -------------------------------------------------------------------------------------

    // Drag a rect on the ground (press hit -> release hit) and freeze the BakeLayer's scatter over it. Always a
    // rect (no disc), and a no-op when there is no scatter layer to bake or the gesture is sub-threshold.
    void UpdateBake(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p))
            {
                _drawStart = p;
                _drawing = true;
            }
            return;
        }

        if (!_drawing || !input.PointerReleased) return;
        _drawing = false;
        if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 end)) return;

        string? layer = BakeLayer;
        if (layer is null) return;   // nothing to bake without a scatter layer

        float minX = MathF.Min(_drawStart.X, end.X), maxX = MathF.Max(_drawStart.X, end.X);
        float minZ = MathF.Min(_drawStart.Z, end.Z), maxZ = MathF.Max(_drawStart.Z, end.Z);
        if (maxX - minX < MinDrawExtent || maxZ - minZ < MinDrawExtent) return;   // stray click, no region

        _document.Execute(new BakeRegionCommand(new RectArea(minX, minZ, maxX, maxZ), layer, _document.Registry));
        _document.SealGesture();

        // One shot: freezing a region is a discrete commit, so return to Select once it lands.
        _mode = EditorToolMode.Select;
    }

    // ---- delete ------------------------------------------------------------------------------------------

    void DeleteSelection()
    {
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Placement:
                if (FindPlacement(sel.Id) is null) return;
                _document.Execute(new RemovePlacementCommand(sel.Id));
                break;
            case SelectionKind.Spawn:
                if (FindSpawn(sel.Id) is null) return;
                _document.Execute(new RemoveSpawnCommand(sel.Id));
                break;
            case SelectionKind.Exclusion:
                if (!int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ei)
                    || ei < 0 || ei >= _document.Doc.Exclusions.Count) return;
                _document.Execute(new RemoveExclusionCommand(ei));
                break;
            case SelectionKind.Region:
                if (!RegionExists(sel.Id)) return;
                _document.Execute(new RemoveRegionCommand(sel.Id));
                break;
            default:
                return;
        }
        _document.SealGesture();
        _document.Selection.Clear();
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    static bool IntersectGroundPlane(Vector3 origin, Vector3 dir, float planeY, out Vector3 hit)
    {
        if (dir.Y == 0f) { hit = default; return false; }
        float t = (planeY - origin.Y) / dir.Y;
        if (t < 0f) { hit = default; return false; }
        hit = origin + dir * t;
        return true;
    }

    MapPlacement? FindPlacement(string id)
    {
        foreach (MapPlacement p in _document.Doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
        return null;
    }

    MapSpawn? FindSpawn(string id)
    {
        foreach (MapSpawn s in _document.Doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
        return null;
    }

    bool PlacementIdExists(string id) => FindPlacement(id) is not null;

    bool SpawnIdExists(string id) => FindSpawn(id) is not null;

    bool RegionExists(string name)
    {
        foreach (MapRegion r in _document.Doc.Regions)
            if (string.Equals(r.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // The first "<prefix>-N" (N from 1) that is not already taken, so auto-named elements never collide.
    static string UniqueName(string prefix, Func<string, bool> exists)
    {
        int n = 1;
        string name;
        do { name = prefix + "-" + n.ToString(CultureInfo.InvariantCulture); n++; }
        while (exists(name));
        return name;
    }
}
