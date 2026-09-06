using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor
{
    /// <summary>Turn-key startup for <see cref="MapEditorLandingScene"/> (decision 6): the menu title, the head's
    /// create-map and open-editor hooks, the recent-files store, and the head's quit path. A per-game editor head
    /// fills this and pushes the landing scene as the bottom scene on its <see cref="SceneManager"/>.</summary>
    public sealed class MapEditorLandingOptions
    {
        /// <summary>The menu heading. Defaults (unset) to a raw fallback in the scene.</summary>
        public LocalizedText Title;

        /// <summary>Creates a new map document for a validated, typed name and returns its path, or null on failure
        /// (the head owns file IO and the default directory). The scene validates the name (non-empty, no path
        /// separators) before calling this, touches the returned path, and pushes <see cref="OpenEditor"/> for it. A
        /// null return leaves an inline failure note without pushing anything.</summary>
        public Func<string, string?>? CreateMap;

        /// <summary>Builds a fully-initialized editor scene for a map path (the head assembles the
        /// <see cref="MapEditorScene"/> with its Scene3D / manifests / registry). The landing scene only pushes the
        /// result and stays beneath it, so the editor's Close pops back to this menu (decision 1).</summary>
        public Func<string, GameScene>? OpenEditor;

        /// <summary>The recent-files store the Open Recent list reads and prunes. Null renders an empty recent list.</summary>
        public IRecentFilesStore? Recent;

        /// <summary>The map documents this head knows about, queried on demand for the Open Map section. The head owns
        /// file IO and decides which directories it looks in: the engine never traverses a directory itself, the same
        /// seam as <see cref="CreateMap"/> (builds the path and writes the document) and <see cref="OpenEditor"/> (builds
        /// the scene). Null renders no Open Map section at all, so a head that does not wire it keeps exactly the menu it
        /// had before. A wired hook returning nothing still renders the section with a placeholder row, so the layout does
        /// not jump as maps migrate into Open Recent.</summary>
        public Func<IReadOnlyList<string>>? DiscoverMaps;

        /// <summary>How the menu leaves the app (the head's quit path, e.g. a <c>GameApp</c> subclass calling the
        /// protected <c>Quit()</c>). Null leaves the Quit button a no-op with an inline note, since a scene never
        /// touches window APIs directly (decision 1).</summary>
        public Action? RequestQuit;
    }

    /// <summary>The turn-key entry menu a per-game editor head pushes as the bottom scene on its
    /// <see cref="SceneManager"/> (decision 6): a title, a New Map row (a name field and a Create button), an Open
    /// Recent list (one button per recent path, most-recent first, missing files greyed and pruned on an activation
    /// attempt), an Open Map section, and a Quit button. Creating or opening a map pushes the head-built editor on
    /// top and leaves this scene at the stack bottom, so the editor's Close pops back here (decision 1). Only 2D
    /// chrome (no 3D pass, so it does not implement <c>IGameScene3D</c>). Developer tooling, so the whole class is
    /// <see cref="LocalizationExemptAttribute">localization-exempt</see>. The widget drive is viewport-gated, so the
    /// scene runs headless (its create / activate / quit actions are reachable without a live viewport).
    /// <para>Open Map (issue 359) is the reachability seam for a map document the recents store does not know about
    /// yet, e.g. a game's committed map on a fresh machine that has never opened it through this editor. It sits
    /// BELOW Open Recent and above the note/Quit rows: Open Recent is the most-recently-used fast lane whose entries
    /// keep stable positions, and Open Map is the variable-length remainder. A path already in the recents store is
    /// filtered OUT of Open Map (ordinal compare, matching <see cref="IRecentFilesStore"/>'s own identity rule), so a
    /// map renders exactly once, and the first time it is opened it migrates from Open Map into Open Recent.
    /// <see cref="MapEditorLandingOptions.DiscoverMaps"/> null renders no section at all (a head that does not wire
    /// it keeps exactly the menu it had before).</para></summary>
    [LocalizationExempt]
    public sealed class MapEditorLandingScene : GameScene
    {
        const float PanelWidth = 460f;
        const float Pad = 22f;
        const float TitleRowHeight = 40f;
        const float SectionLabelHeight = 22f;
        const float FieldRowHeight = 34f;
        const float MapRowHeight = 32f;
        const float RowGap = 8f;
        const float MapRowGap = 6f;
        const float NoteRowHeight = 22f;
        const float QuitRowHeight = 34f;
        const float CreateButtonWidth = 110f;
        const float FieldButtonGap = 10f;

        static readonly Color BackdropColor = new(0.06f, 0.07f, 0.09f, 1f);
        static readonly Color PanelBackground = new(0.115f, 0.12f, 0.165f, 0.98f);
        static readonly Color TitleColor = new(0.92f, 0.94f, 0.98f, 1f);
        static readonly Color LabelColor = new(0.62f, 0.66f, 0.74f, 1f);
        static readonly Color PlaceholderColor = new(0.5f, 0.53f, 0.6f, 1f);
        static readonly Color NoteColor = new(0.95f, 0.78f, 0.45f, 1f);
        static readonly float PanelCornerRadius = GuiStyle.Modern.CornerRadius;

        // A map-list button (either list) whose file is missing: greyed text over a muted fill so it reads inactive,
        // yet still Enabled so a click runs ActivateRecent (which prunes it from the store) or ActivateDiscovered
        // (which re-queries the head). The disabled visual can't do double duty here, since a disabled Button never
        // fires its OnClick (decision 6 wants the click to prune, not just disable).
        static readonly GuiStyle MissingStyle = BuildMissingStyle();

        // One map-list entry (Open Recent or Open Map): the button and the full path it activates (the button's
        // label shows only the file name). The path is the identity that ActivateRecent, ActivateDiscovered, and
        // the store key all act on, not the shortened label.
        readonly struct MapEntry
        {
            public readonly Button Button;
            public readonly string Path;
            public MapEntry(Button button, string path) { Button = button; Path = path; }
        }

        Texture2D _white = null!;
        DpiFont _font = null!;
        MapEditorLandingOptions _options = null!;

        readonly InputManager _ui = new();
        TextInput _nameInput = null!;
        Button _createButton = null!;
        Button _quitButton = null!;
        readonly List<MapEntry> _recent = new();
        readonly ScrollablePanel _mapScroll = new(default)
        {
            BlocksPointer = false,
            ItemSpacing = 0f,
        };

        // The last DiscoverMaps query result: normalized (null/whitespace skipped), deduped (ordinal), sorted. Kept
        // separate from _discoveredButtons because a recents-only mutation (no new query) can change which entries
        // are filtered out without re-running the head's file IO.
        readonly List<string> _discovered = new();
        readonly List<MapEntry> _discoveredButtons = new();

        string _note = "";
        bool _built;

        // Whether this scene was the top of the SceneManager stack as of the last OnUpdate. SceneManager gives this
        // scene no re-exposure hook (see RefreshDiscoveredOnReExpose), so this is how the scene notices it has
        // become active again after the head's editor scene popped off above it.
        bool _wasTopOfStack;

        // The store's Paths as of the last RebuildRecentButtons, so OnUpdate can notice a mutation made while this
        // scene was not driving the actions itself (e.g. a future Save-As from the editor scene pushed on top).
        // IRecentFilesStore.Paths aliases the SAME list instance across calls for EditorRecentFiles (only its
        // contents change), so a reference/identity check can never see such a mutation. This cache is a real copy
        // compared by ordinal sequence instead.
        readonly List<string> _lastSeenRecentPaths = new();

        /// <summary>Wires the shared white pixel and UI font, and the landing options, then returns this for chaining
        /// (the <see cref="MapEditorScene.Init"/> pattern). Nothing is dereferenced until <see cref="OnEnter"/>.</summary>
        public MapEditorLandingScene Init(Texture2D white, DpiFont font, MapEditorLandingOptions options)
        {
            _white = white;
            _font = font;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            return this;
        }

        /// <summary>The inline status note (last validation failure, create failure, prune, or quit no-op), or empty
        /// when none. Exposed for tests.</summary>
        internal string Note => _note;

        /// <summary>The New Map name field, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
        internal TextInput NameInput => _nameInput;

        /// <summary>The Create button, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
        internal Button CreateButton => _createButton;

        /// <summary>The Quit button, or null before <see cref="OnEnter"/>. Exposed for tests.</summary>
        internal Button QuitButton => _quitButton;

        /// <summary>The number of Open Recent buttons currently built (mirrors the store's <see cref="IRecentFilesStore.Paths"/>
        /// count as of the last rebuild). Exposed for tests.</summary>
        internal int RecentButtonCount => _recent.Count;

        /// <summary>The Nth Open Recent button, or null when out of range. After an <see cref="OnUpdate"/> pass its
        /// <c>Bounds</c> reflect that frame's layout, so a test can drive a real tap against it. Exposed for tests.</summary>
        internal Button? RecentButtonAt(int index) => index >= 0 && index < _recent.Count ? _recent[index].Button : null;

        /// <summary>The number of Open Map buttons currently built (the last <see cref="QueryDiscoveredMaps"/> result,
        /// filtered against Open Recent). Exposed for tests.</summary>
        internal int DiscoveredButtonCount => _discoveredButtons.Count;

        /// <summary>The Nth Open Map button, or null when out of range. After an <see cref="OnUpdate"/> pass its
        /// <c>Bounds</c> reflect that frame's layout, so a test can drive a real tap against it. Exposed for tests.</summary>
        internal Button? DiscoveredButtonAt(int index) => index >= 0 && index < _discoveredButtons.Count ? _discoveredButtons[index].Button : null;

        /// <summary>The viewport occupied by the scrollable Open Recent and Open Map lists. Exposed for tests.</summary>
        internal Rect MapListBounds => _mapScroll.ContentBounds;

        // ---- lifecycle ---------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override void OnEnter()
        {
            if (_built) return;
            BuildChrome();
            RebuildRecentButtons();
            QueryDiscoveredMaps();
            _wasTopOfStack = true;
            _built = true;
        }

        void BuildChrome()
        {
            _nameInput = new TextInput(default)
            {
                PlaceholderContent = LocalizedText.Raw("New map name..."),
                Style = GuiStyle.Modern,
            };
            _createButton = new Button(default, LocalizedText.Raw("Create"), null!, TryCreateMap)
            {
                Style = GuiStyle.Modern,
            };
            _quitButton = new Button(default, LocalizedText.Raw("Quit"), null!, RequestQuitLanding)
            {
                Style = GuiStyle.Modern,
            };
        }

        // Rebuild the recent-list buttons from the store, most-recent first. A missing file's button is greyed (see
        // MissingStyle) but stays enabled, so a click still runs ActivateRecent and prunes it. Called on enter and
        // after any store mutation (a Touch / prune), so the list always mirrors the store.
        void RebuildRecentButtons()
        {
            _recent.Clear();
            _lastSeenRecentPaths.Clear();
            if (_options.Recent is { } store)
            {
                foreach (string path in store.Paths)
                {
                    _lastSeenRecentPaths.Add(path);
                    bool exists = SafeExists(path);
                    var button = new Button(default, LocalizedText.Raw(FriendlyLabel(path)), null!, () => ActivateRecent(path))
                    {
                        Style = exists ? GuiStyle.Modern : MissingStyle,
                    };
                    _recent.Add(new MapEntry(button, path));
                }
            }
            // The Open Map filter (what counts as already-in-recents) is derived from this list, so any recents
            // rebuild must re-derive it too. This is IO-free (RebuildDiscoveredButtons only re-filters _discovered,
            // it never re-queries the head), so it is safe to run unconditionally here rather than trusting every
            // call site to remember a second call.
            RebuildDiscoveredButtons();
        }

        // The ONLY place DiscoverMaps is invoked. This is head file IO (a directory enumeration), so it must never
        // ride the per-frame path the way RefreshRecentIfChanged's store compare does: it runs once on OnEnter and
        // once per re-exposure (see RefreshDiscoveredOnReExpose), never on every driven frame. Clears and repopulates
        // _discovered: tolerates a null hook return, skips null/whitespace entries, dedupes ordinal, sorts, then
        // rebuilds the buttons (IO-free) over the fresh list.
        void QueryDiscoveredMaps()
        {
            _discovered.Clear();
            if (_options.DiscoverMaps is { } discover)
            {
                IReadOnlyList<string>? found = discover();
                if (found is not null)
                {
                    foreach (string path in found)
                    {
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        if (_discovered.Exists(p => string.Equals(p, path, StringComparison.Ordinal))) continue;
                        _discovered.Add(path);
                    }
                }
            }
            // Sort by file name first (OrdinalIgnoreCase), then full path (Ordinal) as a tiebreak. Filesystem
            // enumeration order is not stable across platforms or launches, and both comparisons are
            // culture-independent, so the order cannot shift with the machine's locale. The pair is a total order,
            // so two maps sharing a file name in different directories still keep a fixed relative position.
            _discovered.Sort((a, b) =>
            {
                int byName = string.Compare(FriendlyLabel(a), FriendlyLabel(b), StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.Compare(a, b, StringComparison.Ordinal);
            });
            RebuildDiscoveredButtons();
        }

        // Rebuild _discoveredButtons from _discovered: no IO. Filters out anything already in the recents list (so
        // a map renders exactly once, migrating into Open Recent the first time it is opened). Called after every
        // QueryDiscoveredMaps AND every RebuildRecentButtons: the filter depends on both the discovered list and the
        // recents list, so either input changing must re-derive it.
        void RebuildDiscoveredButtons()
        {
            _discoveredButtons.Clear();
            foreach (string path in _discovered)
            {
                if (IsInRecents(path)) continue;
                bool exists = SafeExists(path);
                var button = new Button(default, LocalizedText.Raw(FriendlyLabel(path)), null!, () => ActivateDiscovered(path))
                {
                    Style = exists ? GuiStyle.Modern : MissingStyle,
                };
                _discoveredButtons.Add(new MapEntry(button, path));
            }
        }

        bool IsInRecents(string path)
        {
            for (int i = 0; i < _recent.Count; i++)
                if (string.Equals(_recent[i].Path, path, StringComparison.Ordinal)) return true;
            return false;
        }

        // SceneManager gives this scene no re-exposure hook when the editor scene pushed on top of it later pops
        // back (only OnEnter/OnUpdate exist, and OnEnter runs once per push per the _built guard above), so a store
        // mutation made while this menu was not driving the actions itself would otherwise leave the Open Recent
        // list stale until the next Touch/ActivateRecent through this scene. Called every driven frame (cheap: at
        // most MaxPaths == 10 ordinal string compares) so the list self-heals as soon as this scene is updated again.
        void RefreshRecentIfChanged()
        {
            IReadOnlyList<string> live = _options.Recent?.Paths ?? Array.Empty<string>();
            if (RecentPathsMatchLastSeen(live)) return;
            RebuildRecentButtons();
        }

        bool RecentPathsMatchLastSeen(IReadOnlyList<string> live)
        {
            if (live.Count != _lastSeenRecentPaths.Count) return false;
            for (int i = 0; i < live.Count; i++)
                if (!string.Equals(live[i], _lastSeenRecentPaths[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // SceneManager has no re-exposure hook (only OnEnter/OnUpdate exist, and OnEnter runs once per push per the
        // _built guard), and the head's editor scene does not pass updates down (GameScene.UpdateBelow defaults
        // false), so this scene simply stops being updated while the editor sits above it. The observable edge is
        // therefore: this scene is the top of the stack again, and it was not the last time it looked, which is
        // exactly when a map created or deleted while this menu was not driving should appear. See OpenEditorFor for
        // why the flag must be cleared there rather than inferred here (SceneManager.Push during Update is deferred,
        // so Manager.Active cannot yet reflect a same-frame handover).
        void RefreshDiscoveredOnReExpose()
        {
            bool top = ReferenceEquals(Manager!.Active, this);
            if (top && !_wasTopOfStack) QueryDiscoveredMaps();
            _wasTopOfStack = top;
        }

        // ---- actions (the seam the buttons + Enter key both call, reachable headless) -------------------------

        // The shared success tail of every open path (create, recent, discovered): record the map as most-recent, rebuild
        // the lists over the new store contents (which is what migrates a discovered map into Open Recent), clear the
        // note, and push the head-built editor.
        void OpenTouchedMap(string path)
        {
            _options.Recent?.Touch(path);
            RebuildRecentButtons();
            _note = "";
            OpenEditorFor(path);
        }

        /// <summary>Create a map from the current name-field text (the Create button + Enter both call this).</summary>
        internal void TryCreateMap() => CreateMapNamed(_nameInput?.Text ?? "");

        /// <summary>Validate <paramref name="rawName"/> (non-empty after trim, no path separators), then call
        /// <see cref="MapEditorLandingOptions.CreateMap"/>: on a returned path, touch the store and push the editor.
        /// On a null return or an invalid name, leave an inline note and push nothing. Internal so the tests and the
        /// Create button / Enter key share one path.</summary>
        internal void CreateMapNamed(string rawName)
        {
            string name = (rawName ?? "").Trim();
            if (name.Length == 0) { _note = "Enter a map name"; return; }
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                _note = "Map name cannot contain a path separator";
                return;
            }
            if (_options.CreateMap is not { } create) { _note = "No create-map handler is wired"; return; }

            string? path = create(name);
            if (string.IsNullOrEmpty(path)) { _note = "Could not create map '" + name + "'"; return; }

            OpenTouchedMap(path);
        }

        /// <summary>Open a recent map: when the file exists, touch the store and push the editor. When it is missing,
        /// prune it from the store, re-query the discovered list (so a file that vanished drops out of Open Map too
        /// instead of reappearing there greyed), and leave a note instead of crashing (decision 6). Internal so a
        /// recent button's click and the tests share one path.</summary>
        internal void ActivateRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!SafeExists(path))
            {
                _options.Recent?.Remove(path);
                RebuildRecentButtons();
                QueryDiscoveredMaps();
                _note = "Map not found, removed from recents: " + path;
                return;
            }
            OpenTouchedMap(path);
        }

        /// <summary>Open a map surfaced by <see cref="MapEditorLandingOptions.DiscoverMaps"/>: touch the store and
        /// push the editor exactly like <see cref="ActivateRecent"/>, migrating it into Open Recent. When the file
        /// has vanished from disk since the last query (deleted outside the editor), there is no store entry to
        /// prune here, since the head owns the directory and not this scene, so the re-query itself IS the prune: it
        /// drops the path from the discovered list the same way <see cref="ActivateRecent"/>'s branch drops a
        /// vanished recent from the store. Internal so a discovered button's click and the tests share one path.</summary>
        internal void ActivateDiscovered(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!SafeExists(path))
            {
                QueryDiscoveredMaps();
                _note = "Map not found: " + path;
                return;
            }
            OpenTouchedMap(path);
        }

        /// <summary>Leave the menu via <see cref="MapEditorLandingOptions.RequestQuit"/>, or leave an inline note when
        /// none is wired (a scene never touches window APIs directly, decision 1). Internal so the Quit button and the
        /// tests share one path.</summary>
        internal void RequestQuitLanding()
        {
            if (_options.RequestQuit is { } quit) quit();
            else _note = "Quit is not available here";
        }

        // Build the head editor for a path and push it on top. The landing scene stays at the stack bottom so the
        // editor's Close pops back here. A null OpenEditor (or a hook returning null) leaves a note and pushes nothing.
        void OpenEditorFor(string path)
        {
            if (_options.OpenEditor is not { } open) { _note = "No open-editor handler is wired"; return; }
            GameScene? editor = open(path);
            if (editor is null) { _note = "Could not open map '" + path + "'"; return; }
            Manager?.Push(editor);
            // SceneManager.Push during Update is DEFERRED (applied only once the update pass finishes), so
            // Manager.Active still reports this scene for the rest of THIS frame even though the push has been
            // requested. Clear the flag explicitly here rather than relying on the reference compare in
            // RefreshDiscoveredOnReExpose, which could not otherwise observe the handover this frame.
            _wasTopOfStack = false;
        }

        // ---- per-frame ---------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override void OnUpdate(float dt)
        {
            if (!_built) return;
            UiViewport? ui = Manager!.UiViewport;
            if (ui is null) return;   // headless: no widget drive (actions stay reachable directly)

            RefreshDiscoveredOnReExpose();
            RefreshRecentIfChanged();
            _ui.Update(Manager.Input, ui);
            LandingLayout layout = ComputeLayout(ui.Width, ui.Height);
            _mapScroll.Update(_ui.Pointer, Manager.Input, dt);
            layout = ComputeLayout(ui.Width, ui.Height);

            _nameInput.Bounds = layout.NameField;
            bool nameFocused = _nameInput.Update(_ui.Pointer, Manager.Input, dt);
            // Enter while the name field is focused commits Create, mirroring how a text field's commit reads the
            // press edge (TextInput has no commit event of its own, so the scene watches the key).
            if (nameFocused && Manager.Input.WasPressed(Key.Enter)) TryCreateMap();

            _createButton.Bounds = layout.CreateButton;
            _createButton.Update(_ui.Pointer);

            // Snapshot the entries: a recent button's click runs ActivateRecent, which may RebuildRecentButtons and
            // replace the live list mid-loop, so iterate a copy and stop after the one tap a gesture can produce.
            MapEntry[] entries = _recent.ToArray();
            for (int i = 0; i < entries.Length && i < layout.RecentButtons.Count; i++)
            {
                if (UpdateClipped(entries[i].Button, layout.RecentButtons[i], layout.MapList)) break;
            }

            // Same snapshot-then-break idiom: a discovered button's click runs ActivateDiscovered, which can rebuild
            // _discoveredButtons mid-loop (a successful activation migrates the entry into Open Recent).
            MapEntry[] discovered = _discoveredButtons.ToArray();
            for (int i = 0; i < discovered.Length && i < layout.DiscoveredButtons.Count; i++)
            {
                if (UpdateClipped(discovered[i].Button, layout.DiscoveredButtons[i], layout.MapList)) break;
            }

            _quitButton.Bounds = layout.QuitButton;
            _quitButton.Update(_ui.Pointer);
        }

        bool UpdateClipped(Button button, Rect bounds, Rect clip)
        {
            float left = MathF.Max(bounds.X, clip.X);
            float top = MathF.Max(bounds.Y, clip.Y);
            float right = MathF.Min(bounds.Right, clip.Right);
            float bottom = MathF.Min(bounds.Bottom, clip.Bottom);
            button.Bounds = right > left && bottom > top
                ? new Rect(left, top, right - left, bottom - top)
                : new Rect(-1f, -1f, 0f, 0f);
            bool clicked = button.Update(_ui.Pointer);
            button.Bounds = bounds;
            return clicked;
        }

        // ---- draw ------------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override void OnDrawUi(SpriteBatch batch)
        {
            if (!_built || batch is null || _font is null || Manager is null) return;
            UiViewport? ui = Manager.UiViewport;
            if (ui is null) return;
            SpriteFont font = _font.For(ui.DpiScale);
            LandingLayout layout = ComputeLayout(ui.Width, ui.Height);

            batch.Draw(_white, new Vector4(0f, 0f, ui.Width, ui.Height), BackdropColor);
            batch.DrawRounded(_white, RectVec(layout.Panel), PanelBackground, PanelCornerRadius);

            DrawCentered(batch, font, ResolvedTitle(), layout.Title, TitleColor);
            DrawLeft(batch, font, "New map", layout.NewMapLabel, LabelColor);

            _nameInput.Bounds = layout.NameField;
            _nameInput.Font = font;
            _nameInput.Draw(batch, _white);

            _createButton.Bounds = layout.CreateButton;
            _createButton.Font = font;
            _createButton.Draw(batch, _white);

            _mapScroll.BeginClip(batch);
            DrawLeft(batch, font, "Open recent", layout.RecentLabel, LabelColor);
            if (_recent.Count == 0)
            {
                var placeholder = new Rect(layout.RecentLabel.X, layout.RecentLabel.Bottom, layout.RecentLabel.Width, MapRowHeight);
                DrawLeft(batch, font, "No recent maps", placeholder, PlaceholderColor);
            }
            else
            {
                for (int i = 0; i < _recent.Count && i < layout.RecentButtons.Count; i++)
                {
                    _recent[i].Button.Bounds = layout.RecentButtons[i];
                    _recent[i].Button.Font = font;
                    _recent[i].Button.Draw(batch, _white);
                }
            }

            if (_options.DiscoverMaps is not null)
            {
                DrawLeft(batch, font, "Open map", layout.DiscoveredLabel, LabelColor);
                if (_discoveredButtons.Count == 0)
                {
                    // "other" = not already sitting in Open Recent (that is this whole section's purpose), so an
                    // empty Open Map next to a populated Open Recent is the common case here, not a bug.
                    var placeholder = new Rect(layout.DiscoveredLabel.X, layout.DiscoveredLabel.Bottom, layout.DiscoveredLabel.Width, MapRowHeight);
                    DrawLeft(batch, font, "No other maps found", placeholder, PlaceholderColor);
                }
                else
                {
                    for (int i = 0; i < _discoveredButtons.Count && i < layout.DiscoveredButtons.Count; i++)
                    {
                        _discoveredButtons[i].Button.Bounds = layout.DiscoveredButtons[i];
                        _discoveredButtons[i].Button.Font = font;
                        _discoveredButtons[i].Button.Draw(batch, _white);
                    }
                }
            }
            _mapScroll.EndClip(batch);

            if (_note.Length > 0) DrawLeft(batch, font, _note, layout.Note, NoteColor);

            _quitButton.Bounds = layout.QuitButton;
            _quitButton.Font = font;
            _quitButton.Draw(batch, _white);
        }

        string ResolvedTitle()
        {
            string title = _options.Title.Resolve();
            return string.IsNullOrEmpty(title) ? "Map editor" : title;
        }

        static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, Rect box, Color color)
        {
            float textW = font.Measure(text).X;
            float x = box.X + MathF.Max(0f, (box.Width - textW) * 0.5f);
            float y = box.Y + (box.Height - font.LineHeight) * 0.5f;
            batch.DrawString(font, text, new Vector2(MathF.Floor(x), MathF.Floor(y)), color);
        }

        static void DrawLeft(SpriteBatch batch, SpriteFont font, string text, Rect box, Color color)
        {
            float y = box.Y + (box.Height - font.LineHeight) * 0.5f;
            batch.DrawString(font, text, new Vector2(MathF.Floor(box.X), MathF.Floor(y)), color);
        }

        static Vector4 RectVec(Rect r) => new(r.X, r.Y, r.Width, r.Height);

        // File.Exists never throws on a malformed path in .NET, but guard defensively so a stray recents entry can
        // never take the menu down: a path that cannot be probed reads as missing (and so gets pruned on a click).
        static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        static string FriendlyLabel(string path)
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        static GuiStyle BuildMissingStyle()
        {
            GuiStyle s = GuiStyle.Modern;
            s.Text = new Vector4(0.55f, 0.56f, 0.6f, 1f);      // greyed label
            s.Fill = new Vector4(0.12f, 0.13f, 0.16f, 0.9f);   // muted, inactive-looking fill
            s.Hover = s.Fill;                                   // no hover lift on a stale entry
            s.Press = s.Fill;
            return s;
        }

        // ---- layout ------------------------------------------------------------------------------------------

        // The menu rectangles for one frame, computed in point space. Internal so a headless test could assert the
        // layout, though the binding tests drive the actions rather than the pixels.
        readonly struct LandingLayout
        {
            public readonly Rect Panel, Title, NewMapLabel, NameField, CreateButton, MapList, RecentLabel,
                DiscoveredLabel, Note, QuitButton;
            public readonly IReadOnlyList<Rect> RecentButtons;
            public readonly IReadOnlyList<Rect> DiscoveredButtons;

            public LandingLayout(Rect panel, Rect title, Rect newMapLabel, Rect nameField, Rect createButton,
                Rect mapList, Rect recentLabel, IReadOnlyList<Rect> recentButtons, Rect discoveredLabel,
                IReadOnlyList<Rect> discoveredButtons, Rect note, Rect quitButton)
            {
                Panel = panel; Title = title; NewMapLabel = newMapLabel; NameField = nameField;
                CreateButton = createButton; MapList = mapList; RecentLabel = recentLabel; RecentButtons = recentButtons;
                DiscoveredLabel = discoveredLabel; DiscoveredButtons = discoveredButtons;
                Note = note; QuitButton = quitButton;
            }
        }

        LandingLayout ComputeLayout(float w, float h)
        {
            int recentCount = _recent.Count;
            // Reserve one row for the "No recent maps" placeholder when the list is empty, so the block below does
            // not jump between the empty and populated states.
            float recentBlockH = recentCount > 0
                ? recentCount * MapRowHeight + (recentCount - 1) * MapRowGap
                : MapRowHeight;

            // The whole Open Map block contributes 0 height when the head never wired DiscoverMaps, so an unwired
            // head's menu keeps exactly the layout it had before this feature. When wired, it reserves one row per
            // discovered button, or a single placeholder row when there are none (mirroring the recent block above,
            // same reason: the rows below must not jump as maps migrate into Open Recent).
            bool discoveredWired = _options.DiscoverMaps is not null;
            int discoveredCount = _discoveredButtons.Count;
            float discoveredRowsH = discoveredCount > 0
                ? discoveredCount * MapRowHeight + (discoveredCount - 1) * MapRowGap
                : MapRowHeight;
            float discoveredBlockH = 0f;
            if (discoveredWired)
                discoveredBlockH = SectionLabelHeight + discoveredRowsH + RowGap;

            float fixedTopH = TitleRowHeight + RowGap + SectionLabelHeight + FieldRowHeight + RowGap;
            float listContentH = SectionLabelHeight + recentBlockH + RowGap + discoveredBlockH;
            float fixedBottomH = NoteRowHeight + RowGap + QuitRowHeight;
            float panelH = MathF.Min(h, fixedTopH + listContentH + fixedBottomH + Pad * 2f);
            float panelW = MathF.Min(PanelWidth, w);
            float panelX = (w - panelW) * 0.5f;
            float panelY = MathF.Max(0f, (h - panelH) * 0.42f);   // a touch above center
            var panel = new Rect(panelX, panelY, panelW, panelH);

            float innerX = panelX + Pad;
            float innerW = MathF.Max(1f, panelW - Pad * 2f);
            float y = panelY + Pad;

            var title = new Rect(innerX, y, innerW, TitleRowHeight);
            y += TitleRowHeight + RowGap;

            var newMapLabel = new Rect(innerX, y, innerW, SectionLabelHeight);
            y += SectionLabelHeight;
            float createW = MathF.Min(CreateButtonWidth, innerW * 0.4f);
            var nameField = new Rect(innerX, y, MathF.Max(1f, innerW - createW - FieldButtonGap), FieldRowHeight);
            var createButton = new Rect(innerX + innerW - createW, y, createW, FieldRowHeight);
            y += FieldRowHeight + RowGap;

            var quitButton = new Rect(innerX, panel.Bottom - Pad - QuitRowHeight, innerW, QuitRowHeight);
            var note = new Rect(innerX, quitButton.Y - RowGap - NoteRowHeight, innerW, NoteRowHeight);
            var mapList = new Rect(innerX, y, innerW, MathF.Max(0f, note.Y - y));
            _mapScroll.Bounds = mapList;
            _mapScroll.ItemCount = 1;
            _mapScroll.ItemHeight = listContentH;
            _mapScroll.ScrollTo(_mapScroll.ScrollOffset);
            y -= _mapScroll.ScrollOffset;

            var recentLabel = new Rect(innerX, y, innerW, SectionLabelHeight);
            y += SectionLabelHeight;
            var recentButtons = new Rect[recentCount];
            for (int i = 0; i < recentCount; i++)
                recentButtons[i] = new Rect(innerX, y + i * (MapRowHeight + MapRowGap), innerW, MapRowHeight);
            y += recentBlockH + RowGap;

            Rect discoveredLabel = default;
            Rect[] discoveredButtons = Array.Empty<Rect>();
            if (discoveredWired)
            {
                discoveredLabel = new Rect(innerX, y, innerW, SectionLabelHeight);
                y += SectionLabelHeight;
                discoveredButtons = new Rect[discoveredCount];
                for (int i = 0; i < discoveredCount; i++)
                    discoveredButtons[i] = new Rect(innerX, y + i * (MapRowHeight + MapRowGap), innerW, MapRowHeight);
                y += discoveredRowsH;
                y += RowGap;
            }

            return new LandingLayout(panel, title, newMapLabel, nameField, createButton, mapList, recentLabel,
                recentButtons, discoveredLabel, discoveredButtons, note, quitButton);
        }
    }
}
