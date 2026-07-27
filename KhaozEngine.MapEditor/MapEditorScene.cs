using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>The turn-key in-engine map editor scene a per-game head pushes onto its <see cref="SceneManager"/>:
/// it wires a <see cref="ViewportWorld"/> + fly camera + <see cref="EditorToolController"/> together with the Gui
/// chrome (toolbar tab bar, tree outline, property-grid inspector, kit palette, status strip) and the undo / redo
/// / save hotkeys, over one <see cref="EditorDocument"/>. Shift+Escape opens the modal exit dialog (a scene-owned
/// <see cref="PopupPanel"/>): a dirty document offers Save and Close / Save / Discard / Cancel, a clean one just
/// Close / Cancel, and while it is open every other editor chord, tool pick, and camera step is suppressed
/// (Escape alone stays the gesture cancel when the dialog is closed). Leaving the editor goes through
/// <see cref="MapEditorOptions.RequestQuit"/> when the editor is the bottom scene, otherwise it pops.
/// Developer tooling, so the whole class is
/// <see cref="LocalizationExemptAttribute">localization-exempt</see>.
/// <para>The GPU-touching work lives behind the <see cref="BuildWorld"/> / <see cref="TeardownWorld"/> /
/// <see cref="CheckWorldRebuild"/> / <see cref="UpdateStreaming"/> seams (the Task 5 pattern), and the per-frame
/// step order is exposed through overridable <see cref="UpdateCamera"/> / <see cref="UpdateTools"/> seams, so the
/// lifecycle guards, update ordering, and save-failure handling are all headless-testable.</para>
/// </summary>
[LocalizationExempt]
public partial class MapEditorScene : GameScene, IGameScene3D
{
    /// <summary>Toolbar height in points.</summary>
    const float ToolbarHeight = 40f;
    /// <summary>Width in points reserved at the right end of the toolbar for the Save button (decision 4).</summary>
    const float SaveButtonWidth = 96f;
    /// <summary>Height in points of the toolbar Save button (inset vertically within <see cref="ToolbarHeight"/>).</summary>
    const float SaveButtonHeight = 28f;
    /// <summary>Gap in points between the tab bar, the Save button, and the toolbar's right edge.</summary>
    const float ToolbarGap = 6f;
    /// <summary>Left side-panel width in points (the outline / palette column).</summary>
    const float OutlinePanelWidth = 260f;
    /// <summary>Right side-panel width in points (the inspector column). Wider than <see cref="OutlinePanelWidth"/>
    /// so a scrubbed <see cref="NumberField"/> label and its value both read comfortably at
    /// <see cref="PropertyGrid.LabelFraction"/>'s default split.</summary>
    const float InspectorPanelWidth = 340f;
    /// <summary>Status-strip height in points.</summary>
    const float StatusHeight = 26f;
    /// <summary>Height in points of the filter box slotted at the top of the palette / spawn-list region.</summary>
    const float PaletteFilterHeight = 26f;
    /// <summary>Left inset in points between the status strip's edge and the status text (see <see cref="TruncateStatusLine"/>,
    /// which reserves this on both sides when fitting the line to the strip).</summary>
    const float StatusTextInset = 8f;
    /// <summary>Falls back to this world-space box height for a kit id absent from the manifests.</summary>
    const float FallbackKindHeight = 2f;

    // A touch lighter than a flat black panel so the chrome reads as a raised card over the viewport, matching
    // GuiStyle.Modern's own lifted-surface look (see EditorStyle below). Colors are not pinned exact in tests
    // (decision 6): only the "stays dark" intent and the DrawRounded call shape are.
    static readonly Color PanelBackground = new(0.115f, 0.12f, 0.165f, 0.95f);
    static readonly Color StatusBackground = new(0.075f, 0.08f, 0.105f, 0.97f);
    /// <summary>Corner radius (points) for the rounded chrome panel fills, derived from <see cref="GuiStyle.Modern"/>
    /// so the editor chrome and its inspector widgets read as one consistent style.</summary>
    static readonly float PanelCornerRadius = GuiStyle.Modern.CornerRadius;
    static readonly Color SelectionHighlight = new(1.35f, 1.2f, 0.7f, 1f);

    // Viewport overlay fills: a translucent ground disc/rect/fan per exclusion (red-ish), scatter override (orange),
    // and region (blue-ish), and a small marker disc at each terrain-feature center (amber). The selected element's
    // fill brightens (see Tint). The scatter override orange sits between the exclusion red and the feature amber but
    // stays clearly distinct from both (a lower green than the amber marker, a warmer hue than the red exclusion).
    static readonly Color ExclusionOverlayColor = new(0.9f, 0.22f, 0.16f, 0.26f);
    static readonly Color ScatterOverrideOverlayColor = new(0.98f, 0.52f, 0.1f, 0.28f);
    static readonly Color RegionOverlayColor = new(0.2f, 0.5f, 0.95f, 0.26f);
    static readonly Color FeatureOverlayColor = new(0.96f, 0.76f, 0.22f, 0.55f);
    // The selected biome band's world-Z edge lines: a bright magenta, distinct from every fill hue above (its blue
    // is high like the region's, but its red is far higher, so it never reads as a region). A thin line reads
    // faint, so the alpha runs higher than the translucent area fills.
    static readonly Color BiomeBandOverlayColor = new(0.85f, 0.3f, 0.95f, 0.6f);
    /// <summary>Half-thickness (m, along world Z) of a biome-band edge line drawn as a thin overlay quad. Wide
    /// enough to read as a line at typical camera distances, far narrower than the band widths it delimits.</summary>
    const float BiomeBandLineHalfDepth = 0.4f;
    /// <summary>World-space lift (m) added above the sampled ground height when seating an overlay fill. Overlays
    /// never z-fight the terrain regardless: the debug-fill pass runs depth-disabled after post, so the fills
    /// composite on top of the scene rather than depth-testing against it. The lift only keeps the fill geometry a
    /// touch above the sampled surface.</summary>
    const float OverlayLift = 0.1f;
    /// <summary>RGB scale applied to a selected overlay's fill so it reads brighter than its neighbours.</summary>
    const float OverlaySelectBrighten = 1.6f;
    /// <summary>Alpha multiplier applied to a selected overlay's fill (clamped to 1) so it also firms up.</summary>
    const float OverlaySelectAlphaBoost = 1.7f;

    // Pre-built gizmo mesh sets returned by ComputeGizmoMeshes, avoiding per-frame allocations.
    static readonly GizmoMesh[] FullGizmoMeshes = new[] { GizmoMesh.TranslateArrowsFull, GizmoMesh.YawRing, GizmoMesh.ScaleHandle };
    static readonly GizmoMesh[] MoveScaleRotateGizmoMeshes = new[] { GizmoMesh.TranslateArrowsXZ, GizmoMesh.YawRing, GizmoMesh.ScaleHandle };
    static readonly GizmoMesh[] MoveScaleGizmoMeshes = new[] { GizmoMesh.TranslateArrowsXZ, GizmoMesh.ScaleHandle };
    static readonly GizmoMesh[] MarkerGizmoMeshes = new[] { GizmoMesh.SelectionMarker, GizmoMesh.TranslateArrowsXZ };
    static readonly GizmoMesh[] NoneGizmoMeshes = Array.Empty<GizmoMesh>();

    Scene3D _scene = null!;
    Texture2D _white = null!;
    DpiFont _font = null!;
    MapEditorOptions _options = null!;

    EditorDocument _document = null!;
    EditorToolController _controller = null!;
    ViewportWorld _viewport = null!;
    readonly EditorVisibility _visibility = new();
    FlyCamera3D _camera = null!;
    FlyCameraController _camController = null!;

    // Session-only camera bookmarks (decision 9): index 0 = slot 1 ... index 8 = slot 9. Never persisted, so a
    // fresh editor session starts with every slot empty (CameraBookmark.Set false, the struct's default).
    readonly CameraBookmark[] _bookmarks = new CameraBookmark[9];

    MeshHandle _translateArrows, _translateArrowsXZ, _yawRing, _scaleHandle, _selectionMarker;

    readonly InputManager _ui = new();
    TabBar _toolbar = null!;
    TreeView _outline = null!;
    PropertyGrid _inspector = null!;
    // The inspector's hover tooltip, built lazily on first draw (see DrawInspectorTooltip): BuildChrome runs
    // before a UiViewport exists, so no SpriteFont is resolvable yet at chrome-build time. Rebuilt whenever the
    // resolved SpriteFont instance changes (a DPI rebake, see DpiFont.For), since Tooltip's fonts are fixed at
    // construction: _tooltipFont tracks which instance _tooltip was last built with.
    Tooltip? _tooltip;
    SpriteFont? _tooltipFont;

    // Kit palette: a filter box above a category-grouped, collapsible tree. Spawn archetypes: a filter box above a
    // flat list (a TreeView with leaf-only roots, so it renders and hit-tests exactly like the palette minus the
    // categories). The two share the bottom-left panel region, swapped by the active tool (spawn tool -> spawn list,
    // everything else -> kit palette), so each filter box slots into the existing side-panel bounds cleanly.
    TextInput _paletteFilter = null!;
    TreeView _paletteTree = null!;
    TextInput _spawnFilter = null!;
    TreeView _spawnList = null!;
    // The feature-type picker: a flat leaf-only TreeView of the registry's feature types, shown in the bottom-left
    // panel only in the EditFeature tool. A leaf tap sets the controller's PlaceFeatureType. No filter box (the
    // registered feature set is small and static), so it fills the whole panel region.
    TreeView _featureList = null!;

    // The grouped, twice-sorted palette source (categories ordinal, kit ids ordinal within each), parsed once from
    // KindCategories in OnEnter (the map is immutable after ViewportWorld construction) and re-filtered without
    // rebuilding. Per-category expansion is remembered across rebuilds so clearing a filter restores the tree.
    readonly List<PaletteCategory> _paletteSource = new();
    readonly Dictionary<string, bool> _paletteExpansion = new(StringComparer.Ordinal);
    string _paletteTreeFilter = "";   // the TRIMMED filter text the live palette tree was last built for
    string _spawnTreeFilter = "";     // the TRIMMED filter text the live spawn list was last built for

    // The controller mode UpdateChrome last saw, so it can detect a swap and drop a now-hidden filter's stale
    // focus (see the UpdateChrome block that compares this every frame). Defaults to Select, matching the
    // controller's own default Mode, so the very first chrome step never misfires an unfocus.
    EditorToolMode _lastChromeMode;

    bool _built;
    string _statusText = "";

    // Seconds accumulated toward the next throttled full rebuild while a drag or draw gesture is live (see
    // CheckWorldRebuild). Reset to 0 on every full rebuild that actually runs, gesture or not, so a gesture always
    // starts its throttling window fresh from whenever the last full rebuild landed. Never touched by the partial
    // path, so a run of partial rebuilds mid-gesture cannot starve the eventual full one.
    float _gestureRebuildAccumulator;

    // The modal exit dialog (decision 3), non-null only while it is open. Shift+Escape builds a fresh one keyed to
    // the current dirty state. Its footer-button callbacks save / quit / dismiss. While non-null it is drawn last
    // (over the tooltip) and updated FIRST each frame, and OnUpdate gates every other editor step off it, so the
    // editor beneath is frozen behind the scrim.
    PopupPanel? _exitDialog;

    // The toolbar Save button (decision 4), at the right end of the strip after the tab bar. Its label carries the
    // dirty marker ("Save*" dirty, "Save" clean), re-synced every chrome step (outside the UiViewport guard, so it
    // tracks the document even headless), and its click fires SaveDocument.
    Button _saveButton = null!;

    // Inline-rename bookkeeping, shared by the region / placement / spawn inspectors: those selections are keyed
    // by name (region) or id (placement, spawn), so after a rename the selection must follow the new key. The
    // re-select is deferred until the rename row loses focus (an immediate Selection.Set would rebuild the
    // inspector mid-typing and drop the field's focus per keystroke). Only one inspector row is ever focused, so
    // a single pending slot covers all three renamable kinds.
    TextRow? _nameRow;
    SelectionKind _pendingSelectKind;
    string? _pendingSelectId;

    // The kind string ("disc"/"rect"/...) the current inspector's shape rows were built for, or null when the
    // inspector holds no shape rows. Compared each chrome step against the live selected shape so a kind
    // conversion (or an undo/redo of one) swaps the param rows: see SyncShapeInspector.
    string? _inspectorShapeKind;

    // Whether the current exclusion inspector's layer rows were built with "All layers" on (Layers null), or
    // null when the inspector holds no exclusion layer rows. Compared each chrome step against the live
    // exclusion's Layers so an All-toggle (or an undo/redo of one) reflows the per-layer membership rows into
    // or out of view: see SyncShapeInspector.
    bool? _inspectorLayersAllOn;

    // The scatter-layer name list the CURRENT inspector's rows depend on, captured at build time, or null when
    // the inspector holds no scatter-name-dependent rows. The exclusion inspector's per-layer targeting rows and
    // the companion inspector's HostLayer chooser both enumerate the live scatter layers, so those rows go stale
    // when a scatter layer is added / removed / renamed while an exclusion or companion is selected. SyncShapeInspector
    // compares this snapshot against the live names each chrome step and rebuilds on a mismatch, so the rows never
    // show a stale layer set (the Task 2 review carry-forward). Order-sensitive: a rename reorders nothing, but a
    // straight sequence compare is enough since the outline and rows enumerate the same list order.
    List<string>? _inspectorScatterNames;

    // The scatter layer's Rules count the CURRENT scatter-layer inspector built its per-rule rows for, or null
    // when the inspector is not a scatter-layer inspector. Adding / removing a rule (the crude v1 rule buttons)
    // changes the count without changing the selection, so SyncShapeInspector rebuilds the inspector on a mismatch
    // to reflow the per-rule rows (the same deferred-rebuild discipline as the exclusion layer-row reflow, so no
    // rebuild ever runs inside the grid's row iteration).
    int? _inspectorRuleCount;

    // Whether the CURRENT companion inspector was built showing the host-kinds mismatch warning row, or null when
    // the inspector is not a companion inspector. The mismatch state (non-empty HostKinds with zero intersection
    // against the host layer's rule kinds) flips when HostKinds is edited, the host is swapped, or an undo / redo
    // moves either, none of which rebuild the inspector on their own, so SyncShapeInspector compares this snapshot
    // against the live mismatch each chrome step and rebuilds when it changes, so the warning row appears / hides
    // live (the _inspectorScatterNames deferred-rebuild idiom).
    bool? _inspectorCompanionMismatch;

