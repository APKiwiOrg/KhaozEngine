using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

/// <summary>Turn-key startup for <see cref="MapEditorScene"/>: which document to open (and save back to on
/// Ctrl+S), the asset manifests the palette + picking read, the feature registry, and the game-supplied spawn
/// archetype list. A per-game editor head fills this and pushes the scene onto its <see cref="SceneManager"/>.
/// </summary>
public sealed class MapEditorOptions
{
    /// <summary>The map document file to load on enter and save back to on Ctrl+S. Empty starts a blank document.</summary>
    public string DocumentPath = "";

    /// <summary>Asset manifests parsed into the kit palette and the picking heights.</summary>
    public List<string> ManifestPaths = new();

    /// <summary>The feature registry used to load / save / build the document. Null defaults to
    /// <see cref="MapDocRegistry.CreateDefault"/>.</summary>
    public MapDocRegistry? Registry;

    /// <summary>Spawn archetype ids the game offers in the spawn tool (dropdown content).</summary>
    public List<string> SpawnArchetypes = new();
}

/// <summary>The turn-key in-engine map editor scene a per-game head pushes onto its <see cref="SceneManager"/>:
/// it wires a <see cref="ViewportWorld"/> + fly camera + <see cref="EditorToolController"/> together with the Gui
/// chrome (toolbar tab bar, tree outline, property-grid inspector, kit palette, status strip) and the undo / redo
/// / save hotkeys, over one <see cref="EditorDocument"/>. Shift+Escape is the exit chord: it pops the scene,
/// arming a discard warning first when the document has unsaved changes (Escape alone stays the gesture cancel).
/// Developer tooling, so the whole class is
/// <see cref="LocalizationExemptAttribute">localization-exempt</see>.
/// <para>The GPU-touching work lives behind the <see cref="BuildWorld"/> / <see cref="TeardownWorld"/> /
/// <see cref="CheckWorldRebuild"/> / <see cref="UpdateStreaming"/> seams (the Task 5 pattern), and the per-frame
/// step order is exposed through overridable <see cref="UpdateCamera"/> / <see cref="UpdateTools"/> seams, so the
/// lifecycle guards, update ordering, and save-failure handling are all headless-testable.</para>
/// </summary>
[LocalizationExempt]
public class MapEditorScene : GameScene, IGameScene3D
{
    /// <summary>Toolbar height in points.</summary>
    const float ToolbarHeight = 40f;
    /// <summary>Side-panel width in points (outline / palette on the left, inspector on the right).</summary>
    const float PanelWidth = 260f;
    /// <summary>Status-strip height in points.</summary>
    const float StatusHeight = 26f;
    /// <summary>Height in points of the filter box slotted at the top of the palette / spawn-list region.</summary>
    const float PaletteFilterHeight = 26f;
    /// <summary>Falls back to this world-space box height for a kit id absent from the manifests.</summary>
    const float FallbackKindHeight = 2f;

    static readonly Color PanelBackground = new(0.09f, 0.09f, 0.12f, 0.94f);
    static readonly Color StatusBackground = new(0.05f, 0.05f, 0.07f, 0.96f);
    static readonly Color SelectionHighlight = new(1.35f, 1.2f, 0.7f, 1f);

    static readonly LocalizedText[] ToolLabels =
    {
        LocalizedText.Raw("Select"), LocalizedText.Raw("Prop"), LocalizedText.Raw("Spawn"),
        LocalizedText.Raw("Exclude"), LocalizedText.Raw("Region"), LocalizedText.Raw("Feature"),
        LocalizedText.Raw("Bake"),
    };

    Scene3D _scene = null!;
    Texture2D _white = null!;
    DpiFont _font = null!;
    MapEditorOptions _options = null!;

    EditorDocument _document = null!;
    EditorToolController _controller = null!;
    ViewportWorld _viewport = null!;
    FlyCamera3D _camera = null!;
    FlyCameraController _camController = null!;

    MeshHandle _translateArrows, _yawRing, _scaleHandle, _selectionMarker;

    readonly InputManager _ui = new();
    TabBar _toolbar = null!;
    TreeView _outline = null!;
    PropertyGrid _inspector = null!;

    // Kit palette: a filter box above a category-grouped, collapsible tree. Spawn archetypes: a filter box above a
    // flat list (a TreeView with leaf-only roots, so it renders and hit-tests exactly like the palette minus the
    // categories). The two share the bottom-left panel region, swapped by the active tool (spawn tool -> spawn list,
    // everything else -> kit palette), so each filter box slots into the existing side-panel bounds cleanly.
    TextInput _paletteFilter = null!;
    TreeView _paletteTree = null!;
    TextInput _spawnFilter = null!;
    TreeView _spawnList = null!;

