using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The active editing tool. <see cref="Select"/> drives gizmo gestures on the selection. The place
/// modes ground-snap a click into an Add command. The draw modes rubber-band a disc (click-drag) or rect
/// (shift-drag) into an exclusion, a region, or a scatter override. <see cref="BakeRegion"/> drags a rect on the
/// ground to freeze a scatter layer into placements. <see cref="EditFeature"/> click-places a default-parameterized
/// terrain feature of the selected type at the ground hit.</summary>
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
    /// <summary>Click-place a default-parameterized terrain feature of
    /// <see cref="EditorToolController.PlaceFeatureType"/> at the ground hit. One shot: a placed feature returns
    /// to <see cref="Select"/>. The placed feature is then editable through the inspector and the gizmo.</summary>
    EditFeature,
    /// <summary>Drag a rect on the ground to freeze <see cref="EditorToolController.BakeLayer"/>'s scatter
    /// into placements. One shot: a completed bake returns to <see cref="Select"/>.</summary>
    BakeRegion,
    /// <summary>Rubber-band a scatter override shape (disc on drag, rect on shift-drag), then select the new
    /// override. Appended last so the index-based toolbar cast (<c>(EditorToolMode)ActiveIndex</c>) keeps every
    /// prior mode's index.</summary>
    DrawScatterOverride,
}

/// <summary>Per-frame editor input, GPU-free and immutable: the pick ray (origin plus a caller-normalized
/// direction, so a returned pick T reads as a world distance), the pointer press/down/release edges, the
/// screen-space distance the pointer has travelled since the press (for the body-drag arming threshold), the shift
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
    /// <summary>Screen-space distance (design units, the space the pointer helpers work in) from the press origin to
    /// the current pointer position, i.e. how far the pointer has moved since the button went down. Zero on the
    /// press frame. The body-drag gesture arms only once this clears
    /// <see cref="EditorToolController.BodyDragThreshold"/>, matching the TreeView row-drag threshold precedent, so
    /// a tap below it never turns into a move.</summary>
    public float PointerTravel { get; }
    /// <summary>True while a shift modifier is held (switches the draw modes from disc to rect).</summary>
    public bool Shift { get; }
    /// <summary>True on the frame the delete key went down (removes the selection).</summary>
    public bool DeletePressed { get; }
    /// <summary>True on the frame the escape key went down (cancels the gesture and returns to Select).</summary>
    public bool EscapePressed { get; }
    /// <summary>Seconds elapsed this frame.</summary>
    public float Dt { get; }

    /// <summary>Builds a frame input. Every flag defaults to false, <paramref name="pointerTravel"/> and
    /// <paramref name="dt"/> to zero, so a test only names the edges it exercises.</summary>
    public EditorFrameInput(Vector3 rayOrigin, Vector3 rayDirection,
        bool pointerPressed = false, bool pointerDown = false, bool pointerReleased = false,
        float pointerTravel = 0f,
        bool shift = false, bool deletePressed = false, bool escapePressed = false, float dt = 0f)
    {
        RayOrigin = rayOrigin;
        RayDirection = rayDirection;
        PointerPressed = pointerPressed;
        PointerDown = pointerDown;
        PointerReleased = pointerReleased;
        PointerTravel = pointerTravel;
        Shift = shift;
        DeletePressed = deletePressed;
        EscapePressed = escapePressed;
        Dt = dt;
    }
}

/// <summary>Which transform-gizmo handles the current selection exposes, resolved by
/// <see cref="EditorToolController.TryGizmo"/> and shared with the viewport so the drawn handles match the
/// controller's pickable region.</summary>
internal enum GizmoAffordance
{
    /// <summary>No gizmo (nothing selected, or a selection with no draggable transform: terrain, a polygon shape,
    /// an unknown feature type).</summary>
    None,
    /// <summary>The selection marker plus the XZ translate arrows, no yaw / scale (a spawn: only its ground-plane
    /// position is draggable).</summary>
    Marker,
    /// <summary>Translate arrows plus the scale cube, no yaw ring (a rotationally symmetric feature such as a lake
    /// or flatten, or a disc / rect shape).</summary>
    MoveScale,
    /// <summary>Translate arrows, yaw ring, and scale cube, but no vertical arrow (a rotatable terrain feature: a
    /// ridge turns its direction vector, a rim offsets its pass angles). Distinct from <see cref="Full"/>, which
    /// also carries the +Y arrow a placement needs.</summary>
    MoveScaleRotate,
    /// <summary>The full transform: translate arrows, yaw ring, and scale cube (a placement).</summary>
    Full,
}

/// <summary>The GPU-free per-frame editing policy: it reads the pick ray + pointer/keyboard edges from an
/// <see cref="EditorFrameInput"/> and the <see cref="Field"/> and emits reversible commands through the
/// <see cref="EditorDocument"/> choke point. Select mode picks the document (or grabs a transform-gizmo handle and
/// coalesces the drag into one undo step, sealed on release). The place modes ground-snap a click into an Add
/// command. The draw modes rubber-band a disc or rect into an exclusion, a region, or a scatter override. Escape
/// cancels any gesture and returns to Select, Delete removes the selection. Holds no GPU state, so the whole
/// surface is headless-testable.
/// </summary>
public sealed class EditorToolController
{
    /// <summary>Cap for the pick / ground raycasts, in world units (the ray direction is caller-normalized).</summary>
    const float PickDistance = 100_000f;

    /// <summary>Fallback world-space box height for a kit id absent from <see cref="HeightOf"/>.</summary>
    const float DefaultKindHeight = 2f;

    /// <summary>Smallest disc radius / rect edge a draw gesture commits, so a stray click makes no zero-size shape.</summary>
    const float MinDrawExtent = 0.05f;

    /// <summary>Screen-space distance (design units) a body press must travel before it arms a drag, so a tap on an
    /// object's body selects without moving it. Matches the TreeView row-drag threshold (6f). The gizmo handles keep
    /// their deliberate arm-on-press, only a press on the object body away from a handle waits for this.</summary>
    internal const float BodyDragThreshold = 6f;

    readonly EditorDocument _document;
    EditorToolMode _mode = EditorToolMode.Select;

    // Select-mode gizmo drag state.
    bool _dragging;
    GizmoDrag.GizmoHandle _dragHandle;
    GizmoDrag.DragGesture _drag;
    SelectionKind _dragKind;
    string _dragId = "";
    float? _dragStartY;
    // The pre-drag snapshot for a shape / feature gizmo drag, so each frame builds the new shape / feature from the
    // grab-time value plus the gesture delta (the Edit*ShapeCommand / EditFeatureCommand merge coalesces the drag
    // into one undo step). Null for a placement / spawn drag, which move through their own Move commands.
    MapShapeDoc? _dragStartShape;
    MapFeature? _dragStartFeature;