    /// <summary>Wires the render surface, the shared white pixel and UI font, and the editor options, then returns
    /// this for chaining (the Room3D Init-injection pattern). Nothing is dereferenced until <see cref="OnEnter"/>.</summary>
    public MapEditorScene Init(Scene3D scene, Texture2D white, DpiFont font, MapEditorOptions options)
    {
        _scene = scene;
        _white = white;
        _font = font;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>The status-strip text (mode, dirty flag, undo/redo labels, last save result). Exposed for tests.</summary>
    internal string StatusText => _statusText;

    /// <summary>The editor document, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal EditorDocument Document => _document;

    /// <summary>The tool controller, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal EditorToolController Controller => _controller;

    /// <summary>The inspector grid, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal PropertyGrid Inspector => _inspector;

    /// <summary>The tree outline, or null before <see cref="OnEnter"/>. Exposed for tests (a hidden element stays
    /// listed here: visibility is view-only and never mutates the document).</summary>
    internal TreeView Outline => _outline;

    /// <summary>The editor-session visibility state (groups, scatter layers, per-element hides). Exposed for tests.</summary>
    internal EditorVisibility Visibility => _visibility;

    /// <summary>The mode tab bar, or null before <see cref="OnEnter"/>. Exposed for tests (the selected tab
    /// tracks the controller mode, including one-shot returns to Select).</summary>
    internal TabBar Toolbar => _toolbar;

    /// <summary>The category-grouped kit palette tree, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TreeView PaletteTree => _paletteTree;

    /// <summary>The kit-palette filter box, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TextInput PaletteFilter => _paletteFilter;

    /// <summary>The flat spawn-archetype list (leaf-only roots), or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TreeView SpawnList => _spawnList;

    /// <summary>The spawn-archetype filter box, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TextInput SpawnFilter => _spawnFilter;

    /// <summary>The flat feature-type picker list (leaf-only roots), or null before <see cref="OnEnter"/>. Exposed
    /// for tests.</summary>
    internal TreeView FeatureList => _featureList;

    /// <summary>The modal exit dialog while it is open, or null when it is closed. Exposed for tests (assert the
    /// footer actions per dirty state, or fire one directly).</summary>
    internal PopupPanel? ExitDialog => _exitDialog;

    /// <summary>The toolbar Save button, or null before <see cref="OnEnter"/>. Exposed for tests (its label tracks
    /// the dirty flag and its click saves).</summary>
    internal Button SaveButton => _saveButton;

    /// <summary>The fly camera, or null before <see cref="OnEnter"/>. Exposed for tests (assert the command-modifier
    /// camera suppression leaves the position untouched).</summary>
    internal FlyCamera3D Camera => _camera;

    // ---- lifecycle ---------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void OnEnter()
    {
        if (_built) return;

        MapDocRegistry registry = _options.Registry ?? MapDocRegistry.CreateDefault();
        _document = new EditorDocument(CreateDocument(registry), registry);
        _controller = new EditorToolController(_document)
        {
            HeightOf = KindHeight,
            IsVisible = _visibility.IsElementVisible,
        };
        _viewport = new ViewportWorld(_scene, _options.ManifestPaths)
        {
            ScatterLayerVisible = _visibility.GetLayer,
            TexturedPropsEnabled = () => _options.TexturedProps,
        };
        _camera = new FlyCamera3D { Position = new Vector3(0f, 24f, -32f), Pitch = -0.5f };
        _camController = new FlyCameraController(_camera);

        BuildChrome();
        BuildPaletteSource(PaletteKindCategories());
        RebuildPaletteTree("");   // full tree, every category expanded
        RebuildSpawnList("");     // full flat list
        RebuildFeatureList();     // flat feature-type picker
        if (_options.SpawnArchetypes.Count > 0) _controller.SpawnArchetype = _options.SpawnArchetypes[0];
        _controller.PlaceKind = DefaultPlaceKind();
        _controller.PlaceFeatureType = DefaultFeatureType();

        _document.DocumentChanged += OnDocumentChanged;
        _document.CommandApplied += OnCommandVisibilityForward;
        _document.CommandRedone += OnCommandVisibilityForward;
        _document.CommandUndone += OnCommandVisibilityInverse;
        _document.Selection.Changed += OnSelectionChanged;

        BuildWorld();
        RebuildOutline();
        RebuildInspector();
        _exitDialog = null;   // a re-entered scene starts with no open exit dialog
        _built = true;
    }

    /// <inheritdoc/>
    public override void OnExit()
    {
        if (!_built) return;
        _built = false;
        _document.DocumentChanged -= OnDocumentChanged;
        _document.CommandApplied -= OnCommandVisibilityForward;
        _document.CommandRedone -= OnCommandVisibilityForward;
        _document.CommandUndone -= OnCommandVisibilityInverse;
        _document.Selection.Changed -= OnSelectionChanged;
        TeardownWorld();
    }

    /// <summary>GPU seam: builds the streamed viewport world, uploads the gizmo meshes, points the controller at
    /// the built field, and installs the fly camera. Overridden headless in tests to skip all device work.</summary>
    protected virtual void BuildWorld()
    {
        _viewport.Build(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;

        _translateArrows = _scene.LoadMesh(GizmoGeometry.TranslateArrows());
        _translateArrowsXZ = _scene.LoadMesh(GizmoGeometry.TranslateArrowsXZ());
        _yawRing = _scene.LoadMesh(GizmoGeometry.YawRing());
        _scaleHandle = _scene.LoadMesh(GizmoGeometry.ScaleHandle());
        _selectionMarker = _scene.LoadMesh(GizmoGeometry.SelectionMarker());

        _scene.CameraOverride = _camera;
    }

    /// <summary>GPU seam: frees the viewport world, gizmo meshes, and the camera override. Overridden headless in
    /// tests. Idempotent-safe: <see cref="OnExit"/> guards it behind the built flag.</summary>
    protected virtual void TeardownWorld()
    {
        _scene.CameraOverride = null;
        _viewport.Dispose();
        _scene.UnloadMesh(_translateArrows);
        _scene.UnloadMesh(_translateArrowsXZ);
        _scene.UnloadMesh(_yawRing);
        _scene.UnloadMesh(_scaleHandle);
        _scene.UnloadMesh(_selectionMarker);
    }

    // ---- per-frame ---------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void OnUpdate(float dt)
    {
        if (!_built) return;
        // A modal exit dialog owns the frame: it updates FIRST (its keyboard + pointer-block route Esc/Enter and
        // clicks to its own buttons) and every other editor step is skipped, so no chord, tool pick, or camera
        // move leaks through to the frozen editor beneath the scrim (decision 3).
        if (_exitDialog is not null) { UpdateExitDialog(dt); return; }
        UpdateCamera(dt);
        UpdateTools(dt);
        UpdateChrome(dt);
        CheckWorldRebuild(dt);
        UpdateStreaming(dt);
    }

    /// <summary>Fly-camera step (aspect upkeep + WASD/mouselook). Overridable for headless order tests.</summary>
    protected virtual void UpdateCamera(float dt)
    {
        int w = Manager!.Input.Width, h = Manager.Input.Height;
        if (h > 0) _camera.AspectRatio = (float)w / h;
        // Skip the fly step while a command modifier is held (decision 5): Cmd+S / Cmd+D and friends carry a WASD
        // letter, and running the fly camera on those frames nudges the view one frame per chord. The aspect upkeep
        // above still runs so a resize during a held modifier is not missed.
        if (Manager.Input.IsCommandDown) return;
        _camController.Update(Manager.Input, dt);
    }

    /// <summary>Tool step: builds the frame input from the camera + pointer and advances the controller.
    /// Overridable for headless order tests. No-ops until the world is built (the field is set).</summary>
    protected virtual void UpdateTools(float dt)
    {
        if (_controller.Field is null) return;
        if (_controller.TryGizmo(out Vector3 gizmoPos) != GizmoAffordance.None)
            _controller.GizmoScale = GizmoScaleFor(gizmoPos);
        _controller.Update(BuildFrameInput(dt));
    }

    /// <summary>Consumes a pending world rebuild after every edit source this frame (tools, then chrome, which
    /// covers the property-grid inspector), so an edit from either one lands in the streamed world before the
    /// next frame's pick. A pending edit that reported a bounded region
    /// (<see cref="EditorDocument.PendingRebuildRegion"/>) rebuilds ONLY the chunks that region overlaps via
    /// <see cref="PartialRebuildWorld"/>, never throttled (it is cheap by construction). A null region (a
    /// whole-world edit, or the partial path declining because the world is not built) falls through to the full
    /// <see cref="RebuildWorld"/>, which IS throttled while a drag or draw gesture is live
    /// (<see cref="EditorToolController.IsDragging"/> / <see cref="EditorToolController.IsDrawing"/>): a full
    /// rebuild only runs once <see cref="MapEditorOptions.GestureRebuildInterval"/> seconds have accumulated since
    /// the last one, so a fast mid-gesture edit stream does not re-mesh the whole world every frame. The pending
    /// flag is left untouched on a throttled-skip frame (not acknowledged), so the very next check after the
    /// gesture ends falls straight through to the unthrottled branch and performs the final full rebuild with no
    /// extra plumbing. Either way a rebuild that actually ran is acknowledged so it fires once. Overridable for
    /// headless order tests, and it dispatches through the two rebuild seams so a headless test can observe the
    /// routing without a device.</summary>
    protected virtual void CheckWorldRebuild(float dt)
    {
        if (!_document.WorldRebuildPending) return;
        if (_document.PendingRebuildRegion is RectArea dirty && PartialRebuildWorld(dirty))
        {
            _document.AcknowledgeWorldRebuild();
            return;
        }

        if (_controller.IsDragging || _controller.IsDrawing)
        {
            _gestureRebuildAccumulator += dt;
            if (_gestureRebuildAccumulator < _options.GestureRebuildInterval) return;   // throttled: stays pending
        }

        if (RebuildWorld())
        {
            _document.AcknowledgeWorldRebuild();
            _gestureRebuildAccumulator = 0f;
        }
    }

    /// <summary>Partial-rebuild seam: re-mesh only the loaded chunks overlapping <paramref name="dirty"/> and
    /// re-point the tool controller at the swapped field. Returns false when the viewport is not built (the
    /// <see cref="ViewportWorld.PartialRebuild"/> not-built contract), so <see cref="CheckWorldRebuild"/> falls back
    /// to a full rebuild. Overridable so a headless test can observe the dispatch without a device.</summary>
    protected virtual bool PartialRebuildWorld(RectArea dirty)
    {
        if (!_viewport.PartialRebuild(_document.Doc, _document.Registry, dirty)) return false;
        _controller.Field = _viewport.Field;
        return true;
    }

    /// <summary>Full-rebuild seam for a pending edit with no bounded region: rebuild the whole streamed world and
    /// re-point the tool controller at the fresh field. Returns false (a no-op) when the viewport is not built, so
    /// <see cref="CheckWorldRebuild"/> leaves the rebuild pending rather than throwing. Overridable so a headless
    /// test can observe the dispatch without a device. <see cref="CheckWorldRebuild"/> wraps its gesture throttle
    /// around this full path only, never around <see cref="PartialRebuildWorld"/>.</summary>
    protected virtual bool RebuildWorld()
    {
        if (!_viewport.IsBuilt) return false;
        _viewport.Rebuild(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;
        return true;
    }

    /// <summary>Hotkeys + Gui-chrome input step. Overridable for headless order tests.</summary>
    protected virtual void UpdateChrome(float dt)
    {
        HandleShortcuts();
        UpdateWidgets(dt);

        // The mode tab bar is driven one-way in UpdateWidgets (a tap sets the controller mode). But the
        // controller can also change mode on its own, which that tap never sees: a one-shot draw / bake tool
        // returning to Select on completion, or Escape cancelling a gesture. Mirror the live controller mode back
        // onto the tab selection every frame so the highlighted tab always tracks the active tool. Runs outside
        // UpdateWidgets' UiViewport guard so the toolbar stays in sync even headless, and ActiveIndex's setter
        // never raises a change event, so this cannot loop back into a mode switch.
        _toolbar.ActiveIndex = (int)_controller.Mode;

        // Drop a filter's focus the moment the mode swap that just happened hides its panel: TextInput.Unfocus
        // normally only runs inside the filter's own Update call in UpdateWidgets, which only runs for the
        // mode's CURRENTLY visible filter (see AnyEditorFocused), so a filter focused while its panel was up
        // would otherwise keep IsFocused stuck true forever once the panel hides - not just a toolbar tap (the
        // controller can also change mode on its own, same as the ActiveIndex sync above). Compared every frame
        // outside UpdateWidgets' UiViewport guard so this fixes the stale bit even headless. AnyEditorFocused's
        // own mode gate stays in place too, as defense in depth.
        if (_controller.Mode != _lastChromeMode)
        {
            if (!KitPaletteVisible) _paletteFilter.Unfocus();
            if (!SpawnMode) _spawnFilter.Unfocus();
            // Entering or leaving sculpt swaps the inspector between the brush panel and the selection panel, which
            // only a selection change would otherwise rebuild.
            if ((_lastChromeMode == EditorToolMode.SculptTerrain) != (_controller.Mode == EditorToolMode.SculptTerrain))
                RebuildInspector();
            _lastChromeMode = _controller.Mode;
        }

        // Keep the toolbar Save button's label tracking the dirty flag, also outside the UiViewport guard so a
        // headless test sees "Save" / "Save*" flip without a live viewport (decision 4).
        _saveButton.Content = LocalizedText.Raw(_document.IsDirty ? "Save*" : "Save");

        // Sync the selection to a renamed element (region by name, placement/spawn by id) once the rename row is
        // done (outside the grid's row iteration, so the inspector rebuild this triggers never tears down a row
        // mid-update).
        if (_pendingSelectId is string pending && (_nameRow is null || !_nameRow.Input.IsFocused))
        {
            SelectionKind kind = _pendingSelectKind;
            _pendingSelectId = null;
            _document.Selection.Set(kind, pending);
        }

        // A shape-kind conversion (or an undo/redo of one) changes WHICH param rows the region / exclusion
        // inspector needs (disc rows vs rect rows), but only a selection change rebuilds the inspector.
        // Deferred here, after the widget step, so the rebuild never tears rows down mid-grid-update.
        SyncShapeInspector();
    }

    /// <summary>Streams the viewport world around the camera. Overridable for headless order tests. No-ops until
    /// the world is built.</summary>
    protected virtual void UpdateStreaming(float dt)
    {
        if (_viewport.IsBuilt) _viewport.Update(_camera.Position, dt);
    }

    // ---- 3D + UI draw ------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void OnDraw3D(Scene3D scene)
    {
        if (!_built || !_viewport.IsBuilt) return;
        string? selId = _document.Selection.Kind == SelectionKind.Placement ? _document.Selection.Id : null;
        _viewport.Draw(_camera.Position, selId, SelectionHighlight, _visibility);
        DrawOverlays(scene);
        DrawGizmo(scene);
    }

    // This scene's overlay draw buffer, cleared and refilled by ComputeOverlayDrawList once per DrawOverlays
    // call (the TreeView.VisibleRows per-instance precedent): a per-call List<T> would litter Gen0 with a
    // per-frame allocation for a value nobody keeps past that same frame's GPU submission.
    readonly List<OverlayDraw> _overlayDrawList = new();

    /// <summary>Submits the exclusion / region / feature overlay fills to the Scene3D debug-fill pass. The
    /// doc-to-draw-list step is the pure, headless-tested <see cref="ComputeOverlayDrawList"/>; only the per-entry
    /// GPU submission (a debug disc / quad / fan) lives here. No-op until the field exists (world built).</summary>
    void DrawOverlays(Scene3D scene)
    {
        if (_controller.Field is not { } field) return;
        foreach (OverlayDraw o in ComputeOverlayDrawList(
                     _document.Doc, _document.Selection, field.SampleHeight, _options.ShowOverlays, _visibility,
                     _overlayDrawList))
        {
            switch (o.Shape)
            {
                case OverlayShape.Disc:
                    scene.DebugFilledCircle(o.Center, Vector3.UnitY, o.Radius, o.Color);
                    break;
                case OverlayShape.Rect:
                    scene.DebugFilledQuad(o.Center, o.HalfExtents, o.Color);
                    break;
                case OverlayShape.Polygon:
                    if (o.Rim is { Count: >= 3 } rim) scene.DebugFilledFan(o.Center, rim, o.Color);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Turns the document's exclusions, scatter overrides, regions, and terrain features into a flat list of
    /// ground-plane overlay fills: each authoring shape becomes a disc / rect / polygon fill (exclusions red-ish,
    /// scatter overrides orange, regions blue-ish) and each terrain feature a small amber marker disc at its center,
    /// all lifted a small epsilon above
    /// the sampled ground so they clear the terrain. The overlay whose element matches <paramref name="selection"/>
    /// is flagged and brightened. <paramref name="sampleHeight"/> supplies the ground height at an (x, z); a
    /// <c>null</c> shape, a polygon with fewer than three points, or a feature whose center cannot be derived (an
    /// unknown custom type) is skipped, and so is any element <paramref name="visibility"/> hides (its group is off
    /// or it is individually hidden), so a hidden overlay is not drawn. Leaves the buffer empty when
    /// <paramref name="showOverlays"/> is false. Pure over its inputs (no GPU, no scene state), so the whole
    /// computation is headless-testable. <see cref="DrawOverlays"/> submits the result untested.
    /// <para><paramref name="into"/> is the caller-owned result buffer (the <see cref="TreeView.VisibleRows"/>
    /// reuse pattern, with the buffer at the call site instead of behind the API): it is cleared at entry,
    /// filled, and returned, so a per-frame caller passes one long-lived list and pays no per-call allocation,
    /// while a caller that wants independent results simply passes a fresh list per call.</para></summary>
    internal static List<OverlayDraw> ComputeOverlayDrawList(
        MapDocument doc, EditorSelection selection, Func<float, float, float> sampleHeight, bool showOverlays,
        EditorVisibility visibility, List<OverlayDraw> into)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(sampleHeight);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(into);

        List<OverlayDraw> list = into;
        list.Clear();
        if (!showOverlays) return list;

        int selectedExclusion = selection.Kind == SelectionKind.Exclusion ? SelectedIndex(selection.Id) : -1;
        int selectedScatterOverride = selection.Kind == SelectionKind.ScatterOverride ? SelectedIndex(selection.Id) : -1;
        int selectedFeature = selection.Kind == SelectionKind.Feature ? SelectedIndex(selection.Id) : -1;

        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.Exclusion, Index(i))) continue;   // hidden: no overlay
            AddShapeOverlay(list, doc.Exclusions[i].Shape, OverlayCategory.Exclusion, ExclusionOverlayColor,
                selected: i == selectedExclusion, sampleHeight);
        }

        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.ScatterOverride, Index(i))) continue;   // hidden: no overlay
            AddShapeOverlay(list, doc.ScatterOverrides[i].Shape, OverlayCategory.ScatterOverride, ScatterOverrideOverlayColor,
                selected: i == selectedScatterOverride, sampleHeight);
        }

        foreach (MapRegion region in doc.Regions)
        {
            if (!visibility.IsElementVisible(SelectionKind.Region, region.Name)) continue;   // hidden: no overlay
            bool selected = selection.Kind == SelectionKind.Region &&
                            string.Equals(selection.Id, region.Name, StringComparison.Ordinal);
            AddShapeOverlay(list, region.Shape, OverlayCategory.Region, RegionOverlayColor, selected, sampleHeight);
        }