    // The grouped, twice-sorted palette source (categories ordinal, kit ids ordinal within each), parsed once from
    // KindCategories in OnEnter (the map is immutable after ViewportWorld construction) and re-filtered without
    // rebuilding. Per-category expansion is remembered across rebuilds so clearing a filter restores the tree.
    readonly List<PaletteCategory> _paletteSource = new();
    readonly Dictionary<string, bool> _paletteExpansion = new(StringComparer.Ordinal);
    string _paletteTreeFilter = "";   // the filter text the live palette tree was last built for
    string _spawnTreeFilter = "";     // the filter text the live spawn list was last built for

    bool _built;
    string _statusText = "";

    // Shift+Escape exit chord state: with unsaved changes the first press only ARMS the discard warning
    // (status-strip message) and the next Shift+Escape pops. Any save or document mutation disarms it.
    bool _exitArmed;

    // Inline-rename bookkeeping, shared by the region / placement / spawn inspectors: those selections are keyed
    // by name (region) or id (placement, spawn), so after a rename the selection must follow the new key. The
    // re-select is deferred until the rename row loses focus (an immediate Selection.Set would rebuild the
    // inspector mid-typing and drop the field's focus per keystroke). Only one inspector row is ever focused, so
    // a single pending slot covers all three renamable kinds.
    TextRow? _nameRow;
    SelectionKind _pendingSelectKind;
    string? _pendingSelectId;

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

    /// <summary>The category-grouped kit palette tree, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TreeView PaletteTree => _paletteTree;

    /// <summary>The kit-palette filter box, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TextInput PaletteFilter => _paletteFilter;

    /// <summary>The flat spawn-archetype list (leaf-only roots), or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TreeView SpawnList => _spawnList;

    /// <summary>The spawn-archetype filter box, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
    internal TextInput SpawnFilter => _spawnFilter;

    /// <summary>True while the Shift+Escape discard warning is armed (dirty document, one chord press in).
    /// Exposed for tests.</summary>
    internal bool ExitArmed => _exitArmed;

    // ---- lifecycle ---------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void OnEnter()
    {
        if (_built) return;

        MapDocRegistry registry = _options.Registry ?? MapDocRegistry.CreateDefault();
        _document = new EditorDocument(CreateDocument(registry), registry);
        _controller = new EditorToolController(_document) { HeightOf = KindHeight };
        _viewport = new ViewportWorld(_scene, _options.ManifestPaths);
        _camera = new FlyCamera3D { Position = new Vector3(0f, 24f, -32f), Pitch = -0.5f };
        _camController = new FlyCameraController(_camera);

        BuildChrome();
        BuildPaletteSource(PaletteKindCategories());
        RebuildPaletteTree("");   // full tree, every category expanded
        RebuildSpawnList("");     // full flat list
        if (_options.SpawnArchetypes.Count > 0) _controller.SpawnArchetype = _options.SpawnArchetypes[0];
        _controller.PlaceKind = DefaultPlaceKind();

        _document.DocumentChanged += OnDocumentChanged;
        _document.Selection.Changed += OnSelectionChanged;

        BuildWorld();
        RebuildOutline();
        RebuildInspector();
        _exitArmed = false;   // a re-entered scene starts with no leftover discard warning
        _built = true;
    }

    /// <inheritdoc/>
    public override void OnExit()
    {
        if (!_built) return;
        _built = false;
        _document.DocumentChanged -= OnDocumentChanged;
        _document.Selection.Changed -= OnSelectionChanged;
        TeardownWorld();
    }

    /// <summary>Loads the document from <see cref="MapEditorOptions.DocumentPath"/> (when it exists) or starts a
    /// blank one. A seam so a headless test can inject a document without touching the file system.</summary>
    protected virtual MapDocument CreateDocument(MapDocRegistry registry)
    {
        if (!string.IsNullOrWhiteSpace(_options.DocumentPath) && File.Exists(_options.DocumentPath))
            return MapDocumentFile.Load(_options.DocumentPath, new MapDocumentLoadOptions { Registry = registry });
        return new MapDocument
        {
            Id = "untitled",
            Bounds = new MapBounds { MinX = -128f, MinZ = -128f, MaxX = 128f, MaxZ = 128f },
        };
    }