    // Pending body drag: a press that selected a translate-capable object away from every gizmo handle records this,
    // then arms the real TranslateXZ drag once the pointer clears BodyDragThreshold. A release below the threshold
    // is a plain tap (selection stands, no move). _pendingBodyStart is the ground-plane point under the cursor AT
    // PRESS, so the armed drag starts from the grab point and the object tracks the cursor one-to-one.
    bool _pendingBody;
    Vector3 _pendingBodyStart;

    // Place-and-adjust: the two Place tools set this after their press-edge Add, so while the pointer stays down the
    // placed id follows the ground hit through Move commands that the Add absorbs (one undo step), sealed on release.
    bool _placing;
    SelectionKind _placeKind;
    string _placeId = "";

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
            _pendingBody = false;
            _placing = false;
            _document.SealGesture();
            _mode = value;
        }
    }

    /// <summary>The kit id a <see cref="EditorToolMode.PlacePlacement"/> click instances (palette-selected).</summary>
    public string PlaceKind { get; set; } = "";

    /// <summary>The archetype id a <see cref="EditorToolMode.PlaceSpawn"/> click stamps.</summary>
    public string SpawnArchetype { get; set; } = "";

    /// <summary>When true, a <see cref="EditorToolMode.PlaceSpawn"/> click stamps a player start marker
    /// (<see cref="MapPlayerSpawn"/>, auto-id "player-N") instead of an NPC <see cref="MapSpawn"/>. The spawn
    /// palette's pinned "player spawn" entry sets this true, an archetype entry sets it false.</summary>
    public bool PlacingPlayerSpawn { get; set; }

    /// <summary>The terrain-feature discriminator a <see cref="EditorToolMode.EditFeature"/> click places (a
    /// registry feature type, list-selected). A type outside the four built-ins has no editor default, so a click
    /// with such a type selected places nothing.</summary>
    public string PlaceFeatureType { get; set; } = "";

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

    /// <summary>Whether an element (kind, id) is pickable from the viewport, consulted by the Select-mode pick so a
    /// hidden element cannot be clicked (it is still selectable from the outline, which does not go through here).
    /// Defaults to everything pickable, and the scene points it at its <see cref="EditorVisibility.IsElementVisible"/>.</summary>
    public Func<SelectionKind, string, bool> IsVisible { get; set; } = static (_, _) => true;

    /// <summary>Invoked with (kind, index) right after a Feature, Exclusion, or ScatterOverride delete shrinks its
    /// list, so a caller (the scene, wired to <see cref="EditorVisibility.RemoveIndex"/>) can drop that index's hide
    /// entry and shift every later hidden index down by one, keeping a hide glued to the surviving elements'
    /// identities. Never invoked for the id/name-keyed kinds (Placement/Spawn/Region), whose hide keys need no
    /// index remap on delete. Optional (null default), so a headless controller test that never wires this just
    /// skips the notification.</summary>
    public Action<SelectionKind, int>? OnIndexRemoved { get; set; }

    /// <summary>True while a Select-mode gizmo drag is in flight.</summary>
    public bool IsDragging => _dragging;

    /// <summary>True while a draw-mode rubber-band is in flight (press captured, release pending).</summary>
    public bool IsDrawing => _drawing;

    /// <summary>A one-line, mode-specific hint for the active tool, folding in <see cref="PlaceKind"/> and
    /// <see cref="SpawnArchetype"/> where they apply. The one-shot draw tools (exclusion, region, scatter override,
    /// bake) say so.
    /// The scene renders this alongside the mode name in the status strip. Developer-tool text, so it is a raw
    /// string (the editor is not player-facing) and carries no em / en dashes or semicolons.</summary>
    public string ModeHint => _mode switch
    {
        EditorToolMode.Select => "Select. Click selects, drag the gizmo handles to move.",
        EditorToolMode.PlacePlacement => "Place placement. Click to place " + PlaceKind + ".",
        EditorToolMode.PlaceSpawn => PlacingPlayerSpawn
            ? "Place player spawn. Click to place a player start."
            : "Place spawn. Click to place a " + SpawnArchetype + " spawn.",
        EditorToolMode.DrawExclusion => "Draw exclusion. Drag a disc, shift-drag a rect, scatter skips it. One shot.",
        EditorToolMode.DrawRegion => "Draw region. Drag out a named gameplay region. One shot.",
        EditorToolMode.EditFeature => "Place feature. Click terrain to add a " + PlaceFeatureType + ". One shot.",
        EditorToolMode.BakeRegion => "Bake region. Drag a rect to freeze scatter into placements. One shot.",
        EditorToolMode.DrawScatterOverride => "Draw scatter override. Drag a disc, shift-drag a rect, tweaks its density and kinds. One shot.",
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
            _pendingBody = false;
            _placing = false;
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
            case EditorToolMode.DrawExclusion: UpdateDraw(input, DrawTarget.Exclusion); break;
            case EditorToolMode.DrawRegion: UpdateDraw(input, DrawTarget.Region); break;
            case EditorToolMode.EditFeature: UpdateEditFeature(input); break;
            case EditorToolMode.BakeRegion: UpdateBake(input); break;
            case EditorToolMode.DrawScatterOverride: UpdateDraw(input, DrawTarget.ScatterOverride); break;
        }
    }

    /// <summary>Which document collection a <see cref="UpdateDraw"/> rubber-band commits into, the shared disc /
    /// rect draw path's third dimension beyond exclusion and region.</summary>
    enum DrawTarget
    {
        /// <summary>A scatter exclusion shape.</summary>
        Exclusion,
        /// <summary>A named gameplay region.</summary>
        Region,
        /// <summary>A scatter override shape.</summary>
        ScatterOverride,
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

        // A fresh press always starts a new gesture: it selects (and may record a pending body drag), superseding
        // any stale pending state from a prior press that never released. This keeps the press-frame selection
        // semantics intact (every PointerPressed re-picks) while the body-drag threshold lives on the held frames.
        if (input.PointerPressed)
        {
            _pendingBody = false;
            BeginGestureOrSelect(input);
            return;
        }

        if (_pendingBody)
        {
            // Released below the threshold: a plain tap. The selection set on press stands, no history entry.
            if (input.PointerReleased) { _pendingBody = false; return; }
            // Held past the threshold: arm the SAME TranslateXZ drag path the arrows use, from the recorded grab
            // point, and apply it this frame so the object moves immediately. Sub-threshold travel does nothing.
            if (input.PointerDown && input.PointerTravel >= BodyDragThreshold)
            {
                if (ArmBodyDrag(input)) ApplyDrag(input);
                _pendingBody = false;
            }
        }
    }

    // Arm a body drag on the current selection as a TranslateXZ gesture starting from the press-time ground point.
    // Mirrors the gizmo-handle grab arm (seal + DragGesture + grab-time snapshots), but the handle is forced to the
    // ground-plane translate and the start point is the recorded grab point rather than a fresh hit. Returns false
    // when the selection is no longer a gizmo target (e.g. deleted mid-hold), so the caller runs no move on it.
    bool ArmBodyDrag(in EditorFrameInput input)
    {
        if (!TryGizmoTarget(out Vector3 gizmoPos, out SelectionKind kind, out string id,
                out float? startY, out float startYaw, out float startScale, out _))
            return false;
        _document.SealGesture();
        _drag = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.TranslateXZ, _pendingBodyStart, gizmoPos, startYaw, startScale);
        _dragHandle = GizmoDrag.GizmoHandle.TranslateXZ;
        _dragKind = kind;
        _dragId = id;
        _dragStartY = startY;
        _dragStartShape = kind is SelectionKind.Exclusion or SelectionKind.Region or SelectionKind.ScatterOverride
            ? SelectedShapeOf(kind, id) : null;
        _dragStartFeature = kind == SelectionKind.Feature && FeatureAt(id) is { } f
            ? FeatureGeometry.Clone(f) : null;
        _dragging = true;
        return true;
    }

    // True while the dragged element still exists in the document, so a Delete edge or an undo drain mid-drag
    // cancels the gesture cleanly instead of executing an edit on a vanished target (which the commands throw on).
    bool DragTargetExists() => _dragKind switch
    {
        SelectionKind.Placement => FindPlacement(_dragId) is not null,
        SelectionKind.Spawn => FindSpawn(_dragId) is not null,
        SelectionKind.PlayerSpawn => FindPlayerSpawn(_dragId) is not null,
        SelectionKind.Feature => FeatureAt(_dragId) is not null,
        SelectionKind.Exclusion => ExclusionShape(_dragId) is not null,
        SelectionKind.ScatterOverride => ScatterOverrideShape(_dragId) is not null,
        SelectionKind.Region => RegionByName(_dragId) is not null,
        _ => false,
    };

    void BeginGestureOrSelect(in EditorFrameInput input)
    {
        if (TryGizmoTarget(out Vector3 gizmoPos, out SelectionKind kind, out string id,
                out float? startY, out float startYaw, out float startScale, out bool rotatable))
        {
            GizmoDrag.GizmoHandle handle = RestrictHandle(kind, rotatable,
                GizmoDrag.HitTest(gizmoPos, GizmoScale, input.RayOrigin, input.RayDirection));

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
                // Snapshot the grab-time shape / feature so the drag rewrites it from a fixed start each frame.
                _dragStartShape = kind is SelectionKind.Exclusion or SelectionKind.Region or SelectionKind.ScatterOverride
                    ? SelectedShapeOf(kind, id) : null;
                _dragStartFeature = kind == SelectionKind.Feature && FeatureAt(id) is { } f
                    ? FeatureGeometry.Clone(f) : null;
                _dragging = true;
                return;
            }
        }

        // No handle grabbed: pick a placement / spawn first, else fall through to the overlay shapes under the
        // ground point (exclusions, regions, feature markers), so those otherwise-invisible authoring shapes are
        // selectable with the mouse. A pick that finds nothing at all clears the selection.
        if (EditorPicking.Pick(_document.Doc, Field!, input.RayOrigin, input.RayDirection, PickDistance, HeightOf,
                out EditorPicking.PickResult r, IsVisible))
        {
            if (r.Kind != SelectionKind.None)
                _document.Selection.Set(r.Kind, r.Id);
            else if (OverlayPicking.Pick(_document.Doc, r.Point.X, r.Point.Z, out OverlayPicking.OverlayPickResult o, IsVisible))
                _document.Selection.Set(o.Kind, o.Id);
            else
                _document.Selection.Clear();
        }
        else
        {
            _document.Selection.Clear();
        }

        // The press landed on the object body, not a handle. If the (now current) selection is a translate-capable
        // gizmo target, record a pending body drag from the ground point under the cursor: the held frames arm it
        // once the pointer clears BodyDragThreshold (a press that newly selects can drag in the same hold), while a
        // sub-threshold release stays a plain selection. Every gizmo target honours TranslateXZ, so being a gizmo
        // target is exactly the "body-draggable" test.
        if (TryGizmoTarget(out Vector3 bodyGizmoPos, out _, out _, out _, out _, out _, out _))
        {
            _pendingBody = true;
            _pendingBodyStart = StartPointFor(GizmoDrag.GizmoHandle.TranslateXZ, bodyGizmoPos,
                input.RayOrigin, input.RayDirection);
        }
    }

    // Which gizmo handle a selection kind honours: a placement takes every handle, an NPC or player spawn only the
    // ground-plane translate (no yaw / scale gizmo in either spawn's marker), an exclusion / region / scatter
    // override only translate + scale (their XZ center moves and their primary radius resizes, with no yaw concept).
    // A feature also takes translate + scale, plus the yaw ring ONLY when it is rotatable (a ridge or rim). The
    // rotatable fact is threaded in from TryGizmoTarget, the same source the affordance decision reads, so the drawn
    // ring and this pickable handle can never disagree.
    static GizmoDrag.GizmoHandle RestrictHandle(SelectionKind kind, bool featureRotatable, GizmoDrag.GizmoHandle handle) => kind switch
    {
        SelectionKind.Spawn or SelectionKind.PlayerSpawn => handle == GizmoDrag.GizmoHandle.TranslateXZ ? handle : GizmoDrag.GizmoHandle.None,
        SelectionKind.Feature =>
            handle is GizmoDrag.GizmoHandle.TranslateXZ or GizmoDrag.GizmoHandle.Scale
                || (featureRotatable && handle == GizmoDrag.GizmoHandle.YawRing)
                ? handle : GizmoDrag.GizmoHandle.None,
        SelectionKind.Exclusion or SelectionKind.Region or SelectionKind.ScatterOverride =>
            handle is GizmoDrag.GizmoHandle.TranslateXZ or GizmoDrag.GizmoHandle.Scale
                ? handle : GizmoDrag.GizmoHandle.None,
        _ => handle,
    };

    // The gizmo world position + starting transform of the selection, or false when the selection carries no
    // gizmo (nothing / terrain / polygon shape / unknown feature type). Placements take the full transform;
    // spawns, features, and disc / rect shapes sit their gizmo at the element center on the ground. Placement Y
    // respects the stored ground-snap mode.
    bool TryGizmoTarget(out Vector3 pos, out SelectionKind kind, out string id,
        out float? startY, out float startYaw, out float startScale, out bool rotatable)
    {
        pos = default; kind = SelectionKind.None; id = ""; startY = null; startYaw = 0f; startScale = 1f; rotatable = false;
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Placement when FindPlacement(sel.Id) is { } p:
                pos = new Vector3(p.X, p.Y ?? Field!.SampleHeight(p.X, p.Z), p.Z);
                kind = SelectionKind.Placement; id = p.Id; startY = p.Y; startYaw = p.Yaw; startScale = p.Scale;
                return true;
            case SelectionKind.Spawn when FindSpawn(sel.Id) is { } s:
                pos = new Vector3(s.X, Field!.SampleHeight(s.X, s.Z), s.Z);
                kind = SelectionKind.Spawn; id = s.Id;
                return true;
            case SelectionKind.PlayerSpawn when FindPlayerSpawn(sel.Id) is { } ps:
                pos = new Vector3(ps.X, Field!.SampleHeight(ps.X, ps.Z), ps.Z);
                kind = SelectionKind.PlayerSpawn; id = ps.Id;
                return true;
            case SelectionKind.Feature when FeatureAt(sel.Id) is { } f && FeatureGeometry.TryCenter(f, out float fx, out float fz):
                pos = new Vector3(fx, Field!.SampleHeight(fx, fz), fz);
                kind = SelectionKind.Feature; id = sel.Id;
                rotatable = FeatureGeometry.Rotated(f, 0f) is not null;
                // The ring gesture's absolute start yaw differs by feature. A ridge exposes a real orientation (its
                // direction vector), so the start yaw is that direction's angle and the ring tracks the ridge's real
                // heading. A rim has no single heading, it rotates by offsetting every pass angle from wherever they
                // sit, so its gesture is delta-only and the start yaw stays 0. A symmetric feature is not rotatable.
                startYaw = f is RidgeFeatureDoc ridge ? MathF.Atan2(ridge.DirectionZ, ridge.DirectionX) : 0f;
                return true;
            case SelectionKind.Exclusion when ExclusionShape(sel.Id) is { } ex
                    && ShapeGeometry.IsGizmoEditable(ex) && ShapeGeometry.TryCenter(ex, out float ecx, out float ecz):
                pos = new Vector3(ecx, Field!.SampleHeight(ecx, ecz), ecz);
                kind = SelectionKind.Exclusion; id = sel.Id;
                return true;
            case SelectionKind.ScatterOverride when ScatterOverrideShape(sel.Id) is { } so
                    && ShapeGeometry.IsGizmoEditable(so) && ShapeGeometry.TryCenter(so, out float ocx, out float ocz):
                pos = new Vector3(ocx, Field!.SampleHeight(ocx, ocz), ocz);
                kind = SelectionKind.ScatterOverride; id = sel.Id;
                return true;
            case SelectionKind.Region when RegionByName(sel.Id) is { Shape: { } rs }
                    && ShapeGeometry.IsGizmoEditable(rs) && ShapeGeometry.TryCenter(rs, out float rcx, out float rcz):
                pos = new Vector3(rcx, Field!.SampleHeight(rcx, rcz), rcz);
                kind = SelectionKind.Region; id = sel.Id;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The gizmo world position for the current selection and which handle set the viewport should draw,
    /// or <see cref="GizmoAffordance.None"/> when the selection carries no gizmo. Shared with the viewport so the
    /// drawn handles and this controller's pickable region can never drift. No-op (None) before the field is set.</summary>
    internal GizmoAffordance TryGizmo(out Vector3 pos)
    {
        pos = default;
        if (Field is null) return GizmoAffordance.None;
        if (!TryGizmoTarget(out pos, out SelectionKind kind, out _, out _, out _, out _, out bool rotatable))
            return GizmoAffordance.None;
        return kind switch
        {
            SelectionKind.Placement => GizmoAffordance.Full,
            SelectionKind.Spawn or SelectionKind.PlayerSpawn => GizmoAffordance.Marker,
            // A rotatable feature (ridge / rim) adds the yaw ring, drawn from the same rotatable fact RestrictHandle
            // gates the pickable ring on. A symmetric feature or a disc / rect shape stays translate + scale.
            SelectionKind.Feature => rotatable ? GizmoAffordance.MoveScaleRotate : GizmoAffordance.MoveScale,
            // Exclusion / region / scatter-override shapes: translate + uniform scale, no yaw (RestrictHandle above
            // gates their pickable handles to exactly this set). The trailing arm keeps the same value defensively.
            SelectionKind.Exclusion or SelectionKind.Region or SelectionKind.ScatterOverride => GizmoAffordance.MoveScale,
            _ => GizmoAffordance.MoveScale,
        };
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
                switch (_dragKind)
                {
                    case SelectionKind.Placement:
                        _document.Execute(new MovePlacementCommand(_dragId,
                            _drag.ObjectStart.X + delta.X, _drag.ObjectStart.Z + delta.Z, _dragStartY));
                        break;
                    case SelectionKind.Spawn:
                        _document.Execute(new MoveSpawnCommand(_dragId,
                            _drag.ObjectStart.X + delta.X, _drag.ObjectStart.Z + delta.Z));
                        break;
                    case SelectionKind.PlayerSpawn:
                        _document.Execute(new MovePlayerSpawnCommand(_dragId,
                            _drag.ObjectStart.X + delta.X, _drag.ObjectStart.Z + delta.Z));
                        break;
                    case SelectionKind.Feature:
                        ExecuteFeatureEdit(FeatureGeometry.Translated(_dragStartFeature!, delta.X, delta.Z));
                        break;
                    case SelectionKind.Exclusion:
                    case SelectionKind.ScatterOverride:
                    case SelectionKind.Region:
                        ExecuteShapeEdit(ShapeGeometry.Translated(_dragStartShape!, delta.X, delta.Z));
                        break;
                }
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
                switch (_dragKind)
                {
                    case SelectionKind.Placement:
                    {
                        float newYaw = _drag.ObjectStartYaw + GizmoDrag.YawDelta(_drag, origin, dir);
                        _document.Execute(new RotatePlacementCommand(_dragId, newYaw));
                        break;
                    }
                    case SelectionKind.Feature:
                        // Rotate the grab-time snapshot by the whole-gesture yaw delta, so every frame rebuilds from
                        // a fixed start and the EditFeatureCommand same-index merge coalesces the drag into one step,
                        // exactly like the move / scale feature drags. A null (unrotatable) result no-ops.
                        // Negated: YawDelta is pre-signed to compose additively with Matrix4x4.CreateRotationY,
                        // whose positive yaw turns object +X toward world -Z. FeatureGeometry.Rotated instead turns
                        // a raw world vector the standard atan2-increasing way, +X toward world +Z, so the delta
                        // must be un-negated here or the feature spins opposite the dragged cursor.
                        ExecuteFeatureEdit(FeatureGeometry.Rotated(_dragStartFeature!, -GizmoDrag.YawDelta(_drag, origin, dir)));
                        break;
                }
                break;
            case GizmoDrag.GizmoHandle.Scale:
            {
                float factor = GizmoDrag.ScaleFactor(_drag, origin, dir);
                switch (_dragKind)
                {
                    case SelectionKind.Placement:
                        _document.Execute(new ScalePlacementCommand(_dragId, _drag.ObjectStartScale * factor));
                        break;
                    case SelectionKind.Feature:
                        ExecuteFeatureEdit(FeatureGeometry.Scaled(_dragStartFeature!, factor));
                        break;
                    case SelectionKind.Exclusion:
                    case SelectionKind.ScatterOverride:
                    case SelectionKind.Region:
                        ExecuteShapeEdit(ShapeGeometry.Scaled(_dragStartShape!, factor));
                        break;
                }
                break;
            }
        }
    }

    // Route a dragged feature's new value through EditFeatureCommand (same-index merge coalesces the drag), keeping
    // the live feature as the command's old value. A null new value (an untranslatable / unscalable type) no-ops.
    void ExecuteFeatureEdit(MapFeature? newFeature)
    {
        if (newFeature is null || !TryFeatureIndex(_dragId, out int index) || FeatureAt(_dragId) is not { } current)
            return;
        _document.Execute(new EditFeatureCommand(index, newFeature, current));
    }

    // Route a dragged exclusion / scatter override / region's new shape through the matching Edit*ShapeCommand
    // (same-key merge coalesces the drag), keeping the live shape as the command's old value. A null new shape, or
    // a target that has vanished since the grab, no-ops.
    void ExecuteShapeEdit(MapShapeDoc? newShape)
    {
        if (newShape is null) return;
        switch (_dragKind)
        {
            case SelectionKind.Exclusion:
                if (!TryExclusionIndex(_dragId, out int ei) || _document.Doc.Exclusions[ei].Shape is not { } exCurrent)
                    return;
                _document.Execute(new EditExclusionShapeCommand(ei, newShape, exCurrent));
                break;
            case SelectionKind.ScatterOverride:
                if (!TryScatterOverrideIndex(_dragId, out int oi) || _document.Doc.ScatterOverrides[oi].Shape is not { } soCurrent)
                    return;
                _document.Execute(new EditScatterOverrideShapeCommand(oi, newShape, soCurrent));
                break;
            case SelectionKind.Region:
                if (RegionByName(_dragId) is { Shape: { } rgCurrent })
                    _document.Execute(new EditRegionShapeCommand(_dragId, newShape, rgCurrent));
                break;
        }
    }

    // ---- place -------------------------------------------------------------------------------------------

    // Press-edge Add gives immediate feedback + selection, then the gesture stays held: while down, Move commands
    // for the placed id track the ground hit. AddPlacementCommand.TryMerge absorbs those same-id moves, so the whole
    // place-and-adjust folds into ONE undo step whose undo removes the placement. Release seals. A plain click (no
    // hold-move) lands the lone Add exactly as before.
    void UpdatePlacePlacement(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;
            string id = UniqueName("placement", PlacementIdExists);
            _document.Execute(new AddPlacementCommand(new MapPlacement { Id = id, Kind = PlaceKind, X = p.X, Z = p.Z, Y = null }));
            _document.Selection.Set(SelectionKind.Placement, id);
            _placing = true; _placeKind = SelectionKind.Placement; _placeId = id;
            return;
        }

        if (!_placing || _placeKind != SelectionKind.Placement) return;
        if (input.PointerReleased) { _document.SealGesture(); _placing = false; return; }
        if (input.PointerDown && FindPlacement(_placeId) is not null
            && EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 hit))
            _document.Execute(new MovePlacementCommand(_placeId, hit.X, hit.Z, null));
    }

    // The spawn place tool stamps either an NPC spawn or a player start, chosen by PlacingPlayerSpawn (the pinned
    // "player spawn" palette entry). Both share the press-edge-Add-then-hold-to-adjust place-and-adjust path: the
    // matching Add absorbs the same-id Move so the whole gesture is ONE undo step, sealed on release.
    void UpdatePlaceSpawn(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;
            if (PlacingPlayerSpawn)
            {
                string playerId = UniqueName("player", PlayerSpawnIdExists);
                _document.Execute(new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = playerId, X = p.X, Z = p.Z }));
                _document.Selection.Set(SelectionKind.PlayerSpawn, playerId);
                _placing = true; _placeKind = SelectionKind.PlayerSpawn; _placeId = playerId;
                return;
            }
            string id = UniqueName("spawn", SpawnIdExists);
            _document.Execute(new AddSpawnCommand(new MapSpawn { Id = id, ArchetypeId = SpawnArchetype, X = p.X, Z = p.Z }));
            _document.Selection.Set(SelectionKind.Spawn, id);
            _placing = true; _placeKind = SelectionKind.Spawn; _placeId = id;
            return;
        }

        if (!_placing || _placeKind is not (SelectionKind.Spawn or SelectionKind.PlayerSpawn)) return;
        if (input.PointerReleased) { _document.SealGesture(); _placing = false; return; }
        if (!input.PointerDown
            || !EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 hit))
            return;
        if (_placeKind == SelectionKind.Spawn && FindSpawn(_placeId) is not null)
            _document.Execute(new MoveSpawnCommand(_placeId, hit.X, hit.Z));
        else if (_placeKind == SelectionKind.PlayerSpawn && FindPlayerSpawn(_placeId) is not null)
            _document.Execute(new MovePlayerSpawnCommand(_placeId, hit.X, hit.Z));
    }

    // ---- place feature -----------------------------------------------------------------------------------

    // Click-place a default-parameterized feature of PlaceFeatureType at the terrain hit, select it, and one-shot
    // back to Select. A type with no editor default (outside the four built-ins) places nothing but still consumes
    // the click. The click height feeds the flatten default's target height.
    void UpdateEditFeature(in EditorFrameInput input)
    {
        if (Field is null || !input.PointerPressed) return;
        if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;

        MapFeature? feature = FeatureGeometry.CreateDefault(PlaceFeatureType, p.X, p.Z, p.Y);
        if (feature is null) return;

        _document.Execute(new AddFeatureCommand(feature));
        _document.SealGesture();
        int idx = _document.Doc.Terrain.Features.Count - 1;
        _document.Selection.Set(SelectionKind.Feature, idx.ToString(CultureInfo.InvariantCulture));

        // One shot: a placed feature returns to Select so the next click picks it rather than placing another.
        _mode = EditorToolMode.Select;
    }

    // ---- draw (exclusion / region) ------------------------------------------------------------------------

    void UpdateDraw(in EditorFrameInput input, DrawTarget target)
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

        switch (target)
        {
            case DrawTarget.Region:
            {
                string name = UniqueName("region", RegionExists);
                _document.Execute(new AddRegionCommand(new MapRegion { Name = name, Shape = shape }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Region, name);
                break;
            }
            case DrawTarget.ScatterOverride:
            {
                // A fresh override starts as a pure shape (unit density, no kind mix, all layers): the inspector
                // fills in the density multiplier and kind substitutions afterward.
                _document.Execute(new AddScatterOverrideCommand(new MapScatterOverrideDoc { Shape = shape }));
                _document.SealGesture();
                int idx = _document.Doc.ScatterOverrides.Count - 1;
                _document.Selection.Set(SelectionKind.ScatterOverride, idx.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case DrawTarget.Exclusion:
            default:
            {
                _document.Execute(new AddExclusionCommand(new MapExclusion { Shape = shape }));
                _document.SealGesture();
                int idx = _document.Doc.Exclusions.Count - 1;
                _document.Selection.Set(SelectionKind.Exclusion, idx.ToString(CultureInfo.InvariantCulture));
                break;
            }
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
            case SelectionKind.PlayerSpawn:
                if (FindPlayerSpawn(sel.Id) is null) return;
                _document.Execute(new RemovePlayerSpawnCommand(sel.Id));
                break;
            case SelectionKind.Feature:
                if (!TryFeatureIndex(sel.Id, out int fi)) return;
                _document.Execute(new RemoveFeatureCommand(fi));
                OnIndexRemoved?.Invoke(SelectionKind.Feature, fi);
                break;
            case SelectionKind.Exclusion:
                if (!int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ei)
                    || ei < 0 || ei >= _document.Doc.Exclusions.Count) return;
                _document.Execute(new RemoveExclusionCommand(ei));
                OnIndexRemoved?.Invoke(SelectionKind.Exclusion, ei);
                break;
            case SelectionKind.ScatterOverride:
                if (!TryScatterOverrideIndex(sel.Id, out int oi)) return;
                _document.Execute(new RemoveScatterOverrideCommand(oi));
                OnIndexRemoved?.Invoke(SelectionKind.ScatterOverride, oi);
                break;
            case SelectionKind.Region:
                if (!RegionExists(sel.Id)) return;
                _document.Execute(new RemoveRegionCommand(sel.Id));
                break;
            case SelectionKind.BiomeBand:
                if (!int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bi)
                    || bi < 0 || bi >= _document.Doc.Terrain.Biomes.Count) return;
                _document.Execute(new RemoveBiomeBandCommand(bi));
                OnIndexRemoved?.Invoke(SelectionKind.BiomeBand, bi);   // no band hide today, but keeps the index-remap path uniform
                break;
            default:
                return;
        }
        _document.SealGesture();
        _document.Selection.Clear();
    }

    // ---- duplicate -----------------------------------------------------------------------------------------

    /// <summary>World-unit XZ offset applied to a duplicate's position, so it never lands exactly on top of its
    /// source (Cmd+D, decision 8). The kinds with no position (a biome band, a scatter or companion layer)
    /// ignore it entirely.</summary>
    const float DuplicateOffset = 2f;

    /// <summary>Identifies what <see cref="DuplicateSelection"/> created: the duplicated kind, and its fresh key
    /// (the new id/name for a keyed kind, or the new index as a string for an index-keyed kind), the same shape
    /// <see cref="EditorSelection.Set"/> already takes for that kind. Lets a caller confirm a duplicate actually
    /// landed rather than inferring it from a void return.</summary>
    public readonly record struct DuplicateResult(SelectionKind Kind, string Id);

    /// <summary>Duplicates the current selection: a deep clone with a fresh unique identity, offset +2/+2 on X/Z
    /// for the kinds that carry a position, added through the same kind's Add command and immediately sealed
    /// (<see cref="EditorDocument.SealGesture"/>) before the new element becomes the selection. Sealing right
    /// after Execute matters: several Add commands absorb a same-id Move that immediately follows
    /// (place-and-adjust), and a duplicate is not a place gesture, so without the seal a later drag of the fresh
    /// duplicate could silently fold into its Add instead of landing its own undo step. Mirrors the
    /// <see cref="DeleteSelection"/> dispatcher shape, covering every kind Delete removes plus the two it does not
    /// handle (scatter and companion layers, which have no viewport geometry to delete but are still document
    /// elements a user wants to clone). Returns a <see cref="DuplicateResult"/> naming what got created, or null
    /// when nothing was duplicated: an empty selection, Terrain (the singleton root), or a custom feature type
    /// <see cref="FeatureGeometry.Translated"/> does not know how to offset. Both no-op cases no-op silently here,
    /// exactly like Delete's own default branch, and the null return is what lets a caller (the scene's Cmd+D
    /// chord, or an automation caller) tell "duplicated" from "silently skipped" apart instead of assuming
    /// success. The owning scene surfaces a status note for the Terrain case and for a skipped custom feature
    /// type (this controller carries no status text of its own).</summary>
    public DuplicateResult? DuplicateSelection()
    {
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Placement:
            {
                if (FindPlacement(sel.Id) is not { } p) return null;
                string id = UniqueName("placement", PlacementIdExists);
                _document.Execute(new AddPlacementCommand(new MapPlacement
                {
                    Id = id, Kind = p.Kind, X = p.X + DuplicateOffset, Z = p.Z + DuplicateOffset, Y = p.Y,
                    Yaw = p.Yaw, Scale = p.Scale, Tags = new List<string>(p.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Placement, id);
                return new DuplicateResult(SelectionKind.Placement, id);
            }
            case SelectionKind.Spawn:
            {
                if (FindSpawn(sel.Id) is not { } s) return null;
                string id = UniqueName("spawn", SpawnIdExists);
                _document.Execute(new AddSpawnCommand(new MapSpawn
                {
                    Id = id, ArchetypeId = s.ArchetypeId, X = s.X + DuplicateOffset, Z = s.Z + DuplicateOffset,
                    Enabled = s.Enabled, Tags = new List<string>(s.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Spawn, id);
                return new DuplicateResult(SelectionKind.Spawn, id);
            }
            case SelectionKind.PlayerSpawn:
            {
                if (FindPlayerSpawn(sel.Id) is not { } ps) return null;
                string id = UniqueName("player", PlayerSpawnIdExists);
                // AddPlayerSpawnCommand deep-copies at construction (a fresh Tags list), so handing it a plain new
                // instance here is enough: the command never aliases this local's Tags list either way.
                _document.Execute(new AddPlayerSpawnCommand(new MapPlayerSpawn
                {
                    Id = id, X = ps.X + DuplicateOffset, Z = ps.Z + DuplicateOffset, Yaw = ps.Yaw,
                    Enabled = ps.Enabled, Tags = new List<string>(ps.Tags),
                }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.PlayerSpawn, id);
                return new DuplicateResult(SelectionKind.PlayerSpawn, id);
            }
            case SelectionKind.Feature:
            {
                if (!TryFeatureIndex(sel.Id, out int fi)) return null;
                MapFeature source = _document.Doc.Terrain.Features[fi];
                // FeatureGeometry.Translated already clones AND offsets the center / through-point atomically. It
                // returns null for a custom feature type it does not know how to translate (the same "unknown
                // type, no guess" policy TryCenter / Scaled already follow), so an unsupported type no-ops here
                // rather than adding an un-offset clone. The null return is the signal the owning scene checks to
                // surface its "cannot duplicate this feature type" status note.
                if (FeatureGeometry.Translated(source, DuplicateOffset, DuplicateOffset) is not { } clone) return null;
                // A feature Name is optional and unique-when-set (round 5), but AddFeatureCommand carries no
                // add-time guard for that (only RenameFeatureCommand does), so a straight clone of a named
                // feature would silently collide. Uniquify it, an unnamed feature's null Name carries no key to
                // collide on and needs no change.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", FeatureNameExists);
                _document.Execute(new AddFeatureCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Terrain.Features.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.Feature, key);
                return new DuplicateResult(SelectionKind.Feature, key);
            }
            case SelectionKind.Exclusion:
            {
                if (!TryExclusionIndex(sel.Id, out int ei)) return null;
                MapExclusion source = _document.Doc.Exclusions[ei];
                var clone = new MapExclusion
                {
                    Name = source.Name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    Layers = source.Layers is { } layers ? new List<string>(layers) : null,
                };
                // Same round-5 name-collision dodge as Feature above: AddExclusionCommand has no add-time guard.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", ExclusionNameExists);
                _document.Execute(new AddExclusionCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Exclusions.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.Exclusion, key);
                return new DuplicateResult(SelectionKind.Exclusion, key);
            }
            case SelectionKind.ScatterOverride:
            {
                if (!TryScatterOverrideIndex(sel.Id, out int oi)) return null;
                MapScatterOverrideDoc source = _document.Doc.ScatterOverrides[oi];
                var clone = new MapScatterOverrideDoc
                {
                    Name = source.Name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    DensityMultiplier = source.DensityMultiplier,
                    // Fresh lists AND fresh MapPropKind elements. EditScatterOverrideValuesCommand's own Clone copies
                    // the Kinds list but shares its elements by reference, so a straight reuse of that discipline
                    // here would leave the clone's kinds aliasing the source's. Rebuild each element (CloneKinds) so
                    // a later scrub of the duplicate's kind mix can never mutate the original's.
                    Kinds = source.Kinds is { } kinds ? CloneKinds(kinds) : null,
                    Layers = source.Layers is { } layers ? new List<string>(layers) : null,
                };
                // Same round-5 name-collision dodge as Feature / Exclusion: AddScatterOverrideCommand has no add-time
                // name guard (only RenameScatterOverrideCommand does), so a named clone uniquifies itself here. An
                // unnamed override's null Name carries no key to collide on and needs no change.
                if (!string.IsNullOrEmpty(clone.Name))
                    clone.Name = UniqueName(clone.Name + "-copy", ScatterOverrideNameExists);
                _document.Execute(new AddScatterOverrideCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.ScatterOverrides.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.ScatterOverride, key);
                return new DuplicateResult(SelectionKind.ScatterOverride, key);
            }
            case SelectionKind.Region:
            {
                if (RegionByName(sel.Id) is not { } source) return null;
                // A region's Name IS its identity (like a placement id), always set and always unique, so a
                // duplicate takes a fresh generated name exactly like a freshly drawn region rather than deriving
                // one from the source name.
                string name = UniqueName("region", RegionExists);
                var clone = new MapRegion
                {
                    Name = name,
                    Shape = source.Shape is { } shape ? CloneShapeOffset(shape, DuplicateOffset, DuplicateOffset) : null,
                    Tags = new List<string>(source.Tags),
                };
                _document.Execute(new AddRegionCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.Region, name);
                return new DuplicateResult(SelectionKind.Region, name);
            }
            case SelectionKind.BiomeBand:
            {
                if (!TryListIndex(sel.Id, _document.Doc.Terrain.Biomes.Count, out int bi)) return null;
                MapBiomeBand source = _document.Doc.Terrain.Biomes[bi];
                // No name, no position (a band is an elevation range, not a placed element): a plain verbatim
                // clone, no uniquify, no offset.
                var clone = new MapBiomeBand
                {
                    Start = source.Start, End = source.End, Biome = source.Biome,
                    BaseHeight = source.BaseHeight, HillAmplitude = source.HillAmplitude,
                };
                _document.Execute(new AddBiomeBandCommand(clone));
                _document.SealGesture();
                int idx = _document.Doc.Terrain.Biomes.Count - 1;
                string key = idx.ToString(CultureInfo.InvariantCulture);
                _document.Selection.Set(SelectionKind.BiomeBand, key);
                return new DuplicateResult(SelectionKind.BiomeBand, key);
            }
            case SelectionKind.ScatterLayer:
            {
                if (ScatterLayerByName(sel.Id) is not { } source) return null;
                MapScatterLayer clone = MapEditorScene.CloneScatterLayer(source);
                clone.Name = UniqueName(source.Name + "-copy", ScatterLayerNameExists);
                _document.Execute(new AddScatterLayerCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.ScatterLayer, clone.Name);
                return new DuplicateResult(SelectionKind.ScatterLayer, clone.Name);
            }
            case SelectionKind.CompanionLayer:
            {
                if (CompanionLayerByName(sel.Id) is not { } source) return null;
                MapCompanionLayer clone = MapEditorScene.CloneCompanionLayer(source);
                clone.Name = UniqueName(source.Name + "-copy", CompanionLayerNameExists);
                _document.Execute(new AddCompanionLayerCommand(clone));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.CompanionLayer, clone.Name);
                return new DuplicateResult(SelectionKind.CompanionLayer, clone.Name);
            }
            default:
                return null;   // Terrain (a singleton) and an empty selection: nothing to duplicate.
        }
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

    MapPlayerSpawn? FindPlayerSpawn(string id)
    {
        foreach (MapPlayerSpawn s in _document.Doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
        return null;
    }

    bool PlacementIdExists(string id) => FindPlacement(id) is not null;

    bool SpawnIdExists(string id) => FindSpawn(id) is not null;

    bool PlayerSpawnIdExists(string id) => FindPlayerSpawn(id) is not null;

    bool RegionExists(string name) => RegionByName(name) is not null;

    MapRegion? RegionByName(string name)
    {
        foreach (MapRegion r in _document.Doc.Regions)
            if (string.Equals(r.Name, name, StringComparison.Ordinal)) return r;
        return null;
    }

    // The feature at the index a Feature-selection id encodes, or null when the id is not a valid in-range index.
    MapFeature? FeatureAt(string id) =>
        TryFeatureIndex(id, out int i) ? _document.Doc.Terrain.Features[i] : null;

    // Parses a Feature id to a valid in-range feature index.
    bool TryFeatureIndex(string id, out int index) =>
        TryListIndex(id, _document.Doc.Terrain.Features.Count, out index);

    // The shape of the exclusion at the index an Exclusion-selection id encodes, or null when out of range.
    MapShapeDoc? ExclusionShape(string id) =>
        TryExclusionIndex(id, out int i) ? _document.Doc.Exclusions[i].Shape : null;

    // Parses an Exclusion id to a valid in-range exclusion index.
    bool TryExclusionIndex(string id, out int index) =>
        TryListIndex(id, _document.Doc.Exclusions.Count, out index);

    // The shape of the scatter override at the index a ScatterOverride-selection id encodes, or null when out of
    // range. Index-keyed exactly like ExclusionShape above.
    MapShapeDoc? ScatterOverrideShape(string id) =>
        TryScatterOverrideIndex(id, out int i) ? _document.Doc.ScatterOverrides[i].Shape : null;

    // Parses a ScatterOverride id to a valid in-range scatter override index.
    bool TryScatterOverrideIndex(string id, out int index) =>
        TryListIndex(id, _document.Doc.ScatterOverrides.Count, out index);

    // Whether any CURRENT terrain feature carries `name` (ordinal): AddFeatureCommand has no add-time name
    // guard (only RenameFeatureCommand does), so DuplicateSelection uses this to uniquify a named clone itself.
    bool FeatureNameExists(string name)
    {
        foreach (MapFeature f in _document.Doc.Terrain.Features)
            if (string.Equals(f.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // Whether any CURRENT exclusion carries `name` (ordinal): same add-time gap as FeatureNameExists above.
    bool ExclusionNameExists(string name)
    {
        foreach (MapExclusion e in _document.Doc.Exclusions)
            if (string.Equals(e.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // Whether any CURRENT scatter override carries `name` (ordinal): same add-time gap as ExclusionNameExists, so
    // DuplicateSelection uniquifies a named clone itself.
    bool ScatterOverrideNameExists(string name)
    {
        foreach (MapScatterOverrideDoc o in _document.Doc.ScatterOverrides)
            if (string.Equals(o.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    MapScatterLayer? ScatterLayerByName(string name)
    {
        foreach (MapScatterLayer l in _document.Doc.ScatterLayers)
            if (string.Equals(l.Name, name, StringComparison.Ordinal)) return l;
        return null;
    }

    MapCompanionLayer? CompanionLayerByName(string name)
    {
        foreach (MapCompanionLayer l in _document.Doc.CompanionLayers)
            if (string.Equals(l.Name, name, StringComparison.Ordinal)) return l;
        return null;
    }

    bool ScatterLayerNameExists(string name) => ScatterLayerByName(name) is not null;

    bool CompanionLayerNameExists(string name) => CompanionLayerByName(name) is not null;

    // Deep-clones a shape DTO (disc / rect / polygon), offset by (dx, dz) on the XZ plane, for a duplicated
    // exclusion / region (decision 8). Unlike MapEditorScene's shape-row CloneShape (editable disc / rect only,
    // a polygon row is read-only v1 there), Duplicate must round-trip every shape kind a document can legally
    // hold, so this is a local clone rather than a reuse: a hand-authored polygon exclusion or region is a legal
    // duplicate target even though the inspector cannot edit its points. Internal (not private), like
    // MapEditorScene.CloneScatterLayer / CloneCompanionLayer, so MutationService.ElementDuplicate (the MCP
    // element_duplicate verb, KhaozEngine.MapEdit.Tool, same InternalsVisibleTo grant) reuses this exact
    // polygon-aware clone rather than a second copy that could drift from the GUI's own duplicate.
    internal static MapShapeDoc CloneShapeOffset(MapShapeDoc shape, float dx, float dz)
    {
        switch (shape)
        {
            case DiscShapeDoc d:
                return new DiscShapeDoc { CenterX = d.CenterX + dx, CenterZ = d.CenterZ + dz, Radius = d.Radius };
            case RectShapeDoc r:
                return new RectShapeDoc
                {
                    MinX = r.MinX + dx, MinZ = r.MinZ + dz, MaxX = r.MaxX + dx, MaxZ = r.MaxZ + dz,
                };
            case PolygonShapeDoc poly:
            {
                var clone = new PolygonShapeDoc();
                foreach (float[] point in poly.Points)
                {
                    float x = point.Length > 0 ? point[0] : 0f;
                    float z = point.Length > 1 ? point[1] : 0f;
                    clone.Points.Add(new[] { x + dx, z + dz });
                }
                return clone;
            }
            default:
                throw new InvalidOperationException($"No clone support for shape type '{shape.GetType().Name}'.");
        }
    }

    // Deep-clones a scatter override's Kinds list, rebuilding each MapPropKind so the copy shares no element with
    // the source. Needed because EditScatterOverrideValuesCommand's own Clone copies the list container but shares
    // its elements by reference, so a duplicate that relied on that discipline would alias the source's kinds.
    static List<MapPropKind> CloneKinds(List<MapPropKind> kinds)
    {
        var copy = new List<MapPropKind>(kinds.Count);
        foreach (MapPropKind k in kinds) copy.Add(new MapPropKind { Id = k.Id, Weight = k.Weight });
        return copy;
    }

    // The live shape of the current exclusion / scatter override / region selection, or null for any other kind.
    MapShapeDoc? SelectedShapeOf(SelectionKind kind, string id) => kind switch
    {
        SelectionKind.Exclusion => ExclusionShape(id),
        SelectionKind.ScatterOverride => ScatterOverrideShape(id),
        SelectionKind.Region => RegionByName(id)?.Shape,
        _ => null,
    };

    // Parses an index-string selection id and range-checks it against a list count.
    static bool TryListIndex(string id, int count, out int index) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && index >= 0 && index < count;

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