        IReadOnlyList<MapFeature> features = doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.Feature, Index(i))) continue;   // hidden: no marker
            if (!FeatureGeometry.TryCenter(features[i], out float fx, out float fz)) continue;   // unknown type: no marker
            bool selected = i == selectedFeature;
            var center = new Vector3(fx, sampleHeight(fx, fz) + OverlayLift, fz);
            list.Add(new OverlayDraw(OverlayCategory.Feature, OverlayShape.Disc, center,
                OverlayPicking.FeatureMarkerRadius, Vector2.Zero, rim: null, Tint(FeatureOverlayColor, selected), selected));
        }

        // The selected biome band's finite Start/End edges, as full-width ground lines across the doc's X extent at
        // those world-Z positions (a band is a world-Z slice, not a placed shape - see TerrainField.ShapeAt, which
        // blends bands by z only). A band carries no viewport geometry of its own and its order is meaningless, so
        // ONLY the current selection draws, and an open edge (null Start/End) draws nothing. Not gated on the
        // visibility system: bands have no visibility toggle (they are outline-only, never independently drawn).
        if (selection.Kind == SelectionKind.BiomeBand)
        {
            int selectedBand = SelectedIndex(selection.Id);
            List<MapBiomeBand> bands = doc.Terrain.Biomes;
            if (selectedBand >= 0 && selectedBand < bands.Count)
            {
                MapBiomeBand band = bands[selectedBand];
                AddBandEdgeLine(list, doc.Bounds, band.Start, sampleHeight);
                AddBandEdgeLine(list, doc.Bounds, band.End, sampleHeight);
            }
        }
        return list;
    }

    // One finite biome-band edge as a full-width ground line across the doc's X extent at world-Z `edge`. A null or
    // infinite edge (an open, unbounded band edge) draws nothing. The line is a thin rect quad centered on the doc's
    // X midpoint, seated at the ground height sampled there (a thin line needs one sample, like a feature marker).
    static void AddBandEdgeLine(List<OverlayDraw> list, MapBounds bounds, float? edge, Func<float, float, float> sampleHeight)
    {
        if (edge is not { } z || float.IsInfinity(z)) return;
        float cx = (bounds.MinX + bounds.MaxX) * 0.5f;
        float halfWidth = MathF.Abs(bounds.MaxX - bounds.MinX) * 0.5f;
        var center = new Vector3(cx, sampleHeight(cx, z) + OverlayLift, z);
        var half = new Vector2(halfWidth, BiomeBandLineHalfDepth);
        // Always the current selection, so the base color is drawn directly (no Tint pass): there is no unselected
        // band line to contrast against.
        list.Add(new OverlayDraw(OverlayCategory.BiomeBand, OverlayShape.Rect, center, 0f, half,
            rim: null, BiomeBandOverlayColor, selected: true));
    }

    // An index-keyed element id (feature / exclusion), matching the selection and outline id encoding.
    static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);

    // Turn one authoring shape into its overlay fill, at ground height plus the lift epsilon. Disc -> a ground disc,
    // rect -> a ground quad at the rect's midpoint, polygon (>= 3 points) -> a fan from the point centroid with each
    // rim vertex sampled at its own ground height. A null shape or a degenerate polygon adds nothing.
    static void AddShapeOverlay(List<OverlayDraw> list, MapShapeDoc? shape, OverlayCategory category,
        Color baseColor, bool selected, Func<float, float, float> sampleHeight)
    {
        Color color = Tint(baseColor, selected);
        switch (shape)
        {
            case DiscShapeDoc d:
            {
                var center = new Vector3(d.CenterX, sampleHeight(d.CenterX, d.CenterZ) + OverlayLift, d.CenterZ);
                list.Add(new OverlayDraw(category, OverlayShape.Disc, center, d.Radius,
                    Vector2.Zero, rim: null, color, selected));
                break;
            }
            case RectShapeDoc r:
            {
                float cx = (r.MinX + r.MaxX) * 0.5f, cz = (r.MinZ + r.MaxZ) * 0.5f;
                var center = new Vector3(cx, sampleHeight(cx, cz) + OverlayLift, cz);
                var half = new Vector2(MathF.Abs(r.MaxX - r.MinX) * 0.5f, MathF.Abs(r.MaxZ - r.MinZ) * 0.5f);
                list.Add(new OverlayDraw(category, OverlayShape.Rect, center, 0f, half, rim: null, color, selected));
                break;
            }
            case PolygonShapeDoc p when p.Points.Count >= 3:
            {
                var rim = new List<Vector3>(p.Points.Count);
                float sx = 0f, sz = 0f;
                foreach (float[] pt in p.Points)
                {
                    float px = pt.Length > 0 ? pt[0] : 0f, pz = pt.Length > 1 ? pt[1] : 0f;
                    sx += px; sz += pz;
                    rim.Add(new Vector3(px, sampleHeight(px, pz) + OverlayLift, pz));
                }
                float cx = sx / p.Points.Count, cz = sz / p.Points.Count;
                var center = new Vector3(cx, sampleHeight(cx, cz) + OverlayLift, cz);
                list.Add(new OverlayDraw(category, OverlayShape.Polygon, center, 0f, Vector2.Zero, rim, color, selected));
                break;
            }
            default:
                break;   // null shape or a polygon with fewer than three points: no overlay
        }
    }

    // A selected overlay reads brighter: scale RGB up (clamped at 1.0, alpha preserved) then firm up that alpha,
    // so the highlighted shape stands out against its unselected neighbours without an unclamped channel
    // overshooting past white. Unselected returns the base color.
    static Color Tint(Color baseColor, bool selected) => selected
        ? baseColor.ScaleRgbClamped(OverlaySelectBrighten).WithAlpha(MathF.Min(1f, baseColor.A * OverlaySelectAlphaBoost))
        : baseColor;

    // The list index a feature / exclusion selection id encodes, or -1 when it is not a valid non-negative index.
    static int SelectedIndex(string id) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;

    /// <inheritdoc/>
    public override void OnDrawUi(SpriteBatch batch)
    {
        if (!_built || batch is null || _font is null || Manager is null) return;
        UiViewport? ui = Manager.UiViewport;
        if (ui is null) return;
        SpriteFont font = _font.For(ui.DpiScale);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        FillPanel(batch, L.Toolbar, PanelBackground);
        FillPanel(batch, L.Outline, PanelBackground);
        FillPanel(batch, L.Inspector, PanelBackground);
        FillPanel(batch, L.Status, StatusBackground);

        (Rect tabsRect, Rect saveRect) = SplitToolbar(L.Toolbar);
        _toolbar.Bounds = tabsRect;
        _toolbar.Font = font;
        _toolbar.Draw(batch, _white);

        _saveButton.Bounds = saveRect;
        _saveButton.Font = font;
        _saveButton.Draw(batch, _white);

        _outline.Bounds = L.Outline;
        _outline.Draw(batch, _white, font);

        _inspector.Bounds = L.Inspector;
        _inspector.Draw(batch, _white, font);

        DrawPalette(batch, font, L.Palette);
        string statusLine = TruncateStatusLine(StatusLine(), L.Status.Width, s => font.Measure(s).X);
        batch.DrawString(font, statusLine,
            new Vector2(MathF.Floor(L.Status.X + StatusTextInset), MathF.Floor(L.Status.Y + (StatusHeight - font.LineHeight) * 0.5f)),
            new Color(0.85f, 0.87f, 0.92f, 1f));

        // Drawn LAST, after every other chrome element (including the inspector, whose own Draw already clears its
        // scissor at the end): a hovered row's Description tooltip must escape the grid's clip and overlay
        // everything else, the PatchNotesView.DrawCloseTooltip precedent (drawn after ClearScissor).
        DrawInspectorTooltip(batch, font, new Vector2(ui.Width, ui.Height));

        // The modal exit dialog draws on top of everything (scrim + panel over the tooltip): it dims the whole
        // editor behind it while it is open. Its own font is set here (BuildChrome runs before any font resolves).
        if (_exitDialog is not null)
        {
            _exitDialog.Viewport = new Vector2(ui.Width, ui.Height);
            _exitDialog.TitleFont = font;
            _exitDialog.BodyFont = font;
            _exitDialog.Draw(batch, _white, _ui.Pointer);
        }
    }

    // The inspector's hover tooltip: built lazily here (the PatchNotesView precedent, `_tooltip ??= new
    // Tooltip(font, font)`), because BuildChrome runs before a UiViewport (and so a resolved SpriteFont) exists.
    // Rebuilt (not just re-anchored) whenever `font` is a different instance than the one it was last built with,
    // since Tooltip's title/body fonts are fixed at construction and DpiFont.For re-bakes (and disposes the old
    // SpriteFont) on a DPI change, a stale _tooltip would otherwise keep drawing with a disposed font forever.
    // Shown immediately while PropertyGrid.HoveredRow carries a non-null Description (no delay, matching every
    // other Tooltip consumer), anchored at the hovered row's label rect (top-center, so it opens upward over the
    // row like a standard hover tip), hidden the instant the hover leaves or the row has no Description. The
    // hover-to-content decision itself is the pure ComputeTooltipContent seam below, so it stays headless-testable
    // without a live SpriteFont (a real Tooltip instance only needs one to actually draw).
    void DrawInspectorTooltip(SpriteBatch batch, SpriteFont font, Vector2 viewport)
    {
        if (_tooltip is null || !ReferenceEquals(_tooltipFont, font))
        {
            _tooltip = new Tooltip(font, font);
            _tooltipFont = font;
        }
        _tooltip.Viewport = viewport;

        if (ComputeTooltipContent() is { } content)
            _tooltip.Show(content.Text, Array.Empty<TooltipLine>(), content.Anchor);
        else
            _tooltip.Hide();
        _tooltip.Draw(batch, _white);
    }

    /// <summary>
    /// The inspector hover tooltip's content for this frame: the current <see cref="PropertyGrid.HoveredRow"/>'s
    /// <see cref="PropertyRow.Description"/> plus the anchor point to show it at (the hovered row's label rect,
    /// top-center via <see cref="PropertyGrid.RowLabelBounds(int)"/>), or null while nothing is hovered, the
    /// hovered row carries no Description (a <see cref="HeaderRow"/>, or a row an implementer forgot to describe),
    /// or the hovered row was orphaned by a same-frame inspector rebuild: <c>SyncShapeInspector</c> can call
    /// <c>RebuildInspector</c> after <see cref="PropertyGrid.Update"/> already set <see cref="PropertyGrid.HoveredRow"/>
    /// this frame, and a rebuild clears and re-adds fresh row instances without resetting the hover, so the old row
    /// can briefly outlive the grid it came from. Pure and internal so a headless test can assert the
    /// hover-to-tooltip mapping without a live SpriteFont, which <see cref="Tooltip"/> needs only to actually draw
    /// (see <see cref="DrawInspectorTooltip"/>).
    /// </summary>
    internal (LocalizedText Text, Vector2 Anchor)? ComputeTooltipContent()
    {
        if (_inspector.HoveredRow is not { Description: { } desc } hovered) return null;
        int index = _inspector.Rows.IndexOf(hovered);
        if (index < 0) return null;   // orphaned by a same-frame RebuildInspector, see the summary above
        Rect label = _inspector.RowLabelBounds(index);
        return (desc, new Vector2(label.X + label.Width * 0.5f, label.Y));
    }

    /// <summary>Submits the transform-gizmo meshes for the current selection. The affordance-to-mesh-set
    /// decision is the pure, headless-tested <see cref="ComputeGizmoMeshes"/>. Only the per-entry
    /// <see cref="MeshHandle"/> lookup and <see cref="Scene3D.DrawOverlayMesh"/> submission lives here.</summary>
    void DrawGizmo(Scene3D scene)
    {
        GizmoAffordance affordance = _controller.TryGizmo(out Vector3 pos);
        if (affordance == GizmoAffordance.None) return;
        float s = GizmoScaleFor(pos);
        Matrix4x4 world = Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(pos);
        foreach (GizmoMesh mesh in ComputeGizmoMeshes(affordance))
            scene.DrawOverlayMesh(MeshHandleFor(mesh), world);
    }

    /// <summary>Which baked gizmo meshes <see cref="DrawGizmo"/> draws for a given affordance, in draw order.
    /// Pure (no GPU, no scene state), so the affordance-to-mesh-set decision is fully headless-testable: a spawn
    /// (<see cref="GizmoAffordance.Marker"/>) draws the selection marker plus the XZ arrows (the working
    /// ground-plane drag is otherwise invisible). A feature / disc / rect shape
    /// (<see cref="GizmoAffordance.MoveScale"/>) draws the XZ arrows plus the scale cube, never the +Y arrow
    /// (<c>EditorToolController.RestrictHandle</c> already blocks that handle for both). A rotatable feature, a
    /// ridge or rim (<see cref="GizmoAffordance.MoveScaleRotate"/>), adds the yaw ring between them, still with no
    /// +Y arrow. A placement (<see cref="GizmoAffordance.Full"/>) keeps every handle. <see cref="DrawGizmo"/>
    /// submits the result untested.</summary>
    internal static GizmoMesh[] ComputeGizmoMeshes(GizmoAffordance affordance) => affordance switch
    {
        GizmoAffordance.Full => FullGizmoMeshes,
        GizmoAffordance.MoveScaleRotate => MoveScaleRotateGizmoMeshes,
        GizmoAffordance.MoveScale => MoveScaleGizmoMeshes,
        GizmoAffordance.Marker => MarkerGizmoMeshes,
        _ => NoneGizmoMeshes,
    };

    MeshHandle MeshHandleFor(GizmoMesh mesh) => mesh switch
    {
        GizmoMesh.TranslateArrowsFull => _translateArrows,
        GizmoMesh.TranslateArrowsXZ => _translateArrowsXZ,
        GizmoMesh.YawRing => _yawRing,
        GizmoMesh.ScaleHandle => _scaleHandle,
        GizmoMesh.SelectionMarker => _selectionMarker,
        _ => throw new ArgumentOutOfRangeException(nameof(mesh), mesh, null),
    };

    void DrawPalette(SpriteBatch batch, SpriteFont font, Rect bounds)
    {
        if (!BottomPanelVisible) return;   // no panel this frame: the outline owns the whole left column
        FillPanel(batch, bounds, PanelBackground);
        if (FeatureMode)
        {
            _featureList.Bounds = bounds;   // no filter box: the feature-type list fills the whole panel
            _featureList.Draw(batch, _white, font);
            return;
        }
        (Rect filterRect, Rect bodyRect) = SplitPaletteRegion(bounds);
        if (SpawnMode)
        {
            _spawnFilter.Bounds = filterRect;
            _spawnFilter.Font = font;
            _spawnFilter.Draw(batch, _white);
            _spawnList.Bounds = bodyRect;
            _spawnList.Draw(batch, _white, font);
        }
        else
        {
            _paletteFilter.Bounds = filterRect;
            _paletteFilter.Font = font;
            _paletteFilter.Draw(batch, _white);
            _paletteTree.Bounds = bodyRect;
            _paletteTree.Draw(batch, _white, font);
        }
    }

    // ---- chrome wiring -----------------------------------------------------------------------------------

    void BuildChrome()
    {
        _toolbar = new TabBar(ToolLabels);
        _outline = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _inspector = new PropertyGrid(default) { EditorStyle = GuiStyle.Modern };
        _outline.OnSelected = OnOutlineSelected;
        _outline.OnReordered = OnOutlineReordered;
        _outline.CanReorder = OutlineNodeIsReorderable;

        _paletteFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter kits...") };
        _paletteTree = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _paletteTree.OnSelected = OnPaletteSelected;

        _spawnFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter spawns...") };
        _spawnList = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _spawnList.OnSelected = OnSpawnSelected;

        _featureList = new TreeView(default) { RowHeight = 22f, Style = GuiStyle.Modern };
        _featureList.OnSelected = OnFeatureTypeSelected;

        // The toolbar Save button (decision 4). Font and Bounds are set per frame (no SpriteFont resolves at
        // chrome-build time, the TabBar pattern). The label is re-synced each chrome step in UpdateChrome.
        _saveButton = new Button(default, LocalizedText.Raw("Save"), null!, () => SaveDocument())
        {
            Style = GuiStyle.Modern,
        };
    }

    void UpdateWidgets(float dt)
    {
        UiViewport? ui = Manager!.UiViewport;
        if (ui is null) return;
        _ui.Update(Manager.Input, ui);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        (Rect tabsRect, Rect saveRect) = SplitToolbar(L.Toolbar);
        _toolbar.Bounds = tabsRect;
        if (_toolbar.Update(_ui.Pointer)) _controller.Mode = (EditorToolMode)_toolbar.ActiveIndex;

        _saveButton.Bounds = saveRect;
        _saveButton.Update(_ui.Pointer);

        _outline.Bounds = L.Outline;
        _outline.Update(_ui);

        _inspector.Bounds = L.Inspector;
        _inspector.Update(_ui, dt);

        if (FeatureMode)
        {
            _featureList.Bounds = L.Palette;
            _featureList.Update(_ui);
        }
        else if (BottomPanelVisible)
        {
            (Rect filterRect, Rect bodyRect) = SplitPaletteRegion(L.Palette);
            if (SpawnMode)
            {
                _spawnFilter.Bounds = filterRect;
                _spawnFilter.Update(_ui.Pointer, Manager.Input, dt);
                _spawnList.Bounds = bodyRect;
                _spawnList.Update(_ui);
            }
            else
            {
                _paletteFilter.Bounds = filterRect;
                _paletteFilter.Update(_ui.Pointer, Manager.Input, dt);
                _paletteTree.Bounds = bodyRect;
                _paletteTree.Update(_ui);
            }
        }
        RefreshPalettes();
    }

    // The bottom-left panel hosts a tool-scoped picker: the spawn tool shows the spawn-archetype list, the
    // prop-place tool shows the kit palette, the feature tool shows the feature-type list, and every other tool
    // shows NO panel (the outline reflows over the freed space, see ComputeLayout). At most one picker is driven /
    // drawn per frame. Null-guarded because the layout helpers (StatusRect and friends) may run before OnEnter
    // creates the controller.
    bool SpawnMode => _controller is not null && _controller.Mode == EditorToolMode.PlaceSpawn;

    /// <summary>True while the kit palette (filter + tree) occupies the bottom-left panel: ONLY in the
    /// prop-place tool. Exposed for tests.</summary>
    internal bool KitPaletteVisible => _controller is not null && _controller.Mode == EditorToolMode.PlacePlacement;

    // Whether the feature-type picker occupies the bottom-left panel: ONLY in the EditFeature tool.
    bool FeatureMode => _controller is not null && _controller.Mode == EditorToolMode.EditFeature;

    // Whether the bottom-left panel exists at all this frame (one of its three contents is active).
    bool BottomPanelVisible => SpawnMode || KitPaletteVisible || FeatureMode;

    /// <summary>True while ANY focusable editor the scene owns has keyboard focus: the inspector's aggregate
    /// query, OR the kit-palette filter (only while <see cref="KitPaletteVisible"/>), OR the spawn filter
    /// (only while <see cref="SpawnMode"/>). Mode-gated rather than a raw <c>IsFocused</c> OR, because a
    /// hidden filter can retain stale focus: <see cref="TextInput.Unfocus"/> only runs inside the filter's
    /// own <c>Update</c>, and <see cref="UpdateWidgets"/> only calls that for the mode's currently-visible
    /// filter, so switching tools away from a focused filter leaves its <c>IsFocused</c> field stuck true.
    /// Gating on the same visibility condition that drives the filter's <c>Update</c> call keeps a hidden
    /// field's stale focus from blocking shortcuts in a different tool mode.</summary>
    bool AnyEditorFocused => _inspector.HasActiveEditor
        || (KitPaletteVisible && _paletteFilter.IsFocused)
        || (SpawnMode && _spawnFilter.IsFocused);

    // Cmd+D: duplicate the current selection (decision 8). Terrain (the singleton root) has nothing to
    // duplicate, so it lands a status note instead of a mutation. An empty selection silently no-ops (nothing to
    // explain, same as every other chord over an empty selection). Every other kind clones through
    // EditorToolController.DuplicateSelection, which already offsets the position, gives the clone a fresh
    // identity, selects it, and seals one undo step. DuplicateSelection also no-ops (null result) for a custom
    // feature type FeatureGeometry.Translated does not know how to offset, and the selection kind is still
    // Feature in that case (nothing changed it), which is how this tells that skip apart from the ordinary
    // empty-selection no-op and surfaces its own status note instead of silently doing nothing.
    void DuplicateSelectionChord()
    {
        if (_document.Selection.Kind == SelectionKind.Terrain)
        {
            _statusText = "Nothing to duplicate: Terrain is the document singleton.";
            return;
        }
        SelectionKind kindBefore = _document.Selection.Kind;
        EditorToolController.DuplicateResult? result = _controller.DuplicateSelection();
        if (result is null && kindBefore == SelectionKind.Feature)
            _statusText = "Cannot duplicate this feature type.";
    }

    // The digit keys a bookmark chord watches, index 0 = slot 1 through index 8 = slot 9 (decision 9).
    static readonly Key[] BookmarkKeys =
    {
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9,
    };

    // Checks every digit key for a press edge this frame and stores (Shift held) or recalls (bare) the matching
    // slot. At most one digit fires per frame (a single physical keypress), so the first match wins.
    void HandleBookmarkChord(InputState s, bool shift)
    {
        for (int i = 0; i < BookmarkKeys.Length; i++)
        {
            if (!s.WasPressed(BookmarkKeys[i])) continue;
            int slot = i + 1;
            if (shift) StoreBookmark(i, slot); else RecallBookmark(i, slot);
            return;
        }
    }

    // Shift+<slot>: snapshots the fly camera's pose into the slot (decision 9), overwriting whatever was there.
    void StoreBookmark(int index, int slot)
    {
        _bookmarks[index] = new CameraBookmark(_camera.Position, _camera.Yaw, _camera.Pitch);
        _statusText = "Bookmark " + slot.ToString(CultureInfo.InvariantCulture) + " stored";
    }

    // Bare <slot>: restores the fly camera to a previously stored pose. An empty slot (never stored this
    // session) leaves the camera untouched and surfaces a status note instead.
    void RecallBookmark(int index, int slot)
    {
        CameraBookmark b = _bookmarks[index];
        if (!b.Set)
        {
            _statusText = "Bookmark " + slot.ToString(CultureInfo.InvariantCulture) + " is empty";
            return;
        }
        _camera.Position = b.Position;
        _camera.Yaw = b.Yaw;
        _camera.Pitch = b.Pitch;
        _statusText = "Bookmark " + slot.ToString(CultureInfo.InvariantCulture) + " recalled";
    }

    // Ctrl+Up / Ctrl+Down: move the selected reorderable element one step earlier (delta -1) or later (delta +1) in
    // its list. Dispatches on the selection kind: a Feature reorders in fold order (the LAST feature over an overlap
    // wins, so Ctrl+Down promotes it toward dominating), and a ScatterOverride reorders in match order (the FIRST
    // matching override wins, so its order is genuinely significant too). Both take the same index-based path (the
    // shared ReorderIndexKeyed helper): clamp at the ends, execute the reorder command, remap the element's hide to
    // its new index, and reselect it there. Exclusions are DELIBERATELY not reorderable through this chord: their
    // masks combine as a set union, so list order never changes which ground ends up excluded (it is meaningless),
    // and a chord that implied an ordering would only mislead. Undo / redo does NOT re-follow the moved element
    // (v1): the selection is a bare index string, so after an undo it stays on the same index, which may then
    // address a different element. The direct Ctrl+Up/Down action is what keeps the selection glued to the move.
    void ReorderSelectedElement(int delta)
    {
        EditorSelection selection = _document.Selection;
        switch (selection.Kind)
        {
            case SelectionKind.Feature:
                ReorderIndexKeyed(selection, delta, _document.Doc.Terrain.Features.Count,
                    (from, to) => new ReorderFeatureCommand(from, to));
                break;
            case SelectionKind.ScatterOverride:
                ReorderIndexKeyed(selection, delta, _document.Doc.ScatterOverrides.Count,
                    (from, to) => new ReorderScatterOverrideCommand(from, to));
                break;
            default:
                break;   // no other selection kind reorders through the chord (exclusions are order-free)
        }
    }

    // The shared index-keyed reorder step behind the Ctrl+Up/Down chord: resolve the selected index, clamp both it
    // and its delta target against the live count (so a boundary press lands no command), execute the caller's
    // reorder command, then follow the element's selection (Set) to the new index. A per-element hide follows the
    // move too, but that is driven by the reorder command's IVisibilityEffect through the document events (see
    // OnCommandVisibilityForward), not remapped here. Kept generic over the selection kind so Feature and
    // ScatterOverride share one body.
    void ReorderIndexKeyed(EditorSelection selection, int delta, int count, Func<int, int, IEditorCommand> command)
    {
        int from = SelectedIndex(selection.Id);
        if (from < 0 || from >= count) return;
        int to = from + delta;
        if (to < 0 || to >= count) return;   // clamp at the ends: nothing to move, so land no command
        _document.Execute(command(from, to));
        selection.Set(selection.Kind, to.ToString(CultureInfo.InvariantCulture));
    }

    // R: snap the selected placement back onto the ground by re-issuing its move with a null Y (the runtime
    // ground-snaps a null Y to the deterministic field height). A no-op for non-placement selections and when the
    // placement already carries a null Y (already grounded), so no empty command lands on the undo stack.
    void SnapSelectedPlacementToGround()
    {
        if (_document.Selection.Kind != SelectionKind.Placement) return;
        if (Placement(_document.Selection.Id) is not { } p) return;
        if (p.Y is null) return;   // already grounded: do not execute an empty command
        _document.Execute(new MovePlacementCommand(p.Id, p.X, p.Z, null));
    }

    // Shift+Escape: build and open the modal exit dialog (decision 3), keyed to the current dirty state. A dirty
    // document offers Save and Close (the default, Enter) / Save / Discard / Cancel (the Esc target). A clean one
    // offers Close / Cancel. The footer-button callbacks below drive save / quit / dismiss.
    void OpenExitDialog()
    {
        var dialog = new PopupPanel
        {
            Style = GuiStyle.Modern,
            TitleContent = LocalizedText.Raw("Exit map editor"),
            WrapLongLabels = true,
        };
        if (_document.IsDirty)
        {
            dialog.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("You have unsaved changes."), LocalizedText.Raw(""), Vector4.One) });
            dialog.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("Save and Close"), ExitDialogSaveAndClose),
                new PopupAction(LocalizedText.Raw("Save"), ExitDialogSave),
                new PopupAction(LocalizedText.Raw("Discard"), ExitDialogQuit),
                new PopupAction(LocalizedText.Raw("Cancel"), CloseExitDialog),
            });
        }
        else
        {
            dialog.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("Close the editor?"), LocalizedText.Raw(""), Vector4.One) });
            dialog.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("Close"), ExitDialogQuit),
                new PopupAction(LocalizedText.Raw("Cancel"), CloseExitDialog),
            });
        }
        // CancelIndex default -1 resolves to the last footer action (Cancel), which Esc fires. Enter fires index 0
        // (Save and Close when dirty, Close when clean).
        _exitDialog = dialog;
        _statusText = "";   // clear any stale status so the dialog is the whole story
    }

    // Runs the dialog for a frame while it is open (the OnUpdate gate routes here first): the pointer-block + button
    // hit-test need a live viewport (skipped headless, where UiViewport is null), the Esc/Enter routing does not.
    void UpdateExitDialog(float dt)
    {
        UiViewport? ui = Manager!.UiViewport;
        if (ui is not null)
        {
            _ui.Update(Manager.Input, ui);
            _exitDialog!.Viewport = new Vector2(ui.Width, ui.Height);
            _exitDialog.Update(_ui.Pointer);   // a footer-button click may close the dialog (sets _exitDialog null)
        }
        _exitDialog?.HandleKeys(Manager.Input);   // Enter = index 0, Esc = Cancel (Shift+Escape hits the same edge)
    }

    // "Save" (dirty dialog): save in place, staying in the editor. Dismiss the dialog only on save SUCCESS, so a
    // failure keeps the dialog up with the error in the status strip and the user's unsaved work intact.
    void ExitDialogSave()
    {
        if (SaveDocument()) CloseExitDialog();
    }

    // "Save and Close" (dirty dialog, the default action): save, then quit only if the save SUCCEEDED. A failure
    // aborts the close and leaves the dialog open (the status strip carries the error), never losing the work.
    void ExitDialogSaveAndClose()
    {
        if (!SaveDocument()) return;   // save failed: abort the close, keep the dialog open
        CloseExitDialog();
        QuitEditor();
    }

    // "Discard" / "Close": leave the editor without saving.
    void ExitDialogQuit()
    {
        CloseExitDialog();
        QuitEditor();
    }

    // "Cancel" / Esc: dismiss the dialog and stay in the editor.
    void CloseExitDialog() => _exitDialog = null;

    // Leave the editor (decision 1): when it is the bottom scene (nothing to pop back to) and the head wired a quit
    // path, invoke that, otherwise pop back to whatever sits beneath (the landing menu, or nothing).
    void QuitEditor()
    {
        if (_options.RequestQuit is { } quit && Manager is { Count: 1 })
            quit();
        else
            Manager?.Pop();
    }

    void OnDocumentChanged()
    {
        _viewport.InvalidatePlacements();
        RebuildOutline();
    }

    // The single source of per-element-hide maintenance: a command that carries an IVisibilityEffect runs its
    // forward hide remap on execute / redo and its inverse on undo, so a hide stays glued to the element the command
    // moved, removed, or renamed and survives undo / redo. The document fires these BEFORE DocumentChanged, so the
    // outline rebuild the change triggers already reads the updated hide set. Commands with no visibility effect
    // (every non-reorder / non-remove / non-rename command, and a merged EditFeatureCommand) are ignored.
    void OnCommandVisibilityForward(IEditorCommand command)
    {
        if (command is IVisibilityEffect effect) effect.Effect.ApplyForward(_visibility);
    }

    void OnCommandVisibilityInverse(IEditorCommand command)
    {
        if (command is IVisibilityEffect effect) effect.Effect.ApplyInverse(_visibility);
    }

    void OnSelectionChanged()
    {
        // A rename queues a pending re-select of the NEW key once the rename row loses focus (see UpdateChrome).
        // The pending sync clears the field itself before it calls Selection.Set, so this only ever sees it still
        // set when something ELSE changed the selection first (an outline click, a viewport pick) while the rename
        // row was still focused. That selection must win: drop the stale pending re-select so it cannot fire next
        // frame and stomp the user's new pick back onto the renamed element.
        if (_pendingSelectId is string pending &&
            !(_document.Selection.Kind == _pendingSelectKind && _document.Selection.Id == pending))
        {
            _pendingSelectId = null;
        }
        RebuildInspector();
        SyncOutlineSelection();
    }

    // Resolves the current EditorSelection to its live outline node and highlights + scrolls to it, or clears the
    // outline highlight when nothing is selected. Called from two places: OnSelectionChanged (a viewport pick, an
    // outline tap, a RunOutlineAction select-on-add) and the end of RebuildOutline (every document edit news up a
    // fresh set of TreeNodes, which would otherwise orphan _outline.Selected against a node no longer in Roots).
    // Matches purely on OutlineRef kind/id equality (a record struct, value equality for free), never on a
    // per-kind switch, so a new SelectionKind gets sync for free the day its outline nodes start carrying an
    // OutlineRef tag. An outline-originated selection (the tree already set Selected before invoking OnSelected)
    // resolves back to the SAME node here, so the re-set and ScrollTo are harmless no-ops, not a feedback loop.
    void SyncOutlineSelection()
    {
        EditorSelection sel = _document.Selection;
        if (sel.Kind == SelectionKind.None)
        {
            _outline.Selected = null;
            return;
        }
        // Mid-rename, every keystroke executes a rename command that rebuilds the outline (OnDocumentChanged)
        // while the actual re-select is still deferred (_pendingSelectId, fired once the row loses focus, see
        // UpdateChrome). Selection.Id therefore still holds the OLD key for the rest of that frame, which would
        // resolve to nothing in the freshly rebuilt tree and drop the highlight for the whole edit. Resolve
        // against the pending NEW key instead so the highlight follows the row being renamed live. The kind check
        // guards a pending re-select left over for a DIFFERENT selection kind (OnSelectionChanged clears it on any
        // real kind/id mismatch, so this should never see one, but costs nothing to assert here too).
        string id = _pendingSelectId is string pending && _pendingSelectKind == sel.Kind ? pending : sel.Id;
        var target = new OutlineRef(sel.Kind, id);
        TreeNode? node = _outline.FindByTag(tag => tag is OutlineRef r && r.Equals(target));
        _outline.Selected = node;
        if (node is not null) _outline.ScrollTo(node);
    }

    void OnOutlineSelected(TreeNode node)
    {
        // A normal node carries an OutlineRef and selects its element. A synthetic action node (e.g. "[+ add band]")
        // carries an OutlineAction instead and runs its side effect rather than moving the selection there.
        if (node.Tag is OutlineRef r) _document.Selection.Set(r.Kind, r.Id);
        else if (node.Tag is OutlineAction a) RunOutlineAction(a);
    }

    // Runs an outline action node's side effect. Today the only action appends a default biome band and selects it,
    // so the just-added band opens straight into its inspector for editing (the place-tool select-on-add idiom).
    void RunOutlineAction(OutlineAction action)
    {
        switch (action.Kind)
        {
            case OutlineActionKind.AddBiomeBand:
                _document.Execute(new AddBiomeBandCommand(new MapBiomeBand()));
                _document.SealGesture();
                int added = _document.Doc.Terrain.Biomes.Count - 1;
                _document.Selection.Set(SelectionKind.BiomeBand, added.ToString(CultureInfo.InvariantCulture));
                break;
            case OutlineActionKind.AddScatterLayer:
            {
                string name = GenerateLayerName("layer-", LiveScatterNames());
                _document.Execute(new AddScatterLayerCommand(new MapScatterLayer { Name = name }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.ScatterLayer, name);
                break;
            }
            case OutlineActionKind.AddCompanionLayer:
            {
                string name = GenerateLayerName("companion-", LiveCompanionNames());
                // Default the host to the first scatter layer if one exists, so the new companion validates on
                // save without an extra step. With no scatter layers yet, HostLayer stays empty (invalid until the
                // operator adds a scatter layer and picks it, a dev-tooling edge the HostLayer chooser handles).
                string host = _document.Doc.ScatterLayers.Count > 0 ? _document.Doc.ScatterLayers[0].Name : "";
                _document.Execute(new AddCompanionLayerCommand(new MapCompanionLayer { Name = name, HostLayer = host }));
                _document.SealGesture();
                _document.Selection.Set(SelectionKind.CompanionLayer, name);
                break;
            }
        }
    }

    // The smallest N >= 1 such that `prefix` + N is not already a live layer name (the ke-mapedit GenerateId
    // approach), so a freshly added layer gets a unique, immediately renameable placeholder name.
    static string GenerateLayerName(string prefix, IReadOnlyCollection<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            string candidate = prefix + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // A drag-and-drop reorder inside the outline. Same-parent drops are the only ones the TreeView reports, and
    // three categories accept a reorder: Features fold in list order (last wins overlaps), Scatter Overrides match
    // in list order (FIRST match wins, so their order really is significant), and Exclusions have order-free
    // semantics but still expose a reorder for a stable authored layout. All three map to their index-based command
    // with the selection following the moved row (the ReorderSelectedElement idiom). A per-element hide follows the
    // moved element too, but that is now driven by the command's IVisibilityEffect through the document events (see
    // OnCommandVisibilityForward), not remapped here. Every other category (Placements, Spawns, Regions, Terrain)
    // has no reorder, so its drop is a no-op.
    // Only the outline kinds with a real reorder command (the order-significant, index-keyed lists) may arm a drag:
    // features, exclusions, and scatter overrides. Everything else is blocked at the arm gate, so it never shows a
    // phantom insertion line that OnOutlineReordered would only reject after the drop. That covers biome bands (the
    // blend is order-independent, so band order is meaningless), placements, spawns, regions, and the layer kinds
    // (no list-order semantics), and the category headers and add-actions (not elements at all). OnOutlineReordered's
    // default branch stays as the safety net.
    static bool OutlineNodeIsReorderable(TreeNode node) =>
        node.Tag is OutlineRef r &&
        r.Kind is SelectionKind.Feature or SelectionKind.Exclusion or SelectionKind.ScatterOverride;

    void OnOutlineReordered(TreeNode node, int fromIndex, int toIndex)
    {
        if (node.Tag is not OutlineRef r) return;
        switch (r.Kind)
        {
            case SelectionKind.Feature:
                _document.Execute(new ReorderFeatureCommand(fromIndex, toIndex));
                _document.Selection.Set(SelectionKind.Feature, toIndex.ToString(CultureInfo.InvariantCulture));
                break;
            case SelectionKind.Exclusion:
                _document.Execute(new ReorderExclusionCommand(fromIndex, toIndex));
                _document.Selection.Set(SelectionKind.Exclusion, toIndex.ToString(CultureInfo.InvariantCulture));
                break;
            case SelectionKind.ScatterOverride:
                _document.Execute(new ReorderScatterOverrideCommand(fromIndex, toIndex));
                _document.Selection.Set(SelectionKind.ScatterOverride, toIndex.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                break;   // no list-order semantics for this category: drop rejected
        }
    }

    // ---- palette + spawn-list wiring ---------------------------------------------------------------------

    /// <summary>The kit-id -> category-label map the palette groups by. A seam (like <see cref="CreateDocument"/>)
    /// so a headless test injects a fixed map without a manifest; defaults to the viewport's parsed
    /// <see cref="ViewportWorld.KindCategories"/>, which is immutable after construction.</summary>
    protected virtual IReadOnlyDictionary<string, string> PaletteKindCategories() => _viewport.KindCategories;

    // Group the kit ids by category into the twice-sorted source (categories ordinal, kit ids ordinal within
    // each). Built once in OnEnter because KindCategories never changes over a scene's lifetime.
    void BuildPaletteSource(IReadOnlyDictionary<string, string> categories)
    {
        _paletteSource.Clear();
        var byCategory = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in categories)
        {
            if (!byCategory.TryGetValue(kv.Value, out List<string>? kinds))
            {
                kinds = new List<string>();
                byCategory[kv.Value] = kinds;
            }
            kinds.Add(kv.Key);
        }
        foreach (KeyValuePair<string, List<string>> group in byCategory)
        {
            group.Value.Sort(StringComparer.Ordinal);
            _paletteSource.Add(new PaletteCategory(group.Key, group.Value));
        }
    }

    // Rebuild the category tree for a filter substring: keep only leaves that match case-insensitively, drop a
    // category left with zero matches, and re-resolve each surviving category's expansion. Called only when the
    // filter text changes (see RefreshPalettes), never per frame.
    void RebuildPaletteTree(string filter)
    {
        string needle = filter.Trim();
        bool filtering = needle.Length > 0;

        // Snapshot expansion from the CURRENT (pre-clear) tree only while it is unfiltered, so a filter's
        // forced-open categories never overwrite the user's real collapse choices and clearing the filter
        // restores exactly what they left. A category the filter hides keeps its remembered value in the
        // persistent map (it is absent from Roots to snapshot, but was captured the last time it was shown).
        // _paletteTreeFilter is always already trimmed (see the assignment below), so no re-Trim here.
        if (_paletteTreeFilter.Length == 0)
            foreach (TreeNode root in _paletteTree.Roots)
                if (root.Tag is string label) _paletteExpansion[label] = root.Expanded;

        _paletteTree.Roots.Clear();
        _paletteTree.Selected = null;
        foreach (PaletteCategory category in _paletteSource)
        {
            var node = new TreeNode(LocalizedText.Raw(category.Label), category.Label);
            foreach (string kind in category.Kinds)
            {
                if (filtering && kind.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                node.Children.Add(new TreeNode(LocalizedText.Raw(kind), new PaletteLeaf(kind)));
            }
            if (node.Children.Count == 0) continue;   // hide categories with no matching leaves
            // Filtering forces the matches visible; unfiltered restores the remembered state (default: expanded).
            node.Expanded = filtering
                || !_paletteExpansion.TryGetValue(category.Label, out bool wasExpanded) || wasExpanded;
            _paletteTree.Roots.Add(node);
        }
        _paletteTreeFilter = needle;   // trimmed, so RefreshPalettes' trimmed compare matches what was actually built
    }

    // Rebuild the flat spawn-archetype list for a filter substring, preserving the game-authored order (no
    // categories, no re-sort). The "player spawn" entry is PINNED at the top above every archetype and is never
    // filtered out (it is not an archetype, so the archetype filter box does not apply to it): tapping it flips the
    // spawn tool to placing a player start. Called only when the spawn filter text changes.
    void RebuildSpawnList(string filter)
    {
        string needle = filter.Trim();
        _spawnList.Roots.Clear();
        _spawnList.Selected = null;
        _spawnList.Roots.Add(new TreeNode(LocalizedText.Raw("player spawn"), new PaletteLeaf("", PlayerSpawn: true)));
        foreach (string archetype in _options.SpawnArchetypes)
        {
            if (needle.Length > 0 && archetype.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _spawnList.Roots.Add(new TreeNode(LocalizedText.Raw(archetype), new PaletteLeaf(archetype)));
        }
        _spawnTreeFilter = needle;   // trimmed, so RefreshPalettes' trimmed compare matches what was actually built
    }

    /// <summary>Rebuilds the palette tree and / or the spawn list when its filter box text, trimmed, no longer
    /// matches what the live view was last built for (trimmed too, since that is what <see cref="RebuildPaletteTree"/>
    /// / <see cref="RebuildSpawnList"/> actually match against). Comparing trimmed text on both sides means an
    /// edit that only adds or removes leading/trailing whitespace - the matched leaves are unchanged either way -
    /// does not trigger a rebuild. Called once per widget step after the filter boxes are driven, so a rebuild
    /// happens only on a real filter change, not every frame. Internal so a headless test can trigger it after a
    /// <see cref="TextInput.SetText"/> without a full UI frame.</summary>
    internal void RefreshPalettes()
    {
        if (!string.Equals(_paletteFilter.Text.Trim(), _paletteTreeFilter, StringComparison.Ordinal))
            RebuildPaletteTree(_paletteFilter.Text);
        if (!string.Equals(_spawnFilter.Text.Trim(), _spawnTreeFilter, StringComparison.Ordinal))
            RebuildSpawnList(_spawnFilter.Text);
    }

    // A leaf carries the kit id; a category body-tap (Tag is the label string) never changes the placed kind.
    void OnPaletteSelected(TreeNode node)
    {
        if (node.Tag is PaletteLeaf leaf) _controller.PlaceKind = leaf.Kind;
    }

    void OnSpawnSelected(TreeNode node)
    {
        if (node.Tag is not PaletteLeaf leaf) return;
        if (leaf.PlayerSpawn) { _controller.PlacingPlayerSpawn = true; return; }   // the pinned player-start entry
        _controller.PlacingPlayerSpawn = false;
        _controller.SpawnArchetype = leaf.Kind;
    }

    void OnFeatureTypeSelected(TreeNode node)
    {
        if (node.Tag is PaletteLeaf leaf) _controller.PlaceFeatureType = leaf.Kind;
    }

    // Rebuild the flat feature-type list from the registry's registered types, preserving registration order (the
    // default registry yields lake, flatten, ridge, rim). Built once in OnEnter (the registry is immutable after).
    void RebuildFeatureList()
    {
        _featureList.Roots.Clear();
        _featureList.Selected = null;
        foreach (string type in _document.Registry.FeatureTypes)
            _featureList.Roots.Add(new TreeNode(LocalizedText.Raw(type), new PaletteLeaf(type)));
    }

    // The initial placed feature type: the first registered type, or empty when the registry has none.
    string DefaultFeatureType()
    {
        foreach (string type in _document.Registry.FeatureTypes) return type;
        return "";
    }

    // The initial placed kind: the first kit id of the first category (both sorted), or empty with no kits.
    string DefaultPlaceKind() =>
        _paletteSource.Count > 0 && _paletteSource[0].Kinds.Count > 0 ? _paletteSource[0].Kinds[0] : "";

    // Slot a filter box across the top of the palette region and hand the rest to the tree / list. Guards a
    // region shorter than the filter box (a degenerate window) so both sub-rects stay non-negative.
    static (Rect Filter, Rect Body) SplitPaletteRegion(Rect region)
    {
        float filterH = MathF.Min(PaletteFilterHeight, region.Height);
        var filter = new Rect(region.X + 4f, region.Y + 4f,
            MathF.Max(0f, region.Width - 8f), MathF.Max(0f, filterH - 6f));
        float bodyTop = region.Y + filterH;
        var body = new Rect(region.X, bodyTop, region.Width, MathF.Max(0f, region.Bottom - bodyTop));
        return (filter, body);
    }

    void RebuildOutline()
    {
        _outline.Roots.Clear();
        _outline.Roots.Add(TerrainNode());
        // Biomes sits BESIDE Terrain (a sibling category root, not a child), for consistency with the other
        // per-collection categories (Features, Exclusions): each is a top-level category of selectable nodes.
        _outline.Roots.Add(Category("Biomes", BiomeBandNodes()));
        _outline.Roots.Add(Category("Placements", PlacementNodes()));
        _outline.Roots.Add(Category("Spawns", SpawnNodes()));
        _outline.Roots.Add(Category("Player Spawns", PlayerSpawnNodes()));
        _outline.Roots.Add(Category("Features", FeatureNodes()));
        _outline.Roots.Add(Category("Exclusions", ExclusionNodes()));
        _outline.Roots.Add(Category("Scatter Overrides", ScatterOverrideNodes()));
        _outline.Roots.Add(Category("Scatter Layers", ScatterLayerNodes()));
        _outline.Roots.Add(Category("Companion Layers", CompanionLayerNodes()));
        _outline.Roots.Add(Category("Regions", RegionNodes()));
        // Every root above is a freshly-`new`'d TreeNode, so any prior _outline.Selected reference is now
        // orphaned (no longer reachable from Roots). Re-resolve it against the fresh tree, the fix for the
        // outline highlight dropping on every document edit.
        SyncOutlineSelection();
    }

    // The terrain root: a single selectable leaf (no children) carrying the singleton Terrain selection, so its
    // inspector exposes the editable water level plus the read-only seed / biome count. Terrain has no id (the
    // kind is the whole key), so the OutlineRef id is the empty string.
    static TreeNode TerrainNode() =>
        new TreeNode(LocalizedText.Raw("Terrain"), new OutlineRef(SelectionKind.Terrain, ""));

    static TreeNode Category(string label, IEnumerable<TreeNode> children)
    {
        var root = new TreeNode(LocalizedText.Raw(label)) { Expanded = true };
        foreach (TreeNode child in children) root.Children.Add(child);
        return root;
    }

    IEnumerable<TreeNode> PlacementNodes()
    {
        foreach (MapPlacement p in _document.Doc.Placements)
            yield return new TreeNode(LocalizedText.Raw($"{p.Id} ({p.Kind})"), new OutlineRef(SelectionKind.Placement, p.Id));
    }

    IEnumerable<TreeNode> SpawnNodes()
    {
        foreach (MapSpawn s in _document.Doc.Spawns)
            yield return new TreeNode(LocalizedText.Raw($"{s.Id} ({s.ArchetypeId})"), new OutlineRef(SelectionKind.Spawn, s.Id));
    }

    // The Player Spawns category's children: one selectable node per player start. Player spawns carry no archetype
    // (unlike NPC spawns), so the label is the id plus a "(disabled)" suffix while the spawn is off, surfacing the
    // enabled state in the outline the way the NPC node surfaces its archetype.
    IEnumerable<TreeNode> PlayerSpawnNodes()
    {
        foreach (MapPlayerSpawn s in _document.Doc.PlayerSpawns)
        {
            string label = s.Enabled ? s.Id : $"{s.Id} (disabled)";
            yield return new TreeNode(LocalizedText.Raw(label), new OutlineRef(SelectionKind.PlayerSpawn, s.Id));
        }
    }

    // A feature node shows its Name when set, else the index/type fallback ("[i] type"). Features carry no
    // targeting hint (that is an exclusion-only concept, see ExclusionNodes).
    IEnumerable<TreeNode> FeatureNodes()
    {
        List<MapFeature> features = _document.Doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
        {
            MapFeature f = features[i];
            string label = string.IsNullOrEmpty(f.Name) ? $"[{i}] {f.Type}" : f.Name!;
            yield return new TreeNode(LocalizedText.Raw(label),
                new OutlineRef(SelectionKind.Feature, i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // An exclusion node shows its Name when set, else the index fallback ("exclusion[i]"), ALWAYS suffixed with
    // the targeting hint from its Layers (see TargetingHint), so the outline surfaces which scatter layers the
    // exclusion masks without opening the inspector.
    IEnumerable<TreeNode> ExclusionNodes()
    {
        List<MapExclusion> exclusions = _document.Doc.Exclusions;
        for (int i = 0; i < exclusions.Count; i++)
        {
            MapExclusion e = exclusions[i];
            string baseLabel = string.IsNullOrEmpty(e.Name) ? $"exclusion[{i}]" : e.Name!;
            yield return new TreeNode(LocalizedText.Raw($"{baseLabel} ({TargetingHint(e.Layers)})"),
                new OutlineRef(SelectionKind.Exclusion, i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // The exclusion tree label's targeting suffix: "all" for a null Layers filter (masks scatter on every
    // layer, including future ones, see MapExclusion.Layers), else the comma-joined explicit layer names in
    // list order (an empty explicit list, legal per the model, reads as an empty hint).
    static string TargetingHint(List<string>? layers) => layers is null ? "all" : string.Join(", ", layers);

    // A scatter-override node shows its Name when set, else the index fallback ("override[i]"), ALWAYS suffixed
    // with the same targeting hint an exclusion node carries (see TargetingHint), so the outline surfaces which
    // scatter layers this override retunes without opening the inspector. Order is significant here (first matching
    // override wins), so the outline lists overrides in document order and a reorder is a real edit, unlike the
    // order-free exclusions above.
    IEnumerable<TreeNode> ScatterOverrideNodes()
    {
        List<MapScatterOverrideDoc> overrides = _document.Doc.ScatterOverrides;
        for (int i = 0; i < overrides.Count; i++)
        {
            MapScatterOverrideDoc o = overrides[i];
            string baseLabel = string.IsNullOrEmpty(o.Name) ? $"override[{i}]" : o.Name!;
            yield return new TreeNode(LocalizedText.Raw($"{baseLabel} ({TargetingHint(o.Layers)})"),
                new OutlineRef(SelectionKind.ScatterOverride, i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    IEnumerable<TreeNode> RegionNodes()
    {
        foreach (MapRegion r in _document.Doc.Regions)
            yield return new TreeNode(LocalizedText.Raw(r.Name), new OutlineRef(SelectionKind.Region, r.Name));
    }

    // The Biomes category's children: one selectable node per band (label = "[i] Biome start..end", with an open
    // nullable edge rendered as "*"), then a trailing "[+ add band]" ACTION node. Bands carry no name and no
    // viewport picking, so the outline is their only selection surface, and the add action is the only add
    // affordance (there is no place-tool or palette for bands, and the PropertyGrid has no button row). The action
    // node's Tag is an OutlineAction (not an OutlineRef), so OnOutlineSelected runs the add instead of selecting.
    IEnumerable<TreeNode> BiomeBandNodes()
    {
        List<MapBiomeBand> bands = _document.Doc.Terrain.Biomes;
        for (int i = 0; i < bands.Count; i++)
            yield return new TreeNode(LocalizedText.Raw(BiomeBandLabel(bands[i], i)),
                new OutlineRef(SelectionKind.BiomeBand, i.ToString(CultureInfo.InvariantCulture)));
        yield return new TreeNode(LocalizedText.Raw("[+ add band]"), new OutlineAction(OutlineActionKind.AddBiomeBand));
    }

    // A compact band label: "[i] Biome start..end". A null (open) edge renders as "*" (its +/- infinity cannot be a
    // finite number). Numbers use the invariant culture so a value like 12.5 reads the same everywhere.
    static string BiomeBandLabel(MapBiomeBand band, int index) =>
        $"[{index}] {band.Biome} {BandEdge(band.Start)}..{BandEdge(band.End)}";

    static string BandEdge(float? edge) => edge is float v ? v.ToString(CultureInfo.InvariantCulture) : "*";

    // The Scatter Layers category's children: one selectable node per layer (label = its unique name), then a
    // trailing "[+ add layer]" ACTION node. Scatter layers have no viewport geometry, so the outline is their only
    // selection surface, and the add action is the only add affordance. The action node's Tag is an OutlineAction
    // (not an OutlineRef), so OnOutlineSelected runs the add instead of selecting.
    IEnumerable<TreeNode> ScatterLayerNodes()
    {
        foreach (MapScatterLayer layer in _document.Doc.ScatterLayers)
            yield return new TreeNode(LocalizedText.Raw(layer.Name), new OutlineRef(SelectionKind.ScatterLayer, layer.Name));
        yield return new TreeNode(LocalizedText.Raw("[+ add layer]"), new OutlineAction(OutlineActionKind.AddScatterLayer));
    }

    // The Companion Layers category's children: one selectable node per layer (label = "name (host <host>)", so
    // the outline surfaces which scatter layer a companion rings without opening the inspector), then a trailing
    // "[+ add companion]" ACTION node. The action node is gated on there being a scatter layer to host it (bug
    // #25): a companion rings a host scatter layer's placements, so with none the affordance is meaningless,
    // and activating it anyway used to crash the editor via an undeclared "" HostLayer.
    IEnumerable<TreeNode> CompanionLayerNodes()
    {
        foreach (MapCompanionLayer layer in _document.Doc.CompanionLayers)
            yield return new TreeNode(LocalizedText.Raw($"{layer.Name} (host {layer.HostLayer})"),
                new OutlineRef(SelectionKind.CompanionLayer, layer.Name));
        if (_document.Doc.ScatterLayers.Count > 0)
            yield return new TreeNode(LocalizedText.Raw("[+ add companion]"), new OutlineAction(OutlineActionKind.AddCompanionLayer));
    }

    void RebuildInspector()
    {
        _inspector.Rows.Clear();
        _nameRow = null;
        _inspectorShapeKind = null;
        _inspectorLayersAllOn = null;
        _inspectorScatterNames = null;
        _inspectorRuleCount = null;
        _inspectorCompanionMismatch = null;
        if (SculptMode) { BuildSculptInspector(); return; }   // a tool-mode panel, not a selection panel
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Terrain: BuildTerrainInspector(); break;
            case SelectionKind.Placement: BuildPlacementInspector(sel.Id); break;
            case SelectionKind.Spawn: BuildSpawnInspector(sel.Id); break;
            case SelectionKind.PlayerSpawn: BuildPlayerSpawnInspector(sel.Id); break;
            case SelectionKind.Feature: BuildFeatureInspector(sel.Id); break;
            case SelectionKind.Exclusion: BuildExclusionInspector(sel.Id); break;
            case SelectionKind.ScatterOverride: BuildScatterOverrideInspector(sel.Id); break;
            case SelectionKind.Region: BuildRegionInspector(sel.Id); break;
            case SelectionKind.BiomeBand: BuildBiomeBandInspector(sel.Id); break;
            case SelectionKind.ScatterLayer: BuildScatterLayerInspector(sel.Id); break;
            case SelectionKind.CompanionLayer: BuildCompanionLayerInspector(sel.Id); break;
            default: BuildLayersInspector(); break;   // nothing selected: the visibility Layers panel
        }
    }

    // The empty-selection inspector is the Layers panel: a Visible toggle per group, a Rendering section (the
    // Textured props toggle), then one per named scatter layer. Group toggles only gate draws / picks (no rebuild).
    // The Textured props toggle and a scatter-layer toggle both rebuild the streamed world (RebuildWorldForVisibility)
    // since each is read at load time. Raw dev-tool labels (the editor is not player-facing). Rebuilt on every
    // selection change, so the panel tracks the live scatter-layer set.
    void BuildLayersInspector()
    {
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Groups"), LocalizedText.Raw(
            "Shows or hides a whole category of editor markers and shapes in the viewport. Editor view only, " +
            "turning a group off never changes the saved document or what the game loads, it only stops that " +
            "category from drawing and being pickable here.")));
        foreach (VisibilityGroup group in Enum.GetValues<VisibilityGroup>())
        {
            VisibilityGroup g = group;   // capture per iteration for the closures
            _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(GroupLabel(g)),
                () => _visibility.GetGroup(g), v => _visibility.SetGroup(g, v),
                LocalizedText.Raw($"Shows or hides every {GroupLabel(g)} in the viewport. Editor view only, " +
                    "does not affect the saved document or the game.")));
        }
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Rendering"), LocalizedText.Raw(
            "Viewport rendering options. Editor view only, these never change the saved document or what the " +
            "game loads.")));
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Textured props"),
            () => _options.TexturedProps,
            v => { _options.TexturedProps = v; InvalidateViewportKitMeshes(); RebuildWorldForVisibility(); },
            LocalizedText.Raw("When on (the default, matching gameplay) a kit prop flagged textured shows its " +
                "baked materials in the viewport. Turn off to render every prop in its flattened average colour " +
                "instead, which can be easier to read while placing a dense textured forest. Editor view only, " +
                "does not affect the saved document or the game, and rebuilds the streamed world to take effect.")));

        if (_document.Doc.ScatterLayers.Count > 0)
            _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Scatter Layers"), LocalizedText.Raw(
                "Streams or hides one named scatter layer's placed props in the viewport, independent of the " +
                "whole-group toggles above.")));
        foreach (MapScatterLayer layer in _document.Doc.ScatterLayers)
        {
            string name = layer.Name;
            _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(name),
                () => _visibility.GetLayer(name),
                v => { _visibility.SetLayer(name, v); RebuildWorldForVisibility(); },
                LocalizedText.Raw($"Streams or hides the '{name}' scatter layer's placed props in the viewport. " +
                    "Editor view only, does not affect the saved document.")));
        }
    }

    // The raw dev-tool label for a visibility group (FeatureMarkers reads "Feature markers", ScatterOverrides reads
    // "Scatter overrides", the rest are their enum name). No em / en dashes or semicolons (the editor label
    // convention).
    static string GroupLabel(VisibilityGroup group) => group switch
    {
        VisibilityGroup.FeatureMarkers => "Feature markers",
        VisibilityGroup.ScatterOverrides => "Scatter overrides",
        VisibilityGroup.PlayerSpawns => "Player spawns",
        _ => group.ToString(),
    };

    /// <summary>GPU seam: rebuilds the streamed viewport world so a scatter-layer visibility toggle takes effect
    /// (hidden layers drop out of the fresh prop layers). Called directly from the Layers-panel scatter toggle,
    /// NOT through <see cref="EditorDocument.WorldRebuildPending"/> (visibility is not a document change). No-op
    /// until the world is built, and overridden headless in tests. Re-points the controller field at the rebuilt
    /// world, matching <see cref="CheckWorldRebuild"/>. <see cref="ViewportWorld.Rebuild"/> retains the viewport's
    /// kit meshes and splat material across this call, so a scatter-layer toggle (which never changes which mesh
    /// form a kit id loads) does not need <see cref="InvalidateViewportKitMeshes"/> first, unlike the Textured
    /// props toggle.</summary>
    protected virtual void RebuildWorldForVisibility()
    {
        if (!_viewport.IsBuilt) return;
        _viewport.Rebuild(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;
    }

    /// <summary>GPU seam: invalidates the viewport's retained kit-mesh cache (and its cached splat material) so the
    /// next <see cref="RebuildWorldForVisibility"/> reloads every kit id from disk instead of serving a stale
    /// cached form. The Textured props Layers-panel toggle calls this immediately before
    /// <see cref="RebuildWorldForVisibility"/>, because <see cref="ViewportWorld.LoadKitMeshes"/> keys its cache on
    /// the entry id alone and does not encode which form (textured parts vs. flattened) was loaded, so a retained
    /// cache would otherwise serve the pre-toggle form. Overridden headless in tests (mirrors
    /// <see cref="RebuildWorldForVisibility"/>).</summary>
    protected virtual void InvalidateViewportKitMeshes() => _viewport.InvalidateKitMeshes();

    // The single spot every FloatRow the inspector builds funnels through, directly or via a domain wrapper
    // (AddFeatureRow, AddBandFloatRow, AddScatterFloatRow, AddCompanionFloatRow, AddShapeRow): wires
    // FloatRow.GestureEnded to SealGesture so a scrub or edit commit on this row seals the undo gesture the
    // moment it ends. Without this, scrubbing two different fields back to back (e.g. WaterLevel then
    // BiomeBlend) can coalesce into ONE undo step through the underlying command's same-gesture TryMerge
    // (EditTerrainCommand merges ANY two terrain edits, by design, within one gesture) - sealing here draws the
    // gesture boundary at the widget level so each field's scrub becomes its own undo step. Same signature as
    // the FloatRow constructor, so every existing call site converts by dropping "_inspector.Rows.Add(new
    // FloatRow(" down to "AddFloatRow(".
    FloatRow AddFloatRow(LocalizedText label, Func<float> get, Action<float> set,
        float min = float.MinValue, float max = float.MaxValue, float dragScale = 0.01f, int decimals = 2,
        LocalizedText? description = null)
    {
        var row = new FloatRow(label, get, set, min, max, dragScale, decimals, description);
        row.GestureEnded += _document.SealGesture;
        _inspector.Rows.Add(row);
        return row;
    }

    // The terrain root inspector: every terrain scalar as an editable row (each routed through the widened
    // EditTerrainCommand so scrubs coalesce and the scatter-honouring world rebuild fires), plus a read-only
    // biome-count summary. Each setter captures the LIVE field value as the command's old value before Execute
    // applies the new one. Seed and DetailOctaves are integers, so their rows scrub whole steps (decimals 0) and
    // round to an int on write. Biome bands themselves are edited via the Biomes outline category, not here.
    void BuildTerrainInspector()
    {
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Water")));
        AddFloatRow(LocalizedText.Raw("WaterLevel"),
            () => _document.Doc.Terrain.WaterLevel,
            v => _document.Execute(new EditTerrainCommand(newWaterLevel: v, oldWaterLevel: _document.Doc.Terrain.WaterLevel)),
            description: LocalizedText.Raw(
                "World-space height, in world units, of the flat water plane. Terrain below this height reads as " +
                "submerged. Also feeds scatter placement, so raising or lowering it can drown or reveal existing " +
                "prop placements on the next world rebuild."));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("World")));
        AddFloatRow(LocalizedText.Raw("Seed"),
            () => _document.Doc.Terrain.Seed,
            v => _document.Execute(new EditTerrainCommand(
                newSeed: (int)MathF.Round(v), oldSeed: _document.Doc.Terrain.Seed)),
            dragScale: 1f, decimals: 0,
            description: LocalizedText.Raw(
                "Random seed driving the terrain noise and scatter placement. Two documents with the same seed " +
                "and the same parameters below generate identical terrain. Change it to get a different variation " +
                "of the same settings."));
        AddFloatRow(LocalizedText.Raw("BiomeBlend"),
            () => _document.Doc.Terrain.BiomeBlend,
            v => _document.Execute(new EditTerrainCommand(newBiomeBlend: v, oldBiomeBlend: _document.Doc.Terrain.BiomeBlend)),
            min: 0f,
            description: LocalizedText.Raw(
                "Blend distance, in world units, across a biome band boundary. Higher values soften the " +
                "transition between two adjacent biomes' height and scatter rules, lower values make the " +
                "boundary sharper."));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Biomes"),
            () => _document.Doc.Terrain.Biomes.Count.ToString(CultureInfo.InvariantCulture),
            description: LocalizedText.Raw(
                "Number of biome bands currently defined on this terrain. Bands are added, removed, and edited " +
                "via the Biomes category in the outline, not here.")));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Noise")));
        AddFloatRow(LocalizedText.Raw("GentleFrequency"),
            () => _document.Doc.Terrain.GentleFrequency,
            v => _document.Execute(new EditTerrainCommand(newGentleFrequency: v, oldGentleFrequency: _document.Doc.Terrain.GentleFrequency)),
            min: 0f, dragScale: 0.001f, decimals: 3,
            description: LocalizedText.Raw(
                "Feature size, in inverse world units, of the broad rolling hills layer. Lower values stretch the " +
                "hills wider and smoother, higher values pack them closer together."));
        AddFloatRow(LocalizedText.Raw("GentleAmplitude"),
            () => _document.Doc.Terrain.GentleAmplitude,
            v => _document.Execute(new EditTerrainCommand(newGentleAmplitude: v, oldGentleAmplitude: _document.Doc.Terrain.GentleAmplitude)),
            min: 0f,
            description: LocalizedText.Raw(
                "Height swing, in world units, of the broad rolling hills layer. Higher values make the gentle " +
                "hills taller, zero flattens them out entirely."));
        AddFloatRow(LocalizedText.Raw("DetailFrequency"),
            () => _document.Doc.Terrain.DetailFrequency,
            v => _document.Execute(new EditTerrainCommand(newDetailFrequency: v, oldDetailFrequency: _document.Doc.Terrain.DetailFrequency)),
            min: 0f, dragScale: 0.001f, decimals: 3,
            description: LocalizedText.Raw(
                "Feature size, in inverse world units, of the fine detail noise layered on top of the gentle " +
                "hills. Higher values pack the small bumps closer together for a rougher surface."));
        AddFloatRow(LocalizedText.Raw("DetailOctaves"),
            () => _document.Doc.Terrain.DetailOctaves,
            v => _document.Execute(new EditTerrainCommand(
                newDetailOctaves: (int)MathF.Round(v), oldDetailOctaves: _document.Doc.Terrain.DetailOctaves)),
            min: 1f, dragScale: 1f, decimals: 0,
            description: LocalizedText.Raw(
                "Number of detail noise layers summed together. More octaves add finer, more varied bumps to the " +
                "terrain surface but cost more to generate."));
    }

    // The inline-rename Name row shared by the region, placement, and spawn inspectors. A closure tracks the
    // CURRENT key across renames, so the row (and every downstream row that reads the returned getter) keeps
    // working and keeps focus while the user types. The setter guards blank / unchanged / collision / vanished,
    // routes the rename through `rename`, then queues a deferred re-select of the new key (fired once the row
    // loses focus, see UpdateChrome) so the name-keyed selection follows the rename. Returns a getter for the
    // live key so the caller's remaining rows track the element across a rename.
    Func<string> AddNameRow(SelectionKind kind, string key, Func<string, bool> exists,
        Func<string, string, IEditorCommand> rename, LocalizedText description)
    {
        string current = key;
        var row = new TextRow(LocalizedText.Raw("Name"),
            () => current,
            v =>
            {
                if (string.IsNullOrWhiteSpace(v) || string.Equals(v, current, StringComparison.Ordinal)) return;
                if (exists(v) || !exists(current)) return;   // collision or vanished
                // Set the pending key BEFORE executing the rename: Execute fires DocumentChanged synchronously,
                // which rebuilds the outline and calls SyncOutlineSelection on the spot, and that read needs the
                // NEW key already in place to resolve the freshly rebuilt tree (see SyncOutlineSelection).
                _pendingSelectKind = kind;
                _pendingSelectId = v;
                _document.Execute(rename(current, v));
                current = v;
            }, description: description);
        _nameRow = row;
        _inspector.Rows.Add(row);
        return () => current;
    }

    // The inline Name row for INDEX-pinned selections (feature, exclusion): the selection key is the list
    // index, which a rename never moves (only reorder/delete do, both remapped through the command's
    // IVisibilityEffect via the EditorDocument event path, event-driven rather than called from here), so
    // unlike AddNameRow there is no pending re-select to queue and
    // no `_nameRow` chord-gating hook to wire (the grid's own HasActiveEditor aggregate already covers a
    // focused TextRow). Name is optional (MapFeature.Name / MapExclusion.Name: empty means unnamed, falling
    // back to the index label), so clearing to blank is a legal target, not a guard-reject. Only an UNCHANGED
    // value or a non-empty duplicate of another element's live name is rejected, mirroring the rename command's
    // own GuardNoFeatureName/GuardNoExclusionName check (normalized non-empty, Ordinal, excluding this index),
    // so the row rejects a collision before the command would throw.
    void AddIndexNameRow(int index, Func<int, string> getName, Func<string, int, bool> nameExists,
        Func<int, string, string, IEditorCommand> rename, LocalizedText description)
    {
        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Name"),
            () => getName(index),
            v =>
            {
                string old = getName(index);
                if (string.Equals(v, old, StringComparison.Ordinal)) return;   // unchanged: no command
                if (v.Length > 0 && nameExists(v, index)) return;   // duplicate target: reject before the command throws
                _document.Execute(rename(index, v, old));
            }, description: description));
    }

    // The per-element "Visible" toggle, bound to the visibility hidden set (Visible on == not hidden). Added to
    // every element inspector so the operator can hide a single placement / spawn / feature / exclusion / region
    // from the viewport (draws and picks) while it stays in the outline. `id` is polled through a getter so a
    // renamable element (its key follows the rename via the caller's `cur()` closure) keeps toggling the right key.
    // The description is the same for every caller (the toggle means the same thing everywhere), so it is fixed
    // here rather than threaded through every call site.
    void AddVisibleRow(SelectionKind kind, Func<string> id)
    {
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Visible"),
            () => !_visibility.IsElementHidden(kind, id()),
            v => _visibility.SetElementHidden(kind, id(), !v),
            LocalizedText.Raw(
                "Hides this element from the viewport (both drawing and picking) while it stays in the outline " +
                "and the saved document. Editor view only, does not affect the game.")));
    }

    void BuildPlacementInspector(string id)
    {
        if (Placement(id) is null) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.Placement, id,
            v => Placement(v) is not null, (oldId, newId) => new RenamePlacementCommand(oldId, newId),
            LocalizedText.Raw(
                "Unique id for this placement. Renaming it updates the outline node and the current selection to " +
                "follow the new id. Must be non-empty and not collide with another placement's id."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Transform")));
        AddFloatRow(LocalizedText.Raw("X"),
            () => Placement(cur())?.X ?? 0f, v => MovePlacement(cur(), x: v),
            description: LocalizedText.Raw("World-space X coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Z"),
            () => Placement(cur())?.Z ?? 0f, v => MovePlacement(cur(), z: v),
            description: LocalizedText.Raw("World-space Z coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Yaw"),
            () => Placement(cur())?.Yaw ?? 0f, v => _document.Execute(new RotatePlacementCommand(cur(), v)),
            description: LocalizedText.Raw(
                "Facing rotation around the vertical (Y) axis, in radians."));
        AddFloatRow(LocalizedText.Raw("Scale"),
            () => Placement(cur())?.Scale ?? 1f, v => _document.Execute(new ScalePlacementCommand(cur(), v)),
            min: 0.01f,
            description: LocalizedText.Raw(
                "Uniform scale multiplier applied to the placed kit's mesh. 1 is the kit's authored size, below " +
                "1 shrinks it, above 1 grows it."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        AddVisibleRow(SelectionKind.Placement, cur);
    }

    void BuildSpawnInspector(string id)
    {
        if (Spawn(id) is null) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.Spawn, id,
            v => Spawn(v) is not null, (oldId, newId) => new RenameSpawnCommand(oldId, newId),
            LocalizedText.Raw(
                "Unique id for this spawn marker. Renaming it updates the outline node and the current selection " +
                "to follow the new id. Must be non-empty and not collide with another spawn's id."));
        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Archetype"),
            () => Spawn(cur())?.ArchetypeId ?? "",
            v => { if (Spawn(cur()) is not null) _document.Execute(new SetSpawnArchetypeCommand(cur(), v)); },
            description: LocalizedText.Raw(
                "Id of the NPC archetype spawned here, matched against the game's own archetype registry (the " +
                "spawn-tool palette below lists the ids the current game offers). The editor accepts any text " +
                "here, so a typo only surfaces once the game fails to resolve it.")));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Transform")));
        AddFloatRow(LocalizedText.Raw("X"),
            () => Spawn(cur())?.X ?? 0f, v => MoveSpawn(cur(), x: v),
            description: LocalizedText.Raw("World-space X coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Z"),
            () => Spawn(cur())?.Z ?? 0f, v => MoveSpawn(cur(), z: v),
            description: LocalizedText.Raw("World-space Z coordinate, in world units."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Enabled"),
            () => Spawn(cur())?.Enabled ?? false, v => _document.Execute(new SetSpawnEnabledCommand(cur(), v)),
            description: LocalizedText.Raw(
                "Whether the game spawns an NPC here at all. A disabled spawn stays in the document and the " +
                "outline but never produces an NPC.")));
        AddVisibleRow(SelectionKind.Spawn, cur);
    }

    // The player-spawn inspector mirrors the NPC spawn one (inline-rename Name, X / Z through MovePlayerSpawnCommand,
    // Enabled, per-element Visible), swapping the NPC Archetype row for a Yaw row (player spawns carry a facing, NPC
    // spawns do not). Yaw is raw radians, matching the placement Yaw row (no degree conversion in this editor), and
    // routes through SetPlayerSpawnYawCommand the same way the placement Yaw row routes through
    // RotatePlacementCommand, so the edit is undoable and marks the document dirty. The NPC Archetype row this Yaw
    // row replaces routes through SetSpawnArchetypeCommand the same way, so it is undoable and marks the document
    // dirty too, unrelated to this row.
    void BuildPlayerSpawnInspector(string id)
    {
        if (PlayerSpawn(id) is null) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.PlayerSpawn, id,
            v => PlayerSpawn(v) is not null, (oldId, newId) => new RenamePlayerSpawnCommand(oldId, newId),
            LocalizedText.Raw(
                "Unique id for this player start. Game code picks which player spawn to use by this id (there is " +
                "no archetype, that choice is game code's concern), so renaming it changes what the game must " +
                "reference. Must be non-empty and not collide with another player spawn's id."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Transform")));
        AddFloatRow(LocalizedText.Raw("X"),
            () => PlayerSpawn(cur())?.X ?? 0f, v => MovePlayerSpawn(cur(), x: v),
            description: LocalizedText.Raw("World-space X coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Z"),
            () => PlayerSpawn(cur())?.Z ?? 0f, v => MovePlayerSpawn(cur(), z: v),
            description: LocalizedText.Raw("World-space Z coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Yaw"),
            () => PlayerSpawn(cur())?.Yaw ?? 0f, v => _document.Execute(new SetPlayerSpawnYawCommand(cur(), v)),
            description: LocalizedText.Raw(
                "Facing rotation around the vertical (Y) axis, in radians, the direction the player faces on " +
                "spawn."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Enabled"),
            () => PlayerSpawn(cur())?.Enabled ?? false, v => _document.Execute(new SetPlayerSpawnEnabledCommand(cur(), v)),
            description: LocalizedText.Raw(
                "Whether this player start is a candidate the game can use at all. A disabled player spawn stays " +
                "in the document but game code should skip it when picking a start.")));
        AddVisibleRow(SelectionKind.PlayerSpawn, cur);
    }

    // ---- feature / exclusion / region inspectors -----------------------------------------------------------

    void BuildFeatureInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        MapFeature? feature = FeatureAt(index);
        if (feature is null) return;

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Type"), () => FeatureAt(index)?.Type ?? "",
            description: LocalizedText.Raw(
                "Which terrain-sculpting operation this feature applies (lake, flatten, rim, or ridge). Fixed at " +
                "creation, not editable here.")));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Apply order"), () => FeatureOrderText(index),
            description: LocalizedText.Raw(
                "This feature's position in the fold order and the total feature count. Features apply in list " +
                "order, so the LAST feature covering a given point wins where two overlap. Reorder with " +
                "Ctrl+Up / Ctrl+Down.")));
        AddIndexNameRow(index, i => FeatureAt(i)?.Name ?? "", FeatureNameExists,
            (i, newName, oldName) => new RenameFeatureCommand(i, newName, oldName),
            LocalizedText.Raw(
                "Optional display name shown in the outline instead of the index and type. Leave empty to fall " +
                "back to the index label. Must not duplicate another feature's name."));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        switch (feature)
        {
            case LakeFeatureDoc:
                AddFeatureRow<LakeFeatureDoc>(index, "CenterX",
                    "World-space X coordinate of the lake's center, in world units.", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<LakeFeatureDoc>(index, "CenterZ",
                    "World-space Z coordinate of the lake's center, in world units.", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<LakeFeatureDoc>(index, "Radius",
                    "Radius of the lake basin, in world units, measured from CenterX/CenterZ.", f => f.Radius, (f, v) => f.Radius = v);
                AddFeatureRow<LakeFeatureDoc>(index, "Depth",
                    "How far, in world units, the lake basin is carved below the surrounding terrain height at " +
                    "its center, fading back to the original height toward the radius.", f => f.Depth, (f, v) => f.Depth = v);
                break;
            case FlattenFeatureDoc:
                AddFeatureRow<FlattenFeatureDoc>(index, "CenterX",
                    "World-space X coordinate of the flattened area's center, in world units.", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "CenterZ",
                    "World-space Z coordinate of the flattened area's center, in world units.", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "Radius",
                    "Radius of the flattened area, in world units, measured from CenterX/CenterZ.", f => f.Radius, (f, v) => f.Radius = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "TargetHeight",
                    "World-space height, in world units, the terrain inside the radius is leveled toward.", f => f.TargetHeight, (f, v) => f.TargetHeight = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "Blend",
                    "Fraction, from 0 to 1, of the radius over which the flatten effect fades out: full effect " +
                    "until Radius*(1-Blend), then ramping down to no effect by Radius. Higher values start the " +
                    "fade closer to the center for a gentler edge.", f => f.Blend, (f, v) => f.Blend = v);
                break;
            case RimFeatureDoc:
                AddFeatureRow<RimFeatureDoc>(index, "CenterX",
                    "World-space X coordinate of the rim's center, in world units.", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<RimFeatureDoc>(index, "CenterZ",
                    "World-space Z coordinate of the rim's center, in world units.", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<RimFeatureDoc>(index, "InnerRadius",
                    "Radius, in world units, of the flat interior left unchanged inside the rim wall.", f => f.InnerRadius, (f, v) => f.InnerRadius = v);
                AddFeatureRow<RimFeatureDoc>(index, "OuterRadius",
                    "Radius, in world units, where the wall ramp reaches full WallHeight and plateaus.", f => f.OuterRadius, (f, v) => f.OuterRadius = v);
                AddFeatureRow<RimFeatureDoc>(index, "WallHeight",
                    "Height, in world units, the rim wall plateau rises above the interior.", f => f.WallHeight, (f, v) => f.WallHeight = v);
                AddFeatureRow<RimFeatureDoc>(index, "Ruggedness",
                    "Amount of jagged noise added to the rim wall's crest so it reads as mountains instead of a " +
                    "smooth berm. 0 is a smooth wall, higher values add rougher variation.", f => f.Ruggedness, (f, v) => f.Ruggedness = v);
                break;
            case RidgeFeatureDoc:
                AddFeatureRow<RidgeFeatureDoc>(index, "PointX",
                    "World-space X coordinate of the ridge line's anchor point, in world units.", f => f.PointX, (f, v) => f.PointX = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PointZ",
                    "World-space Z coordinate of the ridge line's anchor point, in world units.", f => f.PointZ, (f, v) => f.PointZ = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "DirectionX",
                    "X component of the ridge's direction vector from the anchor point. Any length works, it is " +
                    "normalized internally.", f => f.DirectionX, (f, v) => f.DirectionX = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "DirectionZ",
                    "Z component of the ridge's direction vector from the anchor point. Any length works, it is " +
                    "normalized internally.", f => f.DirectionZ, (f, v) => f.DirectionZ = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "Height",
                    "How far, in world units, the ridge crest rises above the surrounding terrain.", f => f.Height, (f, v) => f.Height = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "Width",
                    "Width of the ridge, in world units, measured perpendicular to the direction it runs. Larger " +
                    "values make a broader, gentler wall.", f => f.Width, (f, v) => f.Width = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PassAlong",
                    "Signed distance, in world units, along the ridge line from PointX/PointZ marking the center " +
                    "of a lowered pass or gap through the wall. Only takes effect when PassWidth is positive.", f => f.PassAlong, (f, v) => f.PassAlong = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PassWidth",
                    "Width, in world units, of the pass cut through the ridge at PassAlong. 0 (the default) means " +
                    "no pass at all, a solid, continuous ridge wall.", f => f.PassWidth, (f, v) => f.PassWidth = v);
                break;
            default:
                break;   // unknown/custom feature type: the read-only Type row above is the whole inspector
        }
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        AddVisibleRow(SelectionKind.Feature, () => id);   // index-keyed, non-renamable: a constant id getter
    }

    // One scrubbed parameter of the feature at `index`: get reads the LIVE DTO (the instance at the index is
    // replaced by every edit), set clones the current DTO with the one property changed and routes it through
    // EditFeatureCommand, whose same-index merge makes a scrub coalesce into one undo step.
    void AddFeatureRow<T>(int index, string label, string description, Func<T, float> get, Action<T, float> assign) where T : MapFeature
    {
        AddFloatRow(LocalizedText.Raw(label),
            () => FeatureAt(index) is T f ? get(f) : 0f,
            v =>
            {
                if (FeatureAt(index) is not T current) return;
                var clone = (T)FeatureGeometry.Clone(current);
                assign(clone, v);
                _document.Execute(new EditFeatureCommand(index, clone, current));
            }, description: LocalizedText.Raw(description));
    }

    MapFeature? FeatureAt(int index)
    {
        List<MapFeature> features = _document.Doc.Terrain.Features;
        return index >= 0 && index < features.Count ? features[index] : null;
    }

    // Whether `name` (already caller-normalized non-empty) is already the live name of some OTHER feature,
    // mirroring RenameFeatureCommand's own GuardNoFeatureName guard so the Name row rejects a collision before
    // the command would throw.
    bool FeatureNameExists(string name, int exceptIndex)
    {
        List<MapFeature> features = _document.Doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
            if (i != exceptIndex && string.Equals(features[i].Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // "N of M (last wins overlap)": the feature's 1-based fold position and the feature count, with the last-wins
    // reminder. Features fold in list order, so the last feature over an overlap dominates it (Ctrl+Up / Ctrl+Down
    // reorder the selected feature). Polled live by the inspector's read-only row, so it tracks reorders.
    string FeatureOrderText(int index)
    {
        int count = _document.Doc.Terrain.Features.Count;
        if (index < 0 || index >= count) return "";
        return string.Create(CultureInfo.InvariantCulture, $"{index + 1} of {count} (last wins overlap)");
    }

    void BuildExclusionInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        if (index < 0 || index >= _document.Doc.Exclusions.Count) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        AddIndexNameRow(index, i => ExclusionAt(i)?.Name ?? "", ExclusionNameExists,
            (i, newName, oldName) => new RenameExclusionCommand(i, newName, oldName),
            LocalizedText.Raw(
                "Optional display name shown in the outline instead of the index. Leave empty to fall back to " +
                "the index label. Must not duplicate another exclusion's name."));
        AddVisibleRow(SelectionKind.Exclusion, () => id);
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        AddShapeRows(() => index < _document.Doc.Exclusions.Count ? _document.Doc.Exclusions[index].Shape : null,
            (newShape, oldShape) => _document.Execute(new EditExclusionShapeCommand(index, newShape, oldShape)));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Targeting")));
        AddExclusionLayerRows(index);
        _inspectorLayersAllOn = ExclusionAt(index)?.Layers is null;
        // Capture the scatter-layer name set the targeting rows were built from, so SyncShapeInspector rebuilds
        // these rows when a scatter layer is added / removed / renamed while this exclusion stays selected (the
        // Task 2 review carry-forward: the per-layer rows must never show a stale scatter-layer set).
        _inspectorScatterNames = LiveScatterNames();
    }

    MapExclusion? ExclusionAt(int index)
    {
        List<MapExclusion> exclusions = _document.Doc.Exclusions;
        return index >= 0 && index < exclusions.Count ? exclusions[index] : null;
    }

    // Whether `name` (already caller-normalized non-empty) is already the live name of some OTHER exclusion,
    // mirroring RenameExclusionCommand's own GuardNoExclusionName guard so the Name row rejects a collision
    // before the command would throw.
    bool ExclusionNameExists(string name, int exceptIndex)
    {
        List<MapExclusion> exclusions = _document.Doc.Exclusions;
        for (int i = 0; i < exclusions.Count; i++)
            if (i != exceptIndex && string.Equals(exclusions[i].Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // The exclusion's layer-targeting rows (locked decision 4), built through the shared AddLayerTargetingRows
    // helper: an "All layers" toggle bound to Layers == null (masks scatter on every layer, including future ones,
    // see MapExclusion.Layers), plus one per-layer BoolRow while an explicit list is in effect. Every change routes
    // through EditExclusionLayersCommand so it undoes/merges like any other exclusion edit.
    void AddExclusionLayerRows(int index)
    {
        AddLayerTargetingRows(
            () => ExclusionAt(index)?.Layers,
            (next, old) => _document.Execute(new EditExclusionLayersCommand(index, next, old)),
            LocalizedText.Raw(
                "On (the default) masks scatter placement from EVERY scatter layer inside this exclusion's shape, " +
                "including any layer added later. Turn off to target only specific layers below, leaving the rest " +
                "free to scatter through this shape."),
            layerName => LocalizedText.Raw(
                $"On masks scatter placement from the '{layerName}' scatter layer inside this exclusion's " +
                "shape. Only shown while All layers is off, and only masks the layers checked here, every " +
                "other layer still scatters through this shape freely."));
    }

    // The shared layer-targeting row set behind both the exclusion ("All layers" masks all scatter) and the scatter
    // override ("All layers" retunes all scatter). An "All layers" BoolRow bound to Layers == null, plus one BoolRow
    // per document scatter layer while an explicit list is in effect. Checking All ON collapses the explicit list to
    // null. Checking it OFF materializes the FULL explicit layer list (every current layer named). The per-layer
    // rows are HIDDEN (not merely disabled: PropertyGrid rows have no live per-row enabled hook, only a build-time
    // row set) while All is on, reflowing into view the next chrome step once All goes off, via the same
    // SyncShapeInspector idiom that swaps disc/rect param rows on a shape-kind conversion. Manually re-checking every
    // layer does NOT auto-collapse back to null: only the All toggle itself produces null (an unchecked last layer
    // legally leaves an empty explicit list, "applies to nothing", per the model). `layersOf` reads the LIVE Layers
    // list (so a toggle re-reads it, never a stale capture) and `execute(next, old)` runs the caller's whole-value or
    // targeting command with the new list and the pre-edit one. Descriptions differ per caller (mask vs retune), so
    // they are threaded in rather than fixed here.
    void AddLayerTargetingRows(Func<List<string>?> layersOf, Action<List<string>?, List<string>?> execute,
        LocalizedText allDescription, Func<string, LocalizedText> layerDescription)
    {
        var layerNames = new List<string>();
        foreach (MapScatterLayer layer in _document.Doc.ScatterLayers) layerNames.Add(layer.Name);

        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("All layers"),
            () => layersOf() is null,
            v => execute(v ? null : new List<string>(layerNames), layersOf()),
            allDescription));

        if (layersOf() is null) return;   // All is on: no explicit list to show membership rows for
        foreach (string name in layerNames)
        {
            string layerName = name;   // capture per iteration for the closures
            _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(layerName),
                () => layersOf() is { } layers && layers.Contains(layerName),
                v =>
                {
                    if (layersOf() is not { } live) return;   // All went on elsewhere: ignore a stray toggle
                    var next = new List<string>(live);
                    if (v) { if (!next.Contains(layerName)) next.Add(layerName); }
                    else next.Remove(layerName);
                    execute(next, live);
                },
                layerDescription(layerName)));
        }
    }

    // The scatter-override inspector: an inline-rename Name row (index-keyed, like the exclusion's), a Visible
    // toggle, the editable shape surface (AddShapeRows, driving EditScatterOverrideShapeCommand), then the two
    // scatter tweaks (a DensityMultiplier scalar and a Kinds substitution list, both whole-value edits through
    // EditScatterOverrideValuesCommand), and finally the layer-targeting rows shared with the exclusion inspector.
    // The Kinds text is the same crude comma-separated "id:weight" convention the scatter-layer rule editor uses
    // (empty means null kinds, a density-only override), a deliberate v1 carve-out rather than a dedicated editor.
    void BuildScatterOverrideInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        if (index < 0 || index >= _document.Doc.ScatterOverrides.Count) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        AddIndexNameRow(index, i => ScatterOverrideAt(i)?.Name ?? "", ScatterOverrideNameExists,
            (i, newName, oldName) => new RenameScatterOverrideCommand(i, newName, oldName),
            LocalizedText.Raw(
                "Optional display name shown in the outline instead of the index. Leave empty to fall back to " +
                "the index label. Must not duplicate another scatter override's name."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        AddVisibleRow(SelectionKind.ScatterOverride, () => id);
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        AddShapeRows(() => index < _document.Doc.ScatterOverrides.Count ? _document.Doc.ScatterOverrides[index].Shape : null,
            (newShape, oldShape) => _document.Execute(new EditScatterOverrideShapeCommand(index, newShape, oldShape)));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Scatter")));
        AddFloatRow(LocalizedText.Raw("DensityMultiplier"),
            () => ScatterOverrideAt(index)?.DensityMultiplier ?? 1f,
            v => EditScatterOverrideValues(index, o => o.DensityMultiplier = v), min: 0f,
            description: LocalizedText.Raw(
                "Multiplier applied to every targeted scatter layer's placement density inside this override's " +
                "shape. 1 leaves density unchanged, 0 places nothing, above 1 packs it denser. Relative to each " +
                "layer's own density, not an absolute instance count."));
        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Kinds"),
            () => ScatterOverrideAt(index)?.Kinds is { } kinds ? FormatKinds(kinds) : "",
            v => { if (TryParseKinds(v, out List<MapPropKind> parsed)) EditScatterOverrideValues(index, o => o.Kinds = parsed.Count == 0 ? null : parsed); },
            maxLength: 256,
            description: LocalizedText.Raw(
                "Comma-separated kit ids that REPLACE what the targeted scatter layers would place inside this " +
                "override's shape, each optionally followed by :weight (for example 'oak:2, pine'). Leave empty to " +
                "keep each layer's own kinds and only retune density. A crude v1 text convention shared with the " +
                "scatter-layer rule editor's Kinds rows.")));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Layers")));
        AddScatterOverrideLayerRows(index);
        _inspectorLayersAllOn = ScatterOverrideAt(index)?.Layers is null;
        // Capture the live scatter-layer set the per-layer targeting rows were built from, so SyncShapeInspector
        // rebuilds them when a scatter layer is added / removed / renamed while this override stays selected (the
        // same stale-layer-set guard the exclusion / companion inspectors use).
        _inspectorScatterNames = LiveScatterNames();
    }

    MapScatterOverrideDoc? ScatterOverrideAt(int index)
    {
        List<MapScatterOverrideDoc> overrides = _document.Doc.ScatterOverrides;
        return index >= 0 && index < overrides.Count ? overrides[index] : null;
    }

    // Whether `name` (already caller-normalized non-empty) is already the live name of some OTHER scatter override,
    // mirroring RenameScatterOverrideCommand's own GuardNoScatterOverrideName guard so the Name row rejects a
    // collision before the command would throw.
    bool ScatterOverrideNameExists(string name, int exceptIndex)
    {
        List<MapScatterOverrideDoc> overrides = _document.Doc.ScatterOverrides;
        for (int i = 0; i < overrides.Count; i++)
            if (i != exceptIndex && string.Equals(overrides[i].Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // The override's layer-targeting rows, built through the shared AddLayerTargetingRows helper: an "All layers"
    // toggle bound to Layers == null (retunes scatter on every layer, including future ones, see
    // MapScatterOverrideDoc.Layers), plus one per-layer BoolRow while an explicit list is in effect. Every change
    // routes through EditScatterOverrideValuesCommand (the whole-value path), so it undoes / merges with the density
    // and kinds edits like any other override values edit.
    void AddScatterOverrideLayerRows(int index)
    {
        AddLayerTargetingRows(
            () => ScatterOverrideAt(index)?.Layers,
            (next, _) => EditScatterOverrideValues(index, o => o.Layers = next),
            LocalizedText.Raw(
                "On (the default) applies this override's density and kinds to EVERY scatter layer inside its " +
                "shape, including any layer added later. Turn off to retune only specific layers below, leaving the " +
                "rest to scatter unchanged through this shape."),
            layerName => LocalizedText.Raw(
                $"On applies this override's density and kinds to the '{layerName}' scatter layer inside its " +
                "shape. Only shown while All layers is off, and only retunes the layers checked here, every other " +
                "layer scatters unchanged through this shape."));
    }

    // A whole-value scatter-override edit for its density / kinds / layers (NOT its shape or name, which route
    // through EditScatterOverrideShapeCommand / RenameScatterOverrideCommand): deep-clone the live override, apply
    // `mutate` to the clone (so a nested Kinds / Layers change never touches the live instance the command captures
    // as its old value), then route (clone, live) through EditScatterOverrideValuesCommand (same-index merge
    // coalesces a scrub). No-op when the override has vanished.
    void EditScatterOverrideValues(int index, Action<MapScatterOverrideDoc> mutate)
    {
        if (ScatterOverrideAt(index) is not { } live) return;
        MapScatterOverrideDoc clone = CloneScatterOverride(live);
        mutate(clone);
        _document.Execute(new EditScatterOverrideValuesCommand(index, clone, live));
    }

    // Deep clones a scatter override (the whole-value-edit copy discipline, matching CloneScatterLayer): fresh Kinds
    // (fresh MapPropKind elements) and Layers lists so a clone edit never mutates the live instance the command keeps
    // as its old value. Shape is shared by reference, the values command carries it through untouched (a shape move
    // is EditScatterOverrideShapeCommand's job).
    static MapScatterOverrideDoc CloneScatterOverride(MapScatterOverrideDoc v)
    {
        List<MapPropKind>? kinds = null;
        if (v.Kinds is { } srcKinds)
        {
            kinds = new List<MapPropKind>(srcKinds.Count);
            foreach (MapPropKind k in srcKinds) kinds.Add(CloneKind(k));
        }
        return new MapScatterOverrideDoc
        {
            Name = v.Name,
            Shape = v.Shape,
            DensityMultiplier = v.DensityMultiplier,
            Kinds = kinds,
            Layers = v.Layers is null ? null : new List<string>(v.Layers),
        };
    }

    void BuildRegionInspector(string name)
    {
        if (RegionByName(name) is null) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.Region, name,
            v => RegionByName(v) is not null, (oldName, newName) => new RenameRegionCommand(oldName, newName),
            LocalizedText.Raw(
                "Unique name for this region, the key game code reads to find it (KhaozEngine.MapDoc.MapRegion " +
                "carries no other identity). Renaming updates the outline node and the current selection to " +
                "follow the new name. Must be non-empty and not collide with another region's name."));
        AddVisibleRow(SelectionKind.Region, cur);
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        AddShapeRows(() => RegionByName(cur())?.Shape,
            (newShape, oldShape) => _document.Execute(new EditRegionShapeCommand(cur(), newShape, oldShape)));
    }

    // ---- biome band inspector ------------------------------------------------------------------------------

    /// <summary>The biome-choice options a band's Biome selector offers: the <see cref="BiomeId"/> names in
    /// declaration order.</summary>
    static readonly string[] BiomeChoices = Enum.GetNames<BiomeId>();

    // The band inspector: the Biome choice (kind ChoiceRow, Rows[0]), the nullable Start / End edges, and the
    // BaseHeight / HillAmplitude scalars. Every edit is a WHOLE-VALUE edit routed through EditBiomeBandCommand
    // (clone the live band, change the one field, keep the live band as the command's old value), whose same-index
    // merge coalesces a scrub into one undo step. Bands have no name and no authored shape, so there is no name row
    // and no Visible row: a band is not an independently hideable element (visibility is per placed shape). The
    // selected band still draws its Start/End world-Z edge lines in the viewport (see ComputeOverlayDrawList), an
    // always-on aid rather than a toggled overlay element.
    //
    // Nullable edges (the smallest honest mechanism): each of Start / End is a FloatRow for the concrete value
    // PAIRED with an "<edge> open" BoolRow that toggles the open edge (null = +/- infinity). This mirrors the
    // exclusion "All layers" null-gate (decision 4): a BoolRow decides whether the nullable field is null, and
    // editing the FloatRow closes an open edge to that concrete value. Both rows are always present (no reflow),
    // and while the edge is open the FloatRow shows 0 as its placeholder (the toggle above it makes the open state
    // explicit).
    void BuildBiomeBandInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        if (BandAt(index) is null) return;

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Range")));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Affects"),
            () => "terrain shape + scatter",
            LocalizedText.Raw(
                "This band's Biome drives terrain shaping (BaseHeight and HillAmplitude) and the scatter rules keyed " +
                "by that biome, blended over its world-Z range. Ground tinting by biome is not wired yet.")));
        _inspector.Rows.Add(new ChoiceRow(LocalizedText.Raw("Biome"), BiomeChoices,
            () => (BandAt(index)?.Biome ?? BiomeId.Meadow).ToString(),
            v => { if (Enum.TryParse(v, out BiomeId biome)) EditBand(index, b => b.Biome = biome); },
            description: LocalizedText.Raw(
                "Which biome's height and scatter rules apply within this band's Start/End world-Z range.")));

        AddBandFloatRow(index, "Start",
            "Lower world-space Z position where this band begins applying (bands are a Z-axis slice, not a height " +
            "range). Ignored (treated as an open, unbounded edge) while Start open is on.",
            b => b.Start ?? 0f, (b, v) => b.Start = v);
        AddBandEdgeToggle(index, "Start open",
            "On leaves the lower Z edge open: this band applies below End with no lower Z limit. Off closes it to " +
            "the Start value above.",
            b => b.Start, (b, open) => b.Start = open ? null : (b.Start ?? 0f));
        AddBandFloatRow(index, "End",
            "Upper world-space Z position where this band stops applying (bands are a Z-axis slice, not a height " +
            "range). Ignored (treated as an open, unbounded edge) while End open is on.",
            b => b.End ?? 0f, (b, v) => b.End = v);
        AddBandEdgeToggle(index, "End open",
            "On leaves the upper Z edge open: this band applies above Start with no upper Z limit. Off closes it to " +
            "the End value above.",
            b => b.End, (b, open) => b.End = open ? null : (b.End ?? 0f));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        AddBandFloatRow(index, "BaseHeight",
            "Height, in world units, this biome's terrain settles toward within its range, before the gentle and " +
            "detail noise layers add their own variation on top.",
            b => b.BaseHeight, (b, v) => b.BaseHeight = v);
        AddBandFloatRow(index, "HillAmplitude",
            "Extra height swing, in world units, this biome adds on top of BaseHeight. Higher values make this " +
            "band's terrain hillier relative to the rest of the map.",
            b => b.HillAmplitude, (b, v) => b.HillAmplitude = v, min: 0f);
    }

    MapBiomeBand? BandAt(int index)
    {
        List<MapBiomeBand> bands = _document.Doc.Terrain.Biomes;
        return index >= 0 && index < bands.Count ? bands[index] : null;
    }

    static MapBiomeBand CloneBand(MapBiomeBand b) => new MapBiomeBand
    {
        Start = b.Start, End = b.End, Biome = b.Biome, BaseHeight = b.BaseHeight, HillAmplitude = b.HillAmplitude,
    };

    // A whole-value band edit: clone the live band, apply `mutate` to the clone, then route (clone, live) through
    // EditBiomeBandCommand (same-index merge coalesces a scrub). No-op when the band has vanished.
    void EditBand(int index, Action<MapBiomeBand> mutate)
    {
        if (BandAt(index) is not { } current) return;
        MapBiomeBand clone = CloneBand(current);
        mutate(clone);
        _document.Execute(new EditBiomeBandCommand(index, clone, current));
    }

    // One scrubbed scalar of the band at `index` (the AddFeatureRow idiom): get reads the LIVE band (every edit
    // replaces the instance), set clones and writes the one field through EditBand.
    void AddBandFloatRow(int index, string label, string description, Func<MapBiomeBand, float> get,
        Action<MapBiomeBand, float> assign, float min = float.MinValue)
    {
        AddFloatRow(LocalizedText.Raw(label),
            () => BandAt(index) is { } b ? get(b) : 0f,
            v => EditBand(index, b => assign(b, v)), min: min, description: LocalizedText.Raw(description));
    }

    // The "<edge> open" toggle for a nullable band edge: on == the edge is open (the field is null). `read` reads
    // the live nullable edge (so the toggle reflects the current null state), and `apply` sets the field per the
    // new open flag (true => null, false => a concrete value, closing the edge). Whole-value edit via EditBand.
    void AddBandEdgeToggle(int index, string label, string description, Func<MapBiomeBand, float?> read,
        Action<MapBiomeBand, bool> apply)
    {
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(label),
            () => BandAt(index) is { } b && read(b) is null,
            v => EditBand(index, b => apply(b, v)), LocalizedText.Raw(description)));
    }

    // ---- scatter + companion layer inspectors --------------------------------------------------------------

    // The scatter-layer inspector: an inline-rename Name row (scatter layers are name-keyed, so a rename re-points
    // the selection AND remaps the visibility key), the layer scalars, a nullable MaxHeight (the band open-edge
    // idiom), then the per-rule surface (v1-crude, decision 11): each rule shows a Biome choice, a Density scalar,
    // a "id:weight" Kinds text row, and a remove button, with a trailing add-rule button and a remove-layer button.
    // Every scalar / rule edit is a WHOLE-VALUE edit routed through EditScatterLayerCommand (deep-clone the live
    // layer, change the one field, keep the live layer as the command's old value), whose same-name merge coalesces
    // a scrub into one undo step. A rule add / remove seals its own gesture and reflows the rows through the
    // deferred `_inspectorRuleCount` sync (never a rebuild inside the grid's row iteration).
    void BuildScatterLayerInspector(string name)
    {
        if (ScatterLayerByName(name) is not { } layer) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.ScatterLayer, name,
            v => ScatterLayerByName(v) is not null,
            (oldName, newName) => { _visibility.RenameLayer(oldName, newName); return new RenameScatterLayerCommand(oldName, newName); },
            LocalizedText.Raw(
                "Unique name for this scatter layer, referenced by exclusion targeting and by any companion " +
                "layer's HostLayer. Renaming it cascades: exclusions and companions that reference the old name " +
                "retarget to the new one automatically. Must be non-empty and not collide with another layer's " +
                "name."));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Placement")));
        AddScatterFloatRow(cur, "Seed",
            "Random seed driving this layer's scatter placement pattern. Two layers with the same seed and " +
            "parameters place identical instances. Change it to get a different pattern at the same density.",
            l => l.Seed, (l, v) => l.Seed = (int)MathF.Round(v), dragScale: 1f, decimals: 0);
        AddScatterFloatRow(cur, "CellSize",
            "Size, in world units, of the jittered grid cells candidate positions are drawn from. Smaller cells " +
            "pack candidates closer together (denser scatter before rule density filters it down), larger cells " +
            "space them further apart.",
            l => l.CellSize, (l, v) => l.CellSize = v, min: 0.01f);
        AddScatterFloatRow(cur, "Jitter",
            "How far, from 0 (none) to 1 (a full cell), each candidate position is randomly offset from its grid " +
            "cell center. Higher values look more natural and less grid-aligned.",
            l => l.Jitter, (l, v) => l.Jitter = v, min: 0f);

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Scale")));
        AddScatterFloatRow(cur, "ScaleMin",
            "Minimum random uniform scale applied to each placed instance. Actual scale per instance is picked " +
            "uniformly between ScaleMin and ScaleMax.",
            l => l.ScaleMin, (l, v) => l.ScaleMin = v, min: 0f);
        AddScatterFloatRow(cur, "ScaleMax",
            "Maximum random uniform scale applied to each placed instance. Actual scale per instance is picked " +
            "uniformly between ScaleMin and ScaleMax.",
            l => l.ScaleMax, (l, v) => l.ScaleMax = v, min: 0f);
        AddScatterFloatRow(cur, "MaxHeight",
            "World-space height, in world units, above which this layer stops placing instances. Ignored while " +
            "MaxHeight unset is on.",
            l => l.MaxHeight ?? 0f, (l, v) => l.MaxHeight = v);
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("MaxHeight unset"),
            () => ScatterLayerByName(cur())?.MaxHeight is null,
            v => EditScatterLayer(cur(), l => l.MaxHeight = v ? null : (l.MaxHeight ?? 0f)),
            LocalizedText.Raw(
                "On removes the height ceiling entirely: this layer can place at any height. Off closes it to " +
                "the MaxHeight value above.")));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Rules")));
        for (int r = 0; r < layer.Rules.Count; r++)
        {
            int ri = r;   // capture per iteration for the closures
            _inspector.Rows.Add(new ChoiceRow(LocalizedText.Raw($"Rule {ri} biome"), BiomeChoices,
                () => (RuleAt(cur(), ri)?.Biome ?? BiomeId.Meadow).ToString(),
                v => { if (Enum.TryParse(v, out BiomeId biome)) EditScatterLayer(cur(), l => { if (ri < l.Rules.Count) l.Rules[ri].Biome = biome; }); },
                description: LocalizedText.Raw(
                    "Biome this rule applies within. A candidate position only uses this rule's density and " +
                    "kinds where the terrain's live biome at that point matches.")));
            AddFloatRow(LocalizedText.Raw($"Rule {ri} density"),
                () => RuleAt(cur(), ri)?.Density ?? 0f,
                v => EditScatterLayer(cur(), l => { if (ri < l.Rules.Count) l.Rules[ri].Density = v; }), min: 0f,
                description: LocalizedText.Raw(
                    "Chance, from 0 (never) to 1 (always), a candidate position in this rule's biome becomes an " +
                    "instance from its Kinds below."));
            _inspector.Rows.Add(new TextRow(LocalizedText.Raw($"Rule {ri} kinds"),
                () => RuleAt(cur(), ri) is { } rule ? FormatKinds(rule.Kinds) : "",
                v => { if (TryParseKinds(v, out List<MapPropKind> kinds)) EditScatterLayer(cur(), l => { if (ri < l.Rules.Count) l.Rules[ri].Kinds = kinds; }); },
                maxLength: 256,
                description: LocalizedText.Raw(
                    "Comma-separated kit ids this rule places, each optionally followed by :weight (for example " +
                    "'oak:2, pine' places oak twice as often as pine, a bare id defaults to weight 1). Weights " +
                    "are relative to each other within this rule, not a percentage of any total.")));
            AddActionRow($"[- remove rule {ri}]",
                "Removes this rule from the layer. Undoable as its own step.",
                () => { EditScatterLayer(cur(), l => { if (ri < l.Rules.Count) l.Rules.RemoveAt(ri); }); _document.SealGesture(); });
        }
        AddActionRow("[+ add rule]",
            "Adds a new, blank rule to this layer (Meadow biome, zero density, no kinds) for you to configure.",
            () => { EditScatterLayer(cur(), l => l.Rules.Add(new MapBiomeScatterRule())); _document.SealGesture(); });
        AddActionRow("[- remove layer]",
            "Removes this entire scatter layer from the document. Rejected if any companion layer still hosts " +
            "on it, retarget or remove those companions first.",
            () => RemoveScatterLayerFromInspector(cur()));

        _inspectorRuleCount = layer.Rules.Count;   // reflow the per-rule rows when a rule is added / removed
    }

    // The companion-layer inspector: an inline-rename Name row, a HostLayer chooser (the live scatter-layer names,
    // so an invalid host cannot be picked through the GUI), the count / radius / scale scalars, HostKinds (plain id
    // list) and Kinds ("id:weight" list) text rows, and a nullable MaxHeight, then a remove button. Every edit is a
    // WHOLE-VALUE edit through EditCompanionLayerCommand (deep clone). The HostLayer chooser depends on the live
    // scatter-layer set, so `_inspectorScatterNames` is captured for the deferred refresh when a scatter layer is
    // added / removed / renamed while this companion stays selected.
    void BuildCompanionLayerInspector(string name)
    {
        if (CompanionLayerByName(name) is not { } companion) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur = AddNameRow(SelectionKind.CompanionLayer, name,
            v => CompanionLayerByName(v) is not null,
            (oldName, newName) => new RenameCompanionLayerCommand(oldName, newName),
            LocalizedText.Raw(
                "Unique name for this companion layer. Must be non-empty and not collide with another " +
                "companion's name."));

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Host")));
        // Always offer at least the current host as an option (even if it is somehow not among the live scatter
        // layers), so the dropdown never silently drops an out-of-set value. Fall back to a read-only row only when
        // there is nothing at all to choose (no scatter layers and an empty host).
        List<string> hostOptions = LiveScatterNames();
        string liveHost = companion.HostLayer;
        if (liveHost.Length > 0 && !hostOptions.Contains(liveHost)) hostOptions.Add(liveHost);
        if (hostOptions.Count > 0)
            _inspector.Rows.Add(new ChoiceRow(LocalizedText.Raw("HostLayer"), hostOptions.ToArray(),
                () => CompanionLayerByName(cur())?.HostLayer ?? "",
                v => SetCompanionHostLayer(cur(), v),
                description: LocalizedText.Raw(
                    "Which scatter layer's placed instances this companion layer rings. Companions spawn near " +
                    "each of the host layer's placed props (its 'hosts'), so changing this changes which props " +
                    "they cluster around. If HostKinds below no longer matches anything in the new host, it is " +
                    "cleared to match all in the same edit.")));
        else
            _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("HostLayer"),
                () => (CompanionLayerByName(cur())?.HostLayer ?? "") is { Length: > 0 } h ? h : "(no scatter layers)",
                description: LocalizedText.Raw(
                    "Which scatter layer this companion layer rings. No scatter layers exist yet to choose from, " +
                    "add one first.")));

        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("HostKinds"),
            () => CompanionLayerByName(cur()) is { } l ? FormatIds(l.HostKinds) : "",
            v => EditCompanionLayer(cur(), l => l.HostKinds = ParseIds(v)), maxLength: 256,
            description: LocalizedText.Raw(
                "Comma-separated kit ids to match against the host layer's placed instances (plain ids, no " +
                "weights). A host only gets companions when its kit id appears here. Empty (the default) means " +
                "match EVERY kind the host layer can place, not none.")));

        // A populated HostKinds that matches NONE of the host layer's rule kinds spawns no companions (a silent
        // no-op), so surface it with a warning-styled read-only row right under the HostKinds row. Empty HostKinds
        // means match-all (no warning). The mismatch is captured so SyncShapeInspector reflows the row live.
        bool mismatch = companion.HostKinds.Count > 0 && !HostKindsIntersect(companion.HostKinds, companion.HostLayer);
        _inspectorCompanionMismatch = mismatch;
        if (mismatch)
            _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Warning"),
                () => "HostKinds match no kind in the host layer",
                description: LocalizedText.Raw(
                    "HostKinds names no kit id the host layer can actually place, so this companion currently " +
                    "spawns nothing. Either clear HostKinds to match every host, or list an id the host layer's " +
                    "rules actually place.")) { TextColor = GuiTheme.Default.DangerBright });

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Output")));
        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Kinds"),
            () => CompanionLayerByName(cur()) is { } l ? FormatKinds(l.Kinds) : "",
            v => { if (TryParseKinds(v, out List<MapPropKind> kinds)) EditCompanionLayer(cur(), l => l.Kinds = kinds); }, maxLength: 256,
            description: LocalizedText.Raw(
                "Comma-separated kit ids this companion layer places around each matched host, each optionally " +
                "followed by :weight (for example 'bush:2, fern' places bush twice as often as fern, a bare id " +
                "defaults to weight 1).")));
        AddCompanionFloatRow(cur, "CountMin",
            "Minimum number of companion instances placed around each matched host. Actual count per host is " +
            "picked uniformly between CountMin and CountMax.",
            l => l.CountMin, (l, v) => l.CountMin = (int)MathF.Round(v), min: 0f, dragScale: 1f, decimals: 0);
        AddCompanionFloatRow(cur, "CountMax",
            "Maximum number of companion instances placed around each matched host. Actual count per host is " +
            "picked uniformly between CountMin and CountMax.",
            l => l.CountMax, (l, v) => l.CountMax = (int)MathF.Round(v), min: 0f, dragScale: 1f, decimals: 0);

        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Shape")));
        AddCompanionFloatRow(cur, "Seed",
            "Random seed driving this companion layer's placement pattern around its hosts. Two layers with the " +
            "same seed and parameters place identical companions.",
            l => l.Seed, (l, v) => l.Seed = (int)MathF.Round(v), dragScale: 1f, decimals: 0);
        AddCompanionFloatRow(cur, "RadiusMin",
            "Minimum distance, in world units, a companion instance can land from its host. Actual distance per " +
            "instance is picked uniformly between RadiusMin and RadiusMax.",
            l => l.RadiusMin, (l, v) => l.RadiusMin = v, min: 0f);
        AddCompanionFloatRow(cur, "RadiusMax",
            "Maximum distance, in world units, a companion instance can land from its host. Actual distance per " +
            "instance is picked uniformly between RadiusMin and RadiusMax.",
            l => l.RadiusMax, (l, v) => l.RadiusMax = v, min: 0f);
        AddCompanionFloatRow(cur, "ScaleMin",
            "Minimum random uniform scale applied to each placed companion instance. Actual scale per instance " +
            "is picked uniformly between ScaleMin and ScaleMax.",
            l => l.ScaleMin, (l, v) => l.ScaleMin = v, min: 0f);
        AddCompanionFloatRow(cur, "ScaleMax",
            "Maximum random uniform scale applied to each placed companion instance. Actual scale per instance " +
            "is picked uniformly between ScaleMin and ScaleMax.",
            l => l.ScaleMax, (l, v) => l.ScaleMax = v, min: 0f);
        AddCompanionFloatRow(cur, "MaxHeight",
            "World-space height, in world units, above which this companion layer stops placing instances. " +
            "Ignored while MaxHeight unset is on.",
            l => l.MaxHeight ?? 0f, (l, v) => l.MaxHeight = v);
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("MaxHeight unset"),
            () => CompanionLayerByName(cur())?.MaxHeight is null,
            v => EditCompanionLayer(cur(), l => l.MaxHeight = v ? null : (l.MaxHeight ?? 0f)),
            LocalizedText.Raw(
                "On removes the height ceiling entirely: this companion layer can place at any height. Off " +
                "closes it to the MaxHeight value above.")));
        AddActionRow("[- remove companion]",
            "Removes this companion layer from the document. Undoable as its own step.",
            () => RemoveCompanionLayerFromInspector(cur()));

        _inspectorScatterNames = LiveScatterNames();   // the HostLayer chooser enumerates scatter names: refresh on a change
    }

    // One scrubbed scalar of the scatter layer named by `name()` (the AddBandFloatRow idiom): get reads the LIVE
    // layer, set clones deeply and writes the one field through EditScatterLayer.
    void AddScatterFloatRow(Func<string> name, string label, string description, Func<MapScatterLayer, float> get,
        Action<MapScatterLayer, float> assign, float min = float.MinValue, float dragScale = 0.01f, int decimals = 2)
    {
        AddFloatRow(LocalizedText.Raw(label),
            () => ScatterLayerByName(name()) is { } l ? get(l) : 0f,
            v => EditScatterLayer(name(), l => assign(l, v)), min: min, dragScale: dragScale, decimals: decimals,
            description: LocalizedText.Raw(description));
    }

    void AddCompanionFloatRow(Func<string> name, string label, string description, Func<MapCompanionLayer, float> get,
        Action<MapCompanionLayer, float> assign, float min = float.MinValue, float dragScale = 0.01f, int decimals = 2)
    {
        AddFloatRow(LocalizedText.Raw(label),
            () => CompanionLayerByName(name()) is { } l ? get(l) : 0f,
            v => EditCompanionLayer(name(), l => assign(l, v)), min: min, dragScale: dragScale, decimals: decimals,
            description: LocalizedText.Raw(description));
    }

    // A "button" row: no PropertyGrid button widget exists, so a BoolRow whose value is always read as off doubles
    // as one. Tapping flips it on, which runs `action` once. The getter re-reads off next frame (and a deferred
    // rebuild recreates the row), so it never stays pressed. Used for the crude rule / layer add-remove affordances.
    void AddActionRow(string label, string description, Action action)
    {
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw(label), () => false, v => { if (v) action(); },
            LocalizedText.Raw(description)));
    }

    // Removes the scatter layer from its inspector's remove button, surfacing a referenced-removal rejection in the
    // status strip (the command throws before mutating, so the document is untouched). The vanished-selection sync
    // clears the now-dangling name-keyed selection at the next chrome step, safely outside the grid iteration.
    void RemoveScatterLayerFromInspector(string name)
    {
        try
        {
            _document.Execute(new RemoveScatterLayerCommand(name));
            _document.SealGesture();
        }
        catch (InvalidOperationException ex)
        {
            _statusText = ex.Message;   // referenced-removal rejection: surface it, leave the document unchanged
        }
    }

    void RemoveCompanionLayerFromInspector(string name)
    {
        if (CompanionLayerByName(name) is null) return;
        _document.Execute(new RemoveCompanionLayerCommand(name));
        _document.SealGesture();
    }

    // A whole-value scatter-layer edit: deep-clone the live layer, apply `mutate` to the clone (so a nested Rules /
    // Kinds change never touches the live instance the command keeps as its old value), then route (clone, live)
    // through EditScatterLayerCommand (same-name merge coalesces a scrub). No-op when the layer has vanished.
    void EditScatterLayer(string name, Action<MapScatterLayer> mutate)
    {
        if (ScatterLayerByName(name) is not { } live) return;
        MapScatterLayer clone = CloneScatterLayer(live);
        mutate(clone);
        _document.Execute(new EditScatterLayerCommand(name, clone, live));
    }

    void EditCompanionLayer(string name, Action<MapCompanionLayer> mutate)
    {
        if (CompanionLayerByName(name) is not { } live) return;
        MapCompanionLayer clone = CloneCompanionLayer(live);
        mutate(clone);
        _document.Execute(new EditCompanionLayerCommand(name, clone, live));
    }

    // The HostLayer chooser's setter: swap the host in ONE whole-value edit that also CLEARS HostKinds when the
    // populated kinds match nothing in the new host layer (zero intersection), so a host swap never silently leaves
    // a companion that spawns nothing. Because it is a single EditCompanionLayerCommand carrying both fields, one
    // undo restores BOTH the old host and the old HostKinds. Empty HostKinds already means match-all, so it is left
    // untouched (clearing it would be a no-op). A clear is noted in the status strip. No-op when the layer vanished.
    internal void SetCompanionHostLayer(string name, string newHost)
    {
        if (CompanionLayerByName(name) is not { } live) return;
        MapCompanionLayer clone = CloneCompanionLayer(live);
        clone.HostLayer = newHost;
        if (clone.HostKinds.Count > 0 && !HostKindsIntersect(clone.HostKinds, newHost))
        {
            clone.HostKinds = new List<string>();
            _statusText = "host kinds cleared to match all hosts";
        }
        _document.Execute(new EditCompanionLayerCommand(name, clone, live));
    }

    // The set of kit ids a scatter layer's rules can place (every Rules[].Kinds[].Id), i.e. the "host kinds" a
    // companion's HostKinds filters against. An unknown / absent host layer contributes no ids (an empty set).
    HashSet<string> HostLayerKindIds(string hostLayer)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (ScatterLayerByName(hostLayer) is { } layer)
            foreach (MapBiomeScatterRule rule in layer.Rules)
                foreach (MapPropKind kind in rule.Kinds)
                    ids.Add(kind.Id);
        return ids;
    }

    // True when at least one entry of `hostKinds` names a kit id the host layer can actually place (ordinal
    // compare), so a populated HostKinds still matches SOMETHING. Empty hostKinds is not passed here (empty means
    // match-all, the callers guard it before calling).
    bool HostKindsIntersect(IReadOnlyList<string> hostKinds, string hostLayer)
    {
        HashSet<string> hostIds = HostLayerKindIds(hostLayer);
        foreach (string kind in hostKinds)
            if (hostIds.Contains(kind)) return true;
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

    static MapBiomeScatterRule? RuleAt(MapScatterLayer? layer, int index) =>
        layer is not null && index >= 0 && index < layer.Rules.Count ? layer.Rules[index] : null;

    MapBiomeScatterRule? RuleAt(string name, int index) => RuleAt(ScatterLayerByName(name), index);

    // The live scatter / companion layer names in document order, snapshotted into a fresh list (used both for the
    // deferred staleness compare and for unique-name generation).
    List<string> LiveScatterNames()
    {
        var names = new List<string>(_document.Doc.ScatterLayers.Count);
        foreach (MapScatterLayer l in _document.Doc.ScatterLayers) names.Add(l.Name);
        return names;
    }

    List<string> LiveCompanionNames()
    {
        var names = new List<string>(_document.Doc.CompanionLayers.Count);
        foreach (MapCompanionLayer l in _document.Doc.CompanionLayers) names.Add(l.Name);
        return names;
    }

    // Deep clones (the whole-value-edit copy discipline): a scatter / companion layer is a mutable class with
    // nested lists (Rules -> Kinds, HostKinds, Kinds), so a shallow copy would share those lists and let a scrub of
    // the clone mutate the live instance the command captured as its old value. Every nested list is rebuilt.
    // CloneScatterLayer / CloneCompanionLayer are internal (not private) so EditorToolController.DuplicateSelection
    // (EditorTool.cs, same assembly) reuses this exact deep-clone rather than a second copy of the nested-list logic.
    static MapPropKind CloneKind(MapPropKind k) => new MapPropKind { Id = k.Id, Weight = k.Weight };

    static MapBiomeScatterRule CloneRule(MapBiomeScatterRule r)
    {
        var kinds = new List<MapPropKind>(r.Kinds.Count);
        foreach (MapPropKind k in r.Kinds) kinds.Add(CloneKind(k));
        return new MapBiomeScatterRule { Biome = r.Biome, Density = r.Density, Kinds = kinds };
    }

    internal static MapScatterLayer CloneScatterLayer(MapScatterLayer l)
    {
        var rules = new List<MapBiomeScatterRule>(l.Rules.Count);
        foreach (MapBiomeScatterRule r in l.Rules) rules.Add(CloneRule(r));
        return new MapScatterLayer
        {
            Name = l.Name, Seed = l.Seed, CellSize = l.CellSize, Jitter = l.Jitter, MaxHeight = l.MaxHeight,
            ScaleMin = l.ScaleMin, ScaleMax = l.ScaleMax, Rules = rules,
        };
    }

    internal static MapCompanionLayer CloneCompanionLayer(MapCompanionLayer l)
    {
        var kinds = new List<MapPropKind>(l.Kinds.Count);
        foreach (MapPropKind k in l.Kinds) kinds.Add(CloneKind(k));
        return new MapCompanionLayer
        {
            Name = l.Name, HostLayer = l.HostLayer, Seed = l.Seed, HostKinds = new List<string>(l.HostKinds),
            Kinds = kinds, CountMin = l.CountMin, CountMax = l.CountMax, RadiusMin = l.RadiusMin,
            RadiusMax = l.RadiusMax, ScaleMin = l.ScaleMin, ScaleMax = l.ScaleMax, MaxHeight = l.MaxHeight,
        };
    }

    // Formats a scatter kind list as the comma-separated "id" / "id:weight" text the Kinds rows edit: a unit weight
    // renders as the bare id, any other weight as "id:weight" (invariant culture). Round-trips through TryParseKinds.
    static string FormatKinds(IReadOnlyList<MapPropKind> kinds)
    {
        var parts = new List<string>(kinds.Count);
        foreach (MapPropKind k in kinds)
            parts.Add(k.Weight == 1f ? k.Id : string.Create(CultureInfo.InvariantCulture, $"{k.Id}:{k.Weight}"));
        return string.Join(", ", parts);
    }

    // Parses the comma-separated "id" / "id:weight" Kinds text exactly as the ke-mapedit MutationService.ParseKinds
    // does (split on the LAST colon, id before, weight after with the invariant culture, default weight 1). Returns
    // false on any garbage entry (an empty id, or a non-numeric weight) WITHOUT mutating, so the caller keeps the
    // old value and executes no command. Empty / whitespace-only segments are tolerated (skipped), so a trailing
    // comma while typing is not garbage. An entirely empty string parses to an empty kind list (legal per the model).
    static bool TryParseKinds(string text, out List<MapPropKind> kinds)
    {
        kinds = new List<MapPropKind>();
        foreach (string raw in text.Split(','))
        {
            string entry = raw.Trim();
            if (entry.Length == 0) continue;
            int colon = entry.LastIndexOf(':');
            string id = (colon < 0 ? entry : entry[..colon]).Trim();
            if (id.Length == 0) return false;
            float weight = 1f;
            if (colon >= 0)
            {
                string weightText = entry[(colon + 1)..].Trim();
                if (!float.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)) return false;
            }
            kinds.Add(new MapPropKind { Id = id, Weight = weight });
        }
        return true;
    }

    // Formats / parses a plain comma-separated id list (companion HostKinds, which carry no weights). Parsing never
    // fails (any non-empty trimmed segment is a valid id), so the HostKinds row always commits.
    static string FormatIds(IReadOnlyList<string> ids) => string.Join(", ", ids);

    static List<string> ParseIds(string text)
    {
        var result = new List<string>();
        foreach (string raw in text.Split(','))
        {
            string entry = raw.Trim();
            if (entry.Length > 0) result.Add(entry);
        }
        return result;
    }

    /// <summary>The shape-kind options the inspector's kind selector offers. Polygon is read-only v1 (no
    /// conversion in or out), so it is not an option: a polygon shape shows a read-only kind row instead.</summary>
    static readonly string[] ShapeKindChoices = { "disc", "rect" };

    // The editable shape surface of the selected region / exclusion: a kind ChoiceRow (disc <-> rect, converted
    // center-preservingly) plus one FloatRow per parameter, each writing a clone of the live DTO with the one
    // field changed through `execute` (newShape, oldShape), so a scrub coalesces via the command's merge.
    // Polygon gets a read-only kind + point count v1; a null / unknown shape keeps the read-only kind row.
    // `shape` reads the LIVE DTO (every edit replaces the instance), so the rows track edits and undo.
    void AddShapeRows(Func<MapShapeDoc?> shape, Action<MapShapeDoc, MapShapeDoc> execute)
    {
        MapShapeDoc? current = shape();
        switch (current)
        {
            case DiscShapeDoc:
                AddShapeKindRow(shape, execute);
                AddShapeRow<DiscShapeDoc>(shape, execute, "CenterX",
                    "World-space X coordinate of the disc's center, in world units.", s => s.CenterX, (s, v) => s.CenterX = v);
                AddShapeRow<DiscShapeDoc>(shape, execute, "CenterZ",
                    "World-space Z coordinate of the disc's center, in world units.", s => s.CenterZ, (s, v) => s.CenterZ = v);
                AddShapeRow<DiscShapeDoc>(shape, execute, "Radius",
                    "Radius of the disc, in world units, measured from CenterX/CenterZ.", s => s.Radius, (s, v) => s.Radius = v);
                break;
            case RectShapeDoc:
                AddShapeKindRow(shape, execute);
                AddShapeRow<RectShapeDoc>(shape, execute, "MinX",
                    "World-space X coordinate of the rect's minimum (near) corner, in world units.", s => s.MinX, (s, v) => s.MinX = v);
                AddShapeRow<RectShapeDoc>(shape, execute, "MinZ",
                    "World-space Z coordinate of the rect's minimum (near) corner, in world units.", s => s.MinZ, (s, v) => s.MinZ = v);
                AddShapeRow<RectShapeDoc>(shape, execute, "MaxX",
                    "World-space X coordinate of the rect's maximum (far) corner, in world units.", s => s.MaxX, (s, v) => s.MaxX = v);
                AddShapeRow<RectShapeDoc>(shape, execute, "MaxZ",
                    "World-space Z coordinate of the rect's maximum (far) corner, in world units.", s => s.MaxZ, (s, v) => s.MaxZ = v);
                break;
            case PolygonShapeDoc:
                _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Kind"), () => ShapeKind(shape()),
                    description: LocalizedText.Raw(
                        "This element's shape kind. A polygon is read-only in this editor version, it cannot be " +
                        "edited or converted to disc/rect here.")));
                _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Points"),
                    () => ((shape() as PolygonShapeDoc)?.Points.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                    description: LocalizedText.Raw("Number of vertices in this read-only polygon boundary.")));
                break;
            default:
                _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Kind"), () => ShapeKind(shape()),
                    description: LocalizedText.Raw("This element's shape kind. No shape is currently set.")));
                break;
        }
        _inspectorShapeKind = ShapeKind(current);
    }

    // The shape-kind selector: disc <-> rect, converted center-preservingly (see ConvertShape). ChoiceRow fires
    // the setter only on a real change, so re-picking the current kind never lands a command. Labeled "Kind"
    // rather than "Shape" so it does not repeat the "Shape" group HeaderRow it sits directly under.
    void AddShapeKindRow(Func<MapShapeDoc?> shape, Action<MapShapeDoc, MapShapeDoc> execute)
    {
        _inspector.Rows.Add(new ChoiceRow(LocalizedText.Raw("Kind"), ShapeKindChoices,
            () => ShapeKind(shape()),
            v =>
            {
                if (shape() is not { } current || ConvertShape(current, v) is not { } converted) return;
                execute(converted, current);
            },
            description: LocalizedText.Raw(
                "Which primitive defines this element's ground-plane footprint. Switching between disc and rect " +
                "converts the current shape center-preservingly (the new shape covers roughly the same area " +
                "around the same center) instead of resetting it.")));
    }

    // One scrubbed parameter of the selected shape (the AddFeatureRow idiom): get reads the LIVE DTO, set clones
    // the current DTO with the one property changed and routes the (new, old) pair through `execute`, whose
    // command's same-key merge makes a scrub coalesce into one undo step.
    void AddShapeRow<T>(Func<MapShapeDoc?> shape, Action<MapShapeDoc, MapShapeDoc> execute, string label,
        string description, Func<T, float> get, Action<T, float> assign) where T : MapShapeDoc
    {
        AddFloatRow(LocalizedText.Raw(label),
            () => shape() is T s ? get(s) : 0f,
            v =>
            {
                if (shape() is not T current) return;
                var clone = (T)CloneShape(current);
                assign(clone, v);
                execute(clone, current);
            }, description: LocalizedText.Raw(description));
    }

    // Copies a disc / rect shape DTO so an edit replaces the instance (the shape commands hold old + new by
    // reference). Polygon rows are read-only v1, so only the two editable kinds are cloned.
    static MapShapeDoc CloneShape(MapShapeDoc shape) => shape switch
    {
        DiscShapeDoc d => new DiscShapeDoc { CenterX = d.CenterX, CenterZ = d.CenterZ, Radius = d.Radius },
        RectShapeDoc r => new RectShapeDoc { MinX = r.MinX, MinZ = r.MinZ, MaxX = r.MaxX, MaxZ = r.MaxZ },
        _ => throw new InvalidOperationException($"No clone support for shape type '{shape.GetType().Name}'."),
    };

    // Converts a disc / rect to the requested kind, preserving the center: disc -> the square of side 2r around
    // its center; rect -> the disc at the rect's center with half the max extent as the radius. Returns null for
    // a same-kind request or an unconvertible shape (polygon, read-only v1).
    static MapShapeDoc? ConvertShape(MapShapeDoc current, string kind) => (current, kind) switch
    {
        (DiscShapeDoc d, "rect") => new RectShapeDoc
        {
            MinX = d.CenterX - d.Radius, MinZ = d.CenterZ - d.Radius,
            MaxX = d.CenterX + d.Radius, MaxZ = d.CenterZ + d.Radius,
        },
        (RectShapeDoc r, "disc") => new DiscShapeDoc
        {
            CenterX = (r.MinX + r.MaxX) * 0.5f,
            CenterZ = (r.MinZ + r.MaxZ) * 0.5f,
            Radius = MathF.Max(r.MaxX - r.MinX, r.MaxZ - r.MinZ) * 0.5f,
        },
        _ => null,
    };

    /// <summary>Rebuilds the inspector when its structural row set no longer matches what the live selection
    /// needs: the selected shape's kind no longer matches the kind the current rows were built for (a
    /// kind-ChoiceRow conversion, or an undo / redo of one, so disc rows swap to rect rows and back), OR the
    /// selected exclusion's / scatter override's "All layers" state no longer matches what the current layer rows
    /// were built for (an All-toggle, or an undo / redo of one, so the per-layer membership rows reflow into or out
    /// of view). Deferred to the chrome step so the rebuild never happens inside the grid's row iteration. No-op
    /// while the inspector holds neither shape nor layer-targeting rows. Internal so a headless test can fire the
    /// sync directly.</summary>
    internal void SyncShapeInspector()
    {
        // A name-keyed layer selection whose layer was removed (its inspector remove button, or an undo) is now
        // dangling: clear it here (outside the grid's row iteration), which rebuilds the inspector to the fallback.
        // Skip while the Name row is still focused, the same gate the pending-reselect fires on above. TextRow's
        // setter fires per keystroke and renames the document layer immediately, but only queues a deferred
        // reselect that lands once the row loses focus, so the selection id still holds the OLD name for the rest
        // of a mid-rename frame. Without this gate the first keystroke of an inline rename would see the old name
        // resolve to nothing and clear the selection, tearing down the very row the user is typing into. A real
        // removal still clears here on the same frame, because the remove button's own tap unfocuses the row first
        // (TextInput.Update unfocuses on a tap outside its bounds, and the grid visits the Name row before the
        // remove-button row), so the guard cannot skip a legitimate clear forever.
        EditorSelection sel = _document.Selection;
        bool nameRowFocused = _nameRow is not null && _nameRow.Input.IsFocused;
        if (sel.Kind == SelectionKind.ScatterLayer && ScatterLayerByName(sel.Id) is null && !nameRowFocused) { _document.Selection.Clear(); return; }
        if (sel.Kind == SelectionKind.CompanionLayer && CompanionLayerByName(sel.Id) is null && !nameRowFocused) { _document.Selection.Clear(); return; }

        if (_inspectorShapeKind is string builtShape &&
            !string.Equals(ShapeKind(SelectedShape()), builtShape, StringComparison.Ordinal))
        {
            RebuildInspector();
            return;
        }
        if (_inspectorLayersAllOn is bool builtAllOn && SelectedLayersAllOn() is bool liveAllOn &&
            liveAllOn != builtAllOn)
        {
            RebuildInspector();
            return;
        }
        // The scatter-layer name set the current inspector's rows depend on (an exclusion's targeting rows, a
        // companion's HostLayer chooser) changed under it: rebuild so the rows never show a stale layer set (add,
        // remove, or rename of a scatter layer while an exclusion / companion stays selected).
        if (_inspectorScatterNames is { } builtNames && !ScatterNamesUnchanged(builtNames))
        {
            RebuildInspector();
            return;
        }
        // A rule was added to / removed from the selected scatter layer (the crude rule buttons), changing the row
        // count without changing the selection: rebuild to reflow the per-rule rows.
        if (_inspectorRuleCount is int builtRules && SelectedScatterRuleCount() is int liveRules && liveRules != builtRules)
        {
            RebuildInspector();
            return;
        }
        // The selected companion's host-kinds mismatch flipped (a HostKinds edit, a host swap, or an undo / redo of
        // either) without a selection change: rebuild so the warning row appears / disappears with the mismatch.
        if (_inspectorCompanionMismatch is bool builtMismatch && SelectedCompanionMismatch() is bool liveMismatch
            && liveMismatch != builtMismatch)
        {
            RebuildInspector();
        }
    }

    // Whether the selected companion layer's populated HostKinds match NONE of its host layer's rule kinds (the
    // mismatch the warning row surfaces), or null when the selection is not a live companion. Empty HostKinds
    // returns false (empty means match-all, so no warning). Compared against `_inspectorCompanionMismatch`.
    bool? SelectedCompanionMismatch()
    {
        EditorSelection sel = _document.Selection;
        if (sel.Kind != SelectionKind.CompanionLayer) return null;
        if (CompanionLayerByName(sel.Id) is not { } companion) return null;
        if (companion.HostKinds.Count == 0) return false;
        return !HostKindsIntersect(companion.HostKinds, companion.HostLayer);
    }

    // Whether the live scatter-layer names still equal the snapshot the current inspector was built from (same
    // count, same names in order). A difference means a scatter layer was added, removed, or renamed.
    bool ScatterNamesUnchanged(List<string> built)
    {
        List<MapScatterLayer> live = _document.Doc.ScatterLayers;
        if (built.Count != live.Count) return false;
        for (int i = 0; i < live.Count; i++)
            if (!string.Equals(built[i], live[i].Name, StringComparison.Ordinal)) return false;
        return true;
    }

    // The selected scatter layer's Rules count, or null when the selection is not a live scatter layer. Compared
    // against `_inspectorRuleCount` so a rule add / remove reflows the per-rule rows.
    int? SelectedScatterRuleCount()
    {
        EditorSelection sel = _document.Selection;
        if (sel.Kind != SelectionKind.ScatterLayer) return null;
        return ScatterLayerByName(sel.Id)?.Rules.Count;
    }

    // The shape of the selected exclusion / scatter override / region, or null (no shape-carrying selection,
    // vanished element). Feeds SyncShapeInspector's shape-kind-conversion reflow, so every shape-carrying kind the
    // inspector edits must appear here.
    MapShapeDoc? SelectedShape()
    {
        EditorSelection sel = _document.Selection;
        return sel.Kind switch
        {
            SelectionKind.Exclusion when int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int i) && i >= 0 && i < _document.Doc.Exclusions.Count => _document.Doc.Exclusions[i].Shape,
            SelectionKind.ScatterOverride when int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int i) && i >= 0 && i < _document.Doc.ScatterOverrides.Count => _document.Doc.ScatterOverrides[i].Shape,
            SelectionKind.Region => RegionByName(sel.Id)?.Shape,
            _ => null,
        };
    }

    // Whether the selected exclusion's or scatter override's Layers is currently null ("All layers" on), or null
    // when the selection is neither (or has vanished). Both carry the same nullable Layers filter and the same
    // All-toggle reflow, so one probe covers both. Compared against `_inspectorLayersAllOn` by SyncShapeInspector.
    bool? SelectedLayersAllOn()
    {
        EditorSelection sel = _document.Selection;
        if (!int.TryParse(sel.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return null;
        return sel.Kind switch
        {
            SelectionKind.Exclusion => ExclusionAt(i)?.Layers is null,
            SelectionKind.ScatterOverride => ScatterOverrideAt(i)?.Layers is null,
            _ => null,
        };
    }

    static string ShapeKind(MapShapeDoc? shape) => shape switch
    {
        DiscShapeDoc => "disc",
        RectShapeDoc => "rect",
        PolygonShapeDoc => "polygon",
        null => "(none)",
        _ => shape.GetType().Name,
    };

    MapRegion? RegionByName(string name)
    {
        foreach (MapRegion r in _document.Doc.Regions)
            if (string.Equals(r.Name, name, StringComparison.Ordinal)) return r;
        return null;
    }

    void MovePlacement(string id, float? x = null, float? z = null)
    {
        if (Placement(id) is not { } p) return;
        _document.Execute(new MovePlacementCommand(id, x ?? p.X, z ?? p.Z, p.Y));
    }

    void MoveSpawn(string id, float? x = null, float? z = null)
    {
        if (Spawn(id) is not { } s) return;
        _document.Execute(new MoveSpawnCommand(id, x ?? s.X, z ?? s.Z));
    }

    void MovePlayerSpawn(string id, float? x = null, float? z = null)
    {
        if (PlayerSpawn(id) is not { } s) return;
        _document.Execute(new MovePlayerSpawnCommand(id, x ?? s.X, z ?? s.Z));
    }

    MapPlacement? Placement(string id)
    {
        foreach (MapPlacement p in _document.Doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
        return null;
    }

    MapSpawn? Spawn(string id)
    {
        foreach (MapSpawn s in _document.Doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
        return null;
    }

    MapPlayerSpawn? PlayerSpawn(string id)
    {
        foreach (MapPlayerSpawn s in _document.Doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
        return null;
    }

    // ---- frame-input + layout helpers --------------------------------------------------------------------

    EditorFrameInput BuildFrameInput(float dt)
    {
        InputState s = Manager!.Input;
        int vw = s.Width > 0 ? s.Width : 1, vh = s.Height > 0 ? s.Height : 1;
        Ray ray = _camera.ScreenToRay(s.MousePosition, vw, vh);
        Vector3 dir = ray.Direction.LengthSquared() > 1e-12f ? Vector3.Normalize(ray.Direction) : Vector3.UnitZ;

        bool overChrome = IsOverChrome(s.MousePosition);
        Pointer? ptr = Manager.Pointer;
        bool pressed = !overChrome && (ptr?.IsJustPressed ?? false);
        bool down = !overChrome && (ptr?.IsDown ?? false);
        bool released = ptr?.IsJustReleased ?? false;
        // Screen-space distance travelled since the press, in the pointer's own space (matches the TreeView row-drag
        // threshold precedent), so the body-drag gesture arms on the same 6f dead zone the outline reorder uses.
        float travel = ptr is not null ? (ptr.Position - ptr.PressOrigin).Length() : 0f;

        bool shift = s.IsDown(Key.LeftShift) || s.IsDown(Key.RightShift);
        return new EditorFrameInput(ray.Origin, dir,
            pointerPressed: pressed, pointerDown: down, pointerReleased: released,
            pointerTravel: travel,
            shift: shift,
            deletePressed: s.WasPressed(Key.Delete),
            // Shift+Escape opens the exit dialog (OpenExitDialog), so it never doubles as the tool gesture cancel.
            // A focused editor (inspector field or the palette/spawn filter) also owns a bare Escape this
            // frame, so the tool-cancel edge is suppressed too while AnyEditorFocused is true: without this,
            // one Escape would cancel the active tool gesture right out from under a field mid-edit. Only
            // NumberField actually self-cancels on Escape (CancelEdit, in UpdateWidgets which runs AFTER this
            // UpdateTools step). TextInput (TextRow and both filters) and ChoiceRow's Dropdown (pointer-only
            // here, no keyboard nav wired in) have no Escape handling of their own, so for those fields Escape
            // is simply inert while they hold focus, and that is accepted behavior, not a bug: such a field
            // only loses focus from a pointer action (a tap outside its bounds), and only then does the very
            // next Escape cancel the tool as normal.
            escapePressed: s.WasPressed(Key.Escape) && !shift && !AnyEditorFocused,
            dt: dt);
    }

    bool IsOverChrome(Vector2 windowPixel)
    {
        UiViewport? ui = Manager!.UiViewport;
        if (ui is null) return false;
        float dpi = ui.DpiScale > 0f ? ui.DpiScale : 1f;
        Vector2 p = windowPixel / dpi;
        return !ComputeLayout(ui.Width, ui.Height).Viewport.Contains(p);
    }

    float GizmoScaleFor(Vector3 pos) => MathF.Max(0.25f, Vector3.Distance(_camera.Position, pos) * 0.12f);

    float KindHeight(string kind) =>
        _viewport is not null && _viewport.KindHeights.TryGetValue(kind, out float h) ? h : FallbackKindHeight;

    /// <summary>Composes the status-strip text. The active mode name and its <see cref="EditorToolController.ModeHint"/>
    /// lead the line (the operator's most useful cue), followed by the undo/redo labels, the loaded window extent
    /// when the document opened windowed (<see cref="Window"/>), the exit chord, and any transient message (a save
    /// result or a bookmark action). Internal so a headless test can assert the ordering.</summary>
    internal string StatusLine()
    {
        string dirty = _document.IsDirty ? "*" : "";
        string hint = _controller.ModeHint;
        string undo = _document.History.UndoLabel ?? "-";
        string redo = _document.History.RedoLabel ?? "-";
        string window = _window is { } w ? $"   window: ({w.Min.X},{w.Min.Z})-({w.Max.X},{w.Max.Z})" : "";
        string tail = string.IsNullOrEmpty(_statusText) ? "" : "  |  " + _statusText;
        return $"{dirty}{_controller.Mode}   {hint}   undo: {undo}   redo: {redo}{window}   " +
            $"R: snap to ground   Ctrl+Up/Down: reorder feature   Shift+Esc: exit{tail}";
    }

    /// <summary>Fits <paramref name="text"/> to the status strip's available width (its full width minus
    /// <see cref="StatusTextInset"/> on both sides), truncating with a trailing ellipsis via
    /// <see cref="GuiDraw.TruncateWithEllipsis"/> when it does not fit. <c>SpriteBatch.DrawString</c> has no
    /// width or clip parameter of its own, so an unbounded status line (a long save-failure message, a
    /// compressed strip width) would otherwise run past the strip's edge with nothing to catch it.
    /// <paramref name="measureWidth"/> is the caller's width function (e.g. <c>s =&gt; font.Measure(s).X</c>),
    /// so the helper is pure and headless-testable without a live <c>SpriteFont</c>.</summary>
    internal static string TruncateStatusLine(string text, float stripWidth, Func<string, float> measureWidth) =>
        GuiDraw.TruncateWithEllipsis(text, MathF.Max(0f, stripWidth - StatusTextInset * 2f), measureWidth);

    void Fill(SpriteBatch batch, Rect r, Color color) =>
        batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height), color);

    // A chrome panel background (toolbar / outline / inspector / status / palette): a rounded, slightly lifted
    // fill instead of the flat Fill above, so the chrome matches GuiStyle.Modern's own rounded-surface look
    // (decision 6). Same signature as Fill, so every panel-background call site is a one-word swap.
    void FillPanel(SpriteBatch batch, Rect r, Color color) =>
        batch.DrawRounded(_white, new Vector4(r.X, r.Y, r.Width, r.Height), color, PanelCornerRadius);

    /// <summary>The status-strip rectangle the chrome lays out for a window of <paramref name="w"/> x
    /// <paramref name="h"/> points, honouring <see cref="MapEditorOptions.StatusBottomOffset"/>. Exposed so a
    /// headless test can assert the strip shifts up to clear a host-reserved bottom band.</summary>
    internal Rect StatusRect(float w, float h) => ComputeLayout(w, h).Status;

    /// <summary>The bottom-left panel rectangle for a window of <paramref name="w"/> x <paramref name="h"/>
    /// points (zero-height when neither Place tool is active). Exposed so a headless test can assert the palette
    /// region exists only in the Place modes.</summary>
    internal Rect PaletteRect(float w, float h) => ComputeLayout(w, h).Palette;

    /// <summary>The outline rectangle for a window of <paramref name="w"/> x <paramref name="h"/> points.
    /// Exposed so a headless test can assert the outline reflows over the freed palette space outside the
    /// Place modes.</summary>
    internal Rect OutlineRect(float w, float h) => ComputeLayout(w, h).Outline;

    /// <summary>The inspector rectangle for a window of <paramref name="w"/> x <paramref name="h"/> points.
    /// Exposed so a headless test can assert <see cref="InspectorPanelWidth"/> (flush against the right edge,
    /// independent of <see cref="OutlinePanelWidth"/> on the left).</summary>
    internal Rect InspectorRect(float w, float h) => ComputeLayout(w, h).Inspector;

    ChromeLayout ComputeLayout(float w, float h)
    {
        var toolbar = new Rect(0f, 0f, w, ToolbarHeight);
        float bodyTop = ToolbarHeight;
        // Reserve StatusBottomOffset points at the bottom for a host overlay (the Showcase display readout), so
        // the status strip and the body above it sit clear of it instead of stacking on the same pixels.
        float bottomReserve = StatusHeight + MathF.Max(0f, _options.StatusBottomOffset);
        float bodyBottom = MathF.Max(bodyTop, h - bottomReserve);
        float bodyH = bodyBottom - bodyTop;
        // The bottom-left panel (kit palette / spawn picker) exists only in the two Place tools; otherwise the
        // outline takes the whole left column and the panel rect collapses to zero height at its bottom edge.
        float outlineH = BottomPanelVisible ? bodyH * 0.5f : bodyH;
        var outline = new Rect(0f, bodyTop, OutlinePanelWidth, outlineH);
        var palette = new Rect(0f, bodyTop + outlineH, OutlinePanelWidth, bodyH - outlineH);
        var inspector = new Rect(w - InspectorPanelWidth, bodyTop, InspectorPanelWidth, bodyH);
        var status = new Rect(0f, bodyBottom, w, StatusHeight);
        var viewport = new Rect(OutlinePanelWidth, bodyTop, MathF.Max(0f, w - OutlinePanelWidth - InspectorPanelWidth), bodyH);
        return new ChromeLayout(toolbar, outline, inspector, palette, status, viewport);
    }

    // Splits the full toolbar strip into the tab-bar region (left) and the Save button rect at the right end
    // (decision 4): the button reserves SaveButtonWidth plus gaps, and the tab bar takes the rest, so the tabs
    // shrink to leave room instead of overlapping the button. The button is inset vertically within the strip.
    // Pure math, so the split is asserted headless.
    (Rect Tabs, Rect Save) SplitToolbar(Rect toolbar)
    {
        float saveW = MathF.Min(SaveButtonWidth, MathF.Max(0f, toolbar.Width - ToolbarGap * 2f));
        var save = new Rect(toolbar.Right - saveW - ToolbarGap,
            toolbar.Y + (toolbar.Height - SaveButtonHeight) * 0.5f, saveW, SaveButtonHeight);
        var tabs = new Rect(toolbar.X, toolbar.Y, MathF.Max(0f, toolbar.Width - saveW - ToolbarGap * 2f), toolbar.Height);
        return (tabs, save);
    }

    // Identity payload on an outline row: which document element the row selects. Internal (not private) so
    // tests can assert the resolved outline node's Tag matches the live selection after a sync.
    internal readonly record struct OutlineRef(SelectionKind Kind, string Id);

    // Which side effect a synthetic outline ACTION node runs when tapped (a node with no document element behind
    // it, e.g. an add affordance). Distinct from OutlineRef so OnOutlineSelected can tell the two apart.
    enum OutlineActionKind { AddBiomeBand, AddScatterLayer, AddCompanionLayer }

    // Identity payload on a synthetic outline action row (e.g. "[+ add band]"): the action to run on tap. Carried
    // in TreeNode.Tag in place of an OutlineRef, so the tap runs the action instead of setting a selection.
    readonly record struct OutlineAction(OutlineActionKind Kind);

    // A palette category label plus its ordinal-sorted kit ids. The source list is itself category-sorted, so this
    // pair is all a tree build needs to emit a category root and its leaves.
    readonly record struct PaletteCategory(string Label, IReadOnlyList<string> Kinds);

    // Identity payload on a palette / spawn-list leaf: the kit id (PlaceKind) or archetype id (SpawnArchetype) the
    // leaf selects. A category node's Tag is its label string instead, so a body-tap on a category is ignored while
    // a leaf tap sets the placed kind. The pinned "player spawn" spawn-list entry sets PlayerSpawn true (and carries
    // an empty Kind), so a tap on it flips the spawn tool to placing a player start instead of an archetype spawn.
    readonly record struct PaletteLeaf(string Kind, bool PlayerSpawn = false);

    // The chrome rectangles for one frame, computed in point space.
    readonly struct ChromeLayout
    {
        public readonly Rect Toolbar, Outline, Inspector, Palette, Status, Viewport;
        public ChromeLayout(Rect toolbar, Rect outline, Rect inspector, Rect palette, Rect status, Rect viewport)
        {
            Toolbar = toolbar; Outline = outline; Inspector = inspector;
            Palette = palette; Status = status; Viewport = viewport;
        }
    }

    // One stored fly-camera pose (Shift+1..9 stores, bare 1..9 recalls, decision 9). Set is false for a
    // never-stored slot's default zero value, so RecallBookmark can tell "nothing here" apart from a legitimately
    // stored pose that happens to sit at the world origin looking along +Z.
    readonly record struct CameraBookmark(Vector3 Position, float Yaw, float Pitch, bool Set = true);
}

/// <summary>Which baked transform-gizmo mesh a <see cref="MapEditorScene.ComputeGizmoMeshes"/> entry draws, keyed
/// to the mesh handles loaded once in <see cref="MapEditorScene.BuildWorld"/>.</summary>
internal enum GizmoMesh
{
    /// <summary>The three-axis translate arrows (X, Y, Z): a placement's full transform.</summary>
    TranslateArrowsFull,
    /// <summary>The two ground-plane translate arrows (X, Z) only, no vertical handle: a spawn's marker drag, or
    /// a feature / disc / rect shape's move.</summary>
    TranslateArrowsXZ,
    /// <summary>The flat yaw ring.</summary>
    YawRing,
    /// <summary>The corner scale cube.</summary>
    ScaleHandle,
    /// <summary>The selection marker pyramid.</summary>
    SelectionMarker,
}

/// <summary>Which document collection a viewport <see cref="OverlayDraw"/> came from. Drives its base fill color
/// (exclusions red-ish, scatter overrides orange, regions blue-ish, features amber).</summary>
internal enum OverlayCategory
{
    /// <summary>A scatter exclusion shape.</summary>
    Exclusion,
    /// <summary>A scatter override shape (a region-scoped density / kind tweak).</summary>
    ScatterOverride,
    /// <summary>A named, game-interpreted region shape.</summary>
    Region,
    /// <summary>A terrain feature's center marker.</summary>
    Feature,
    /// <summary>A biome band's world-Z edge line (a full-width line at Start or End).</summary>
    BiomeBand,
}

/// <summary>Which <see cref="Scene3D"/> debug-fill primitive draws an <see cref="OverlayDraw"/>.</summary>
internal enum OverlayShape
{
    /// <summary>A flat ground disc (<see cref="Scene3D.DebugFilledCircle"/>).</summary>
    Disc,
    /// <summary>A flat ground quad (<see cref="Scene3D.DebugFilledQuad(System.Numerics.Vector3, System.Numerics.Vector2, Color)"/>).</summary>
    Rect,
    /// <summary>A flat ground triangle fan (<see cref="Scene3D.DebugFilledFan"/>).</summary>
    Polygon,
}

/// <summary>One computed viewport overlay fill that makes an exclusion, region, or terrain feature visible: a
/// ground-plane translucent shape lifted a small epsilon above the terrain. A pure value produced by
/// <see cref="MapEditorScene.ComputeOverlayDrawList"/> and submitted to <see cref="Scene3D"/> untested, so the
/// doc-to-draw-list computation is fully headless-testable.</summary>
internal readonly struct OverlayDraw
{
    /// <summary>Which document collection this overlay came from (drives the base color).</summary>
    public readonly OverlayCategory Category;
    /// <summary>Which debug-fill primitive draws it.</summary>
    public readonly OverlayShape Shape;
    /// <summary>The fill center in world space, already lifted the overlay epsilon above the sampled ground. For a
    /// <see cref="OverlayShape.Polygon"/> this is the fan hub at the point centroid.</summary>
    public readonly Vector3 Center;
    /// <summary>The radius for a <see cref="OverlayShape.Disc"/> (the shape radius, or the fixed marker radius for a
    /// feature); zero for the other shapes.</summary>
    public readonly float Radius;
    /// <summary>The half-extents (X along world X, Y along world Z) for a <see cref="OverlayShape.Rect"/>; zero for
    /// the other shapes.</summary>
    public readonly Vector2 HalfExtents;
    /// <summary>The ground-height rim ring for a <see cref="OverlayShape.Polygon"/> (each vertex sampled at its own
    /// terrain height); null for the other shapes.</summary>
    public readonly IReadOnlyList<Vector3>? Rim;
    /// <summary>The RGBA fill color, already brightened when <see cref="Selected"/>.</summary>
    public readonly Color Color;
    /// <summary>True when this overlay's element is the current selection, so it is drawn brighter.</summary>
    public readonly bool Selected;

    /// <summary>Creates an overlay-draw record from its already-computed fields.</summary>
    public OverlayDraw(OverlayCategory category, OverlayShape shape, Vector3 center, float radius,
        Vector2 halfExtents, IReadOnlyList<Vector3>? rim, Color color, bool selected)
    {
        Category = category;
        Shape = shape;
        Center = center;
        Radius = radius;
        HalfExtents = halfExtents;
        Rim = rim;
        Color = color;
        Selected = selected;
    }
}