    /// <summary>GPU seam: builds the streamed viewport world, uploads the gizmo meshes, points the controller at
    /// the built field, and installs the fly camera. Overridden headless in tests to skip all device work.</summary>
    protected virtual void BuildWorld()
    {
        _viewport.Build(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;

        _translateArrows = _scene.LoadMesh(GizmoGeometry.TranslateArrows());
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
        _scene.UnloadMesh(_yawRing);
        _scene.UnloadMesh(_scaleHandle);
        _scene.UnloadMesh(_selectionMarker);
    }

    // ---- per-frame ---------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void OnUpdate(float dt)
    {
        if (!_built) return;
        UpdateCamera(dt);
        UpdateTools(dt);
        CheckWorldRebuild();
        UpdateChrome(dt);
        UpdateStreaming(dt);
    }

    /// <summary>Fly-camera step (aspect upkeep + WASD/mouselook). Overridable for headless order tests.</summary>
    protected virtual void UpdateCamera(float dt)
    {
        int w = Manager!.Input.Width, h = Manager.Input.Height;
        if (h > 0) _camera.AspectRatio = (float)w / h;
        _camController.Update(Manager.Input, dt);
    }

    /// <summary>Tool step: builds the frame input from the camera + pointer and advances the controller.
    /// Overridable for headless order tests. No-ops until the world is built (the field is set).</summary>
    protected virtual void UpdateTools(float dt)
    {
        if (_controller.Field is null) return;
        if (TryGizmoWorldPos(out Vector3 gizmoPos)) _controller.GizmoScale = GizmoScaleFor(gizmoPos);
        _controller.Update(BuildFrameInput(dt));
    }

    /// <summary>Consumes a pending world rebuild after the tool step, so an edit this frame lands in the streamed
    /// world before the next frame's pick. Overridable for headless order tests. No-ops until the world is built.</summary>
    protected virtual void CheckWorldRebuild()
    {
        if (!_viewport.IsBuilt || !_document.WorldRebuildPending) return;
        _viewport.Rebuild(_document.Doc, _document.Registry);
        _document.AcknowledgeWorldRebuild();
        _controller.Field = _viewport.Field;
    }

    /// <summary>Hotkeys + Gui-chrome input step. Overridable for headless order tests.</summary>
    protected virtual void UpdateChrome(float dt)
    {
        HandleShortcuts();
        UpdateWidgets(dt);

        // Sync the selection to a renamed element (region by name, placement/spawn by id) once the rename row is
        // done (outside the grid's row iteration, so the inspector rebuild this triggers never tears down a row
        // mid-update).
        if (_pendingSelectId is string pending && (_nameRow is null || !_nameRow.Input.IsFocused))
        {
            SelectionKind kind = _pendingSelectKind;
            _pendingSelectId = null;
            _document.Selection.Set(kind, pending);
        }
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
        _viewport.Draw(_camera.Position, selId, SelectionHighlight);
        DrawGizmo(scene);
    }

    /// <inheritdoc/>
    public override void OnDrawUi(SpriteBatch batch)
    {
        if (!_built || batch is null || _font is null || Manager is null) return;
        UiViewport? ui = Manager.UiViewport;
        if (ui is null) return;
        SpriteFont font = _font.For(ui.DpiScale);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        Fill(batch, L.Outline, PanelBackground);
        Fill(batch, L.Inspector, PanelBackground);
        Fill(batch, L.Status, StatusBackground);

        _toolbar.Bounds = L.Toolbar;
        _toolbar.Font = font;
        _toolbar.Draw(batch, _white);

        _outline.Bounds = L.Outline;
        _outline.Draw(batch, _white, font);

        _inspector.Bounds = L.Inspector;
        _inspector.Draw(batch, _white, font);

        DrawPalette(batch, font, L.Palette);
        batch.DrawString(font, StatusLine(),
            new Vector2(MathF.Floor(L.Status.X + 8f), MathF.Floor(L.Status.Y + (StatusHeight - font.LineHeight) * 0.5f)),
            new Color(0.85f, 0.87f, 0.92f, 1f));
    }

    void DrawGizmo(Scene3D scene)
    {
        if (!TryGizmoWorldPos(out Vector3 pos)) return;
        float s = GizmoScaleFor(pos);
        Matrix4x4 world = Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(pos);
        if (_document.Selection.Kind == SelectionKind.Placement)
        {
            scene.DrawOverlayMesh(_translateArrows, world);
            scene.DrawOverlayMesh(_yawRing, world);
            scene.DrawOverlayMesh(_scaleHandle, world);
        }
        else
        {
            scene.DrawOverlayMesh(_selectionMarker, world);
        }
    }

    void DrawPalette(SpriteBatch batch, SpriteFont font, Rect bounds)
    {
        Fill(batch, bounds, PanelBackground);
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
        _outline = new TreeView(default) { RowHeight = 22f };
        _inspector = new PropertyGrid(default);
        _outline.OnSelected = OnOutlineSelected;

        _paletteFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter kits...") };
        _paletteTree = new TreeView(default) { RowHeight = 22f };
        _paletteTree.OnSelected = OnPaletteSelected;

        _spawnFilter = new TextInput(default) { PlaceholderContent = LocalizedText.Raw("Filter spawns...") };
        _spawnList = new TreeView(default) { RowHeight = 22f };
        _spawnList.OnSelected = OnSpawnSelected;
    }

    void UpdateWidgets(float dt)
    {
        UiViewport? ui = Manager!.UiViewport;
        if (ui is null) return;
        _ui.Update(Manager.Input, ui);
        ChromeLayout L = ComputeLayout(ui.Width, ui.Height);

        _toolbar.Bounds = L.Toolbar;
        if (_toolbar.Update(_ui.Pointer)) _controller.Mode = (EditorToolMode)_toolbar.ActiveIndex;

        _outline.Bounds = L.Outline;
        _outline.Update(_ui);

        _inspector.Bounds = L.Inspector;
        _inspector.Update(_ui, dt);

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
        RefreshPalettes();
    }

    // The spawn tool swaps the bottom-left panel to the spawn-archetype picker; every other tool shows the kit
    // palette. Both live in the same region, so only one filter box + list is driven / drawn per frame.
    bool SpawnMode => _controller.Mode == EditorToolMode.PlaceSpawn;

    void HandleShortcuts()
    {
        InputState s = Manager!.Input;
        bool shift = s.IsDown(Key.LeftShift) || s.IsDown(Key.RightShift);
        if (shift && s.WasPressed(Key.Escape)) { HandleExitChord(); return; }
        bool ctrl = s.IsDown(Key.LeftControl) || s.IsDown(Key.RightControl);
        if (!ctrl) return;
        if (s.WasPressed(Key.Z)) { if (shift) _document.Redo(); else _document.Undo(); }
        else if (s.WasPressed(Key.Y)) _document.Redo();
        else if (s.WasPressed(Key.S)) SaveDocument();
    }

    // Shift+Escape: pop the scene right away when the document has no unsaved changes. With unsaved changes
    // the first press only arms the discard warning (status strip), and the next Shift+Escape (with nothing
    // disarming it in between, see SaveDocument / OnDocumentChanged) discards and pops.
    void HandleExitChord()
    {
        if (!_document.IsDirty || _exitArmed)
        {
            Manager?.Pop();
            return;
        }
        _exitArmed = true;
        _statusText = "Unsaved changes. Shift+Escape again to discard and exit";
    }

    /// <summary>Saves the document back to <see cref="MapEditorOptions.DocumentPath"/>, surfacing a
    /// <see cref="MapDocumentException"/> (invalid content) into the status strip instead of throwing. Internal so
    /// the Ctrl+S handler and the tests share one path. Any save attempt disarms the Shift+Escape discard
    /// warning: the user chose to save, so the pending discard no longer reflects their intent.</summary>
    internal void SaveDocument()
    {
        _exitArmed = false;
        if (string.IsNullOrWhiteSpace(_options.DocumentPath))
        {
            _statusText = "No document path set";
            return;
        }
        try
        {
            MapDocumentFile.Save(_document.Doc, _options.DocumentPath, _document.Registry);
            _document.MarkSaved();
            _statusText = "Saved " + _options.DocumentPath;
        }
        catch (MapDocumentException ex)
        {
            _statusText = "Save failed: " + ex.Message;
        }
    }

    void OnDocumentChanged()
    {
        _exitArmed = false;   // any mutation (execute / undo / redo) invalidates the pending discard warning
        _viewport.InvalidatePlacements();
        RebuildOutline();
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
    }

    void OnOutlineSelected(TreeNode node)
    {
        if (node.Tag is OutlineRef r) _document.Selection.Set(r.Kind, r.Id);
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
        if (_paletteTreeFilter.Trim().Length == 0)
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
        _paletteTreeFilter = filter;
    }

    // Rebuild the flat spawn-archetype list for a filter substring, preserving the game-authored order (no
    // categories, no re-sort). Called only when the spawn filter text changes.
    void RebuildSpawnList(string filter)
    {
        string needle = filter.Trim();
        _spawnList.Roots.Clear();
        _spawnList.Selected = null;
        foreach (string archetype in _options.SpawnArchetypes)
        {
            if (needle.Length > 0 && archetype.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _spawnList.Roots.Add(new TreeNode(LocalizedText.Raw(archetype), new PaletteLeaf(archetype)));
        }
        _spawnTreeFilter = filter;
    }

    /// <summary>Rebuilds the palette tree and / or the spawn list when its filter box text no longer matches what
    /// the live view was built for. Called once per widget step after the filter boxes are driven, so a rebuild
    /// happens only on a filter change, not every frame. Internal so a headless test can trigger it after a
    /// <see cref="TextInput.SetText"/> without a full UI frame.</summary>
    internal void RefreshPalettes()
    {
        if (!string.Equals(_paletteFilter.Text, _paletteTreeFilter, StringComparison.Ordinal))
            RebuildPaletteTree(_paletteFilter.Text);
        if (!string.Equals(_spawnFilter.Text, _spawnTreeFilter, StringComparison.Ordinal))
            RebuildSpawnList(_spawnFilter.Text);
    }

    // A leaf carries the kit id; a category body-tap (Tag is the label string) never changes the placed kind.
    void OnPaletteSelected(TreeNode node)
    {
        if (node.Tag is PaletteLeaf leaf) _controller.PlaceKind = leaf.Kind;
    }

    void OnSpawnSelected(TreeNode node)
    {
        if (node.Tag is PaletteLeaf leaf) _controller.SpawnArchetype = leaf.Kind;
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
        _outline.Roots.Add(Category("Placements", PlacementNodes()));
        _outline.Roots.Add(Category("Spawns", SpawnNodes()));
        _outline.Roots.Add(Category("Features", FeatureNodes()));
        _outline.Roots.Add(Category("Exclusions", ExclusionNodes()));
        _outline.Roots.Add(Category("Regions", RegionNodes()));
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

    IEnumerable<TreeNode> FeatureNodes()
    {
        for (int i = 0; i < _document.Doc.Terrain.Features.Count; i++)
            yield return new TreeNode(LocalizedText.Raw($"[{i}] {_document.Doc.Terrain.Features[i].Type}"),
                new OutlineRef(SelectionKind.Feature, i.ToString(CultureInfo.InvariantCulture)));
    }

    IEnumerable<TreeNode> ExclusionNodes()
    {
        for (int i = 0; i < _document.Doc.Exclusions.Count; i++)
            yield return new TreeNode(LocalizedText.Raw($"exclusion[{i}]"),
                new OutlineRef(SelectionKind.Exclusion, i.ToString(CultureInfo.InvariantCulture)));
    }

    IEnumerable<TreeNode> RegionNodes()
    {
        foreach (MapRegion r in _document.Doc.Regions)
            yield return new TreeNode(LocalizedText.Raw(r.Name), new OutlineRef(SelectionKind.Region, r.Name));
    }

    void RebuildInspector()
    {
        _inspector.Rows.Clear();
        _nameRow = null;
        EditorSelection sel = _document.Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Terrain: BuildTerrainInspector(); break;
            case SelectionKind.Placement: BuildPlacementInspector(sel.Id); break;
            case SelectionKind.Spawn: BuildSpawnInspector(sel.Id); break;
            case SelectionKind.Feature: BuildFeatureInspector(sel.Id); break;
            case SelectionKind.Exclusion: BuildExclusionInspector(sel.Id); break;
            case SelectionKind.Region: BuildRegionInspector(sel.Id); break;
            default: break;
        }
    }

    // The terrain root inspector: the editable water level (routed through EditTerrainCommand so it coalesces
    // scrubs and forces the scatter-honouring world rebuild) plus read-only seed and biome-count displays. The
    // setter captures the LIVE water level as the command's old value before Execute applies the new one.
    void BuildTerrainInspector()
    {
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("WaterLevel"),
            () => _document.Doc.Terrain.WaterLevel,
            v => _document.Execute(new EditTerrainCommand(v, _document.Doc.Terrain.WaterLevel))));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Seed"),
            () => _document.Doc.Terrain.Seed.ToString(CultureInfo.InvariantCulture)));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Biomes"),
            () => _document.Doc.Terrain.Biomes.Count.ToString(CultureInfo.InvariantCulture)));
    }

    // The inline-rename Name row shared by the region, placement, and spawn inspectors. A closure tracks the
    // CURRENT key across renames, so the row (and every downstream row that reads the returned getter) keeps
    // working and keeps focus while the user types. The setter guards blank / unchanged / collision / vanished,
    // routes the rename through `rename`, then queues a deferred re-select of the new key (fired once the row
    // loses focus, see UpdateChrome) so the name-keyed selection follows the rename. Returns a getter for the
    // live key so the caller's remaining rows track the element across a rename.
    Func<string> AddNameRow(SelectionKind kind, string key, Func<string, bool> exists,
        Func<string, string, IEditorCommand> rename)
    {
        string current = key;
        var row = new TextRow(LocalizedText.Raw("Name"),
            () => current,
            v =>
            {
                if (string.IsNullOrWhiteSpace(v) || string.Equals(v, current, StringComparison.Ordinal)) return;
                if (exists(v) || !exists(current)) return;   // collision or vanished
                _document.Execute(rename(current, v));
                current = v;
                _pendingSelectKind = kind;
                _pendingSelectId = v;
            });
        _nameRow = row;
        _inspector.Rows.Add(row);
        return () => current;
    }

    void BuildPlacementInspector(string id)
    {
        if (Placement(id) is null) return;
        Func<string> cur = AddNameRow(SelectionKind.Placement, id,
            v => Placement(v) is not null, (oldId, newId) => new RenamePlacementCommand(oldId, newId));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("X"),
            () => Placement(cur())?.X ?? 0f, v => MovePlacement(cur(), x: v)));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Z"),
            () => Placement(cur())?.Z ?? 0f, v => MovePlacement(cur(), z: v)));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Yaw"),
            () => Placement(cur())?.Yaw ?? 0f, v => _document.Execute(new RotatePlacementCommand(cur(), v))));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Scale"),
            () => Placement(cur())?.Scale ?? 1f, v => _document.Execute(new ScalePlacementCommand(cur(), v)),
            min: 0.01f));
    }

    void BuildSpawnInspector(string id)
    {
        if (Spawn(id) is null) return;
        Func<string> cur = AddNameRow(SelectionKind.Spawn, id,
            v => Spawn(v) is not null, (oldId, newId) => new RenameSpawnCommand(oldId, newId));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("X"),
            () => Spawn(cur())?.X ?? 0f, v => MoveSpawn(cur(), x: v)));
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw("Z"),
            () => Spawn(cur())?.Z ?? 0f, v => MoveSpawn(cur(), z: v)));
        _inspector.Rows.Add(new BoolRow(LocalizedText.Raw("Enabled"),
            () => Spawn(cur())?.Enabled ?? false, v => _document.Execute(new SetSpawnEnabledCommand(cur(), v))));
        _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Archetype"),
            () => Spawn(cur())?.ArchetypeId ?? "", v => { if (Spawn(cur()) is { } s) s.ArchetypeId = v; }));
    }

    // ---- feature / exclusion / region inspectors -----------------------------------------------------------

    void BuildFeatureInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        MapFeature? feature = FeatureAt(index);
        if (feature is null) return;

        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Type"), () => FeatureAt(index)?.Type ?? ""));
        switch (feature)
        {
            case LakeFeatureDoc:
                AddFeatureRow<LakeFeatureDoc>(index, "CenterX", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<LakeFeatureDoc>(index, "CenterZ", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<LakeFeatureDoc>(index, "Radius", f => f.Radius, (f, v) => f.Radius = v);
                AddFeatureRow<LakeFeatureDoc>(index, "Depth", f => f.Depth, (f, v) => f.Depth = v);
                break;
            case FlattenFeatureDoc:
                AddFeatureRow<FlattenFeatureDoc>(index, "CenterX", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "CenterZ", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "Radius", f => f.Radius, (f, v) => f.Radius = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "TargetHeight", f => f.TargetHeight, (f, v) => f.TargetHeight = v);
                AddFeatureRow<FlattenFeatureDoc>(index, "Blend", f => f.Blend, (f, v) => f.Blend = v);
                break;
            case RimFeatureDoc:
                AddFeatureRow<RimFeatureDoc>(index, "CenterX", f => f.CenterX, (f, v) => f.CenterX = v);
                AddFeatureRow<RimFeatureDoc>(index, "CenterZ", f => f.CenterZ, (f, v) => f.CenterZ = v);
                AddFeatureRow<RimFeatureDoc>(index, "InnerRadius", f => f.InnerRadius, (f, v) => f.InnerRadius = v);
                AddFeatureRow<RimFeatureDoc>(index, "OuterRadius", f => f.OuterRadius, (f, v) => f.OuterRadius = v);
                AddFeatureRow<RimFeatureDoc>(index, "WallHeight", f => f.WallHeight, (f, v) => f.WallHeight = v);
                AddFeatureRow<RimFeatureDoc>(index, "Ruggedness", f => f.Ruggedness, (f, v) => f.Ruggedness = v);
                break;
            case RidgeFeatureDoc:
                AddFeatureRow<RidgeFeatureDoc>(index, "PointX", f => f.PointX, (f, v) => f.PointX = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PointZ", f => f.PointZ, (f, v) => f.PointZ = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "Height", f => f.Height, (f, v) => f.Height = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "Width", f => f.Width, (f, v) => f.Width = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PassAlong", f => f.PassAlong, (f, v) => f.PassAlong = v);
                AddFeatureRow<RidgeFeatureDoc>(index, "PassWidth", f => f.PassWidth, (f, v) => f.PassWidth = v);
                break;
            default:
                break;   // unknown/custom feature type: the read-only Type row above is the whole inspector
        }
    }

    // One scrubbed parameter of the feature at `index`: get reads the LIVE DTO (the instance at the index is
    // replaced by every edit), set clones the current DTO with the one property changed and routes it through
    // EditFeatureCommand, whose same-index merge makes a scrub coalesce into one undo step.
    void AddFeatureRow<T>(int index, string label, Func<T, float> get, Action<T, float> assign) where T : MapFeature
    {
        _inspector.Rows.Add(new FloatRow(LocalizedText.Raw(label),
            () => FeatureAt(index) is T f ? get(f) : 0f,
            v =>
            {
                if (FeatureAt(index) is not T current) return;
                var clone = (T)CloneFeature(current);
                assign(clone, v);
                _document.Execute(new EditFeatureCommand(index, clone, current));
            }));
    }

    MapFeature? FeatureAt(int index)
    {
        List<MapFeature> features = _document.Doc.Terrain.Features;
        return index >= 0 && index < features.Count ? features[index] : null;
    }

    // Copies one of the four built-in feature DTOs so an edit replaces the instance (EditFeatureCommand holds
    // old + new by reference). Only ever called for the types the switch above binds.
    static MapFeature CloneFeature(MapFeature feature) => feature switch
    {
        LakeFeatureDoc l => new LakeFeatureDoc
        {
            CenterX = l.CenterX, CenterZ = l.CenterZ, Radius = l.Radius, Depth = l.Depth,
            InnerFraction = l.InnerFraction, OuterFraction = l.OuterFraction,
        },
        FlattenFeatureDoc f => new FlattenFeatureDoc
        {
            CenterX = f.CenterX, CenterZ = f.CenterZ, Radius = f.Radius,
            TargetHeight = f.TargetHeight, Blend = f.Blend,
        },
        RidgeFeatureDoc r => new RidgeFeatureDoc
        {
            PointX = r.PointX, PointZ = r.PointZ, DirectionX = r.DirectionX, DirectionZ = r.DirectionZ,
            Height = r.Height, Width = r.Width, PassAlong = r.PassAlong, PassWidth = r.PassWidth,
        },
        RimFeatureDoc rim => CloneRim(rim),
        _ => throw new InvalidOperationException($"No clone support for feature type '{feature.Type}'."),
    };

    static RimFeatureDoc CloneRim(RimFeatureDoc r)
    {
        var clone = new RimFeatureDoc
        {
            CenterX = r.CenterX, CenterZ = r.CenterZ, InnerRadius = r.InnerRadius, OuterRadius = r.OuterRadius,
            WallHeight = r.WallHeight, Ruggedness = r.Ruggedness, Seed = r.Seed, CrestFrequency = r.CrestFrequency,
        };
        foreach (RimPassDoc pass in r.Passes)
            clone.Passes.Add(new RimPassDoc { AngleRadians = pass.AngleRadians, HalfWidth = pass.HalfWidth, Falloff = pass.Falloff });
        return clone;
    }

    void BuildExclusionInspector(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
        if (index < 0 || index >= _document.Doc.Exclusions.Count) return;
        AddShapeRows(() => index < _document.Doc.Exclusions.Count ? _document.Doc.Exclusions[index].Shape : null);
    }

    void BuildRegionInspector(string name)
    {
        if (RegionByName(name) is null) return;
        Func<string> cur = AddNameRow(SelectionKind.Region, name,
            v => RegionByName(v) is not null, (oldName, newName) => new RenameRegionCommand(oldName, newName));
        AddShapeRows(() => RegionByName(cur())?.Shape);
    }

    void AddShapeRows(Func<MapShapeDoc?> shape)
    {
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Shape"), () => ShapeKind(shape())));
        _inspector.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("Params"), () => ShapeParams(shape())));
    }

    static string ShapeKind(MapShapeDoc? shape) => shape switch
    {
        DiscShapeDoc => "disc",
        RectShapeDoc => "rect",
        PolygonShapeDoc => "polygon",
        null => "(none)",
        _ => shape.GetType().Name,
    };

    static string ShapeParams(MapShapeDoc? shape) => shape switch
    {
        DiscShapeDoc d => FormattableString.Invariant($"center ({d.CenterX:0.##}, {d.CenterZ:0.##})  radius {d.Radius:0.##}"),
        RectShapeDoc r => FormattableString.Invariant($"({r.MinX:0.##}, {r.MinZ:0.##}) .. ({r.MaxX:0.##}, {r.MaxZ:0.##})"),
        PolygonShapeDoc p => FormattableString.Invariant($"{p.Points.Count} points"),
        _ => "",
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

        bool shift = s.IsDown(Key.LeftShift) || s.IsDown(Key.RightShift);
        return new EditorFrameInput(ray.Origin, dir,
            pointerPressed: pressed, pointerDown: down, pointerReleased: released,
            shift: shift,
            deletePressed: s.WasPressed(Key.Delete),
            // Shift+Escape is the exit chord (HandleExitChord), so it never doubles as the tool gesture
            // cancel. Escape alone stays the cancel edge.
            escapePressed: s.WasPressed(Key.Escape) && !shift,
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

    bool TryGizmoWorldPos(out Vector3 pos)
    {
        pos = default;
        if (_controller.Field is null) return false;
        EditorSelection sel = _document.Selection;
        if (sel.Kind == SelectionKind.Placement && Placement(sel.Id) is { } p)
        {
            pos = new Vector3(p.X, p.Y ?? _controller.Field.SampleHeight(p.X, p.Z), p.Z);
            return true;
        }
        if (sel.Kind == SelectionKind.Spawn && Spawn(sel.Id) is { } s)
        {
            pos = new Vector3(s.X, _controller.Field.SampleHeight(s.X, s.Z), s.Z);
            return true;
        }
        return false;
    }

    float GizmoScaleFor(Vector3 pos) => MathF.Max(0.25f, Vector3.Distance(_camera.Position, pos) * 0.12f);

    float KindHeight(string kind) =>
        _viewport is not null && _viewport.KindHeights.TryGetValue(kind, out float h) ? h : FallbackKindHeight;

    /// <summary>Composes the status-strip text. The active mode name and its <see cref="EditorToolController.ModeHint"/>
    /// lead the line (the operator's most useful cue), followed by the undo/redo labels, the exit chord, and any
    /// transient message (save result, discard warning). Internal so a headless test can assert the ordering.</summary>
    internal string StatusLine()
    {
        string dirty = _document.IsDirty ? "*" : "";
        string hint = _controller.ModeHint;
        string undo = _document.History.UndoLabel ?? "-";
        string redo = _document.History.RedoLabel ?? "-";
        string tail = string.IsNullOrEmpty(_statusText) ? "" : "  |  " + _statusText;
        return $"{dirty}{_controller.Mode}   {hint}   undo: {undo}   redo: {redo}   Shift+Esc: exit{tail}";
    }

    void Fill(SpriteBatch batch, Rect r, Color color) =>
        batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height), color);

    ChromeLayout ComputeLayout(float w, float h)
    {
        var toolbar = new Rect(0f, 0f, w, ToolbarHeight);
        float bodyTop = ToolbarHeight;
        float bodyBottom = MathF.Max(bodyTop, h - StatusHeight);
        float bodyH = bodyBottom - bodyTop;
        float half = bodyH * 0.5f;
        var outline = new Rect(0f, bodyTop, PanelWidth, half);
        var palette = new Rect(0f, bodyTop + half, PanelWidth, bodyH - half);
        var inspector = new Rect(w - PanelWidth, bodyTop, PanelWidth, bodyH);
        var status = new Rect(0f, bodyBottom, w, StatusHeight);
        var viewport = new Rect(PanelWidth, bodyTop, MathF.Max(0f, w - 2f * PanelWidth), bodyH);
        return new ChromeLayout(toolbar, outline, inspector, palette, status, viewport);
    }

    // Identity payload on an outline row: which document element the row selects.
    readonly record struct OutlineRef(SelectionKind Kind, string Id);

    // A palette category label plus its ordinal-sorted kit ids. The source list is itself category-sorted, so this
    // pair is all a tree build needs to emit a category root and its leaves.
    readonly record struct PaletteCategory(string Label, IReadOnlyList<string> Kinds);

    // Identity payload on a palette / spawn-list leaf: the kit id (PlaceKind) or archetype id (SpawnArchetype) the
    // leaf selects. A category node's Tag is its label string instead, so a body-tap on a category is ignored while
    // a leaf tap sets the placed kind.
    readonly record struct PaletteLeaf(string Kind);

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
}
