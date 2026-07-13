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

        /// <summary>How the menu leaves the app (the head's quit path, e.g. a <c>GameApp</c> subclass calling the
        /// protected <c>Quit()</c>). Null leaves the Quit button a no-op with an inline note, since a scene never
        /// touches window APIs directly (decision 1).</summary>
        public Action? RequestQuit;
    }

    /// <summary>The turn-key entry menu a per-game editor head pushes as the bottom scene on its
    /// <see cref="SceneManager"/> (decision 6): a title, a New Map row (a name field and a Create button), an Open
    /// Recent list (one button per recent path, most-recent first, missing files greyed and pruned on an activation
    /// attempt), and a Quit button. Creating or opening a map pushes the head-built editor on top and leaves this
    /// scene at the stack bottom, so the editor's Close pops back here (decision 1). Only 2D chrome (no 3D pass, so
    /// it does not implement <c>IGameScene3D</c>). Developer tooling, so the whole class is
    /// <see cref="LocalizationExemptAttribute">localization-exempt</see>. The widget drive is viewport-gated, so the
    /// scene runs headless (its create / activate / quit actions are reachable without a live viewport).</summary>
    [LocalizationExempt]
    public sealed class MapEditorLandingScene : GameScene
    {
        const float PanelWidth = 460f;
        const float Pad = 22f;
        const float TitleRowHeight = 40f;
        const float SectionLabelHeight = 22f;
        const float FieldRowHeight = 34f;
        const float RecentRowHeight = 32f;
        const float RowGap = 8f;
        const float RecentGap = 6f;
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

        // A recent-list button whose file is missing: greyed text over a muted fill so it reads inactive, yet still
        // Enabled so a click runs ActivateRecent (which prunes it). The disabled visual can't do double duty here,
        // since a disabled Button never fires its OnClick (decision 6 wants the click to prune, not just disable).
        static readonly GuiStyle MissingStyle = BuildMissingStyle();

        // One recent-list entry: the button and the full path it activates (the button's label shows only the file
        // name). The path is the identity ActivateRecent / the store key on, not the shortened label.
        readonly struct RecentEntry
        {
            public readonly Button Button;
            public readonly string Path;
            public RecentEntry(Button button, string path) { Button = button; Path = path; }
        }

        Texture2D _white = null!;
        DpiFont _font = null!;
        MapEditorLandingOptions _options = null!;

        readonly InputManager _ui = new();
        TextInput _nameInput = null!;
        Button _createButton = null!;
        Button _quitButton = null!;
        readonly List<RecentEntry> _recent = new();

        string _note = "";
        bool _built;

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

        // ---- lifecycle ---------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override void OnEnter()
        {
            if (_built) return;
            BuildChrome();
            RebuildRecentButtons();
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
            if (_options.Recent is not { } store) return;
            foreach (string path in store.Paths)
            {
                bool exists = SafeExists(path);
                var button = new Button(default, LocalizedText.Raw(FriendlyLabel(path)), null!, () => ActivateRecent(path))
                {
                    Style = exists ? GuiStyle.Modern : MissingStyle,
                };
                _recent.Add(new RecentEntry(button, path));
            }
        }

        // ---- actions (the seam the buttons + Enter key both call, reachable headless) -------------------------

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

            _options.Recent?.Touch(path);
            RebuildRecentButtons();
            _note = "";
            OpenEditorFor(path);
        }

        /// <summary>Open a recent map: when the file exists, touch the store and push the editor. When it is missing,
        /// prune it from the store and leave a note instead of crashing (decision 6). Internal so a recent button's
        /// click and the tests share one path.</summary>
        internal void ActivateRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!SafeExists(path))
            {
                _options.Recent?.Remove(path);
                RebuildRecentButtons();
                _note = "Map not found, removed from recents: " + path;
                return;
            }
            _options.Recent?.Touch(path);
            RebuildRecentButtons();
            _note = "";
            OpenEditorFor(path);
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
        }

        // ---- per-frame ---------------------------------------------------------------------------------------

        /// <inheritdoc/>
        public override void OnUpdate(float dt)
        {
            if (!_built) return;
            UiViewport? ui = Manager!.UiViewport;
            if (ui is null) return;   // headless: no widget drive (actions stay reachable directly)

            _ui.Update(Manager.Input, ui);
            LandingLayout layout = ComputeLayout(ui.Width, ui.Height);

            _nameInput.Bounds = layout.NameField;
            bool nameFocused = _nameInput.Update(_ui.Pointer, Manager.Input, dt);
            // Enter while the name field is focused commits Create, mirroring how a text field's commit reads the
            // press edge (TextInput has no commit event of its own, so the scene watches the key).
            if (nameFocused && Manager.Input.WasPressed(Key.Enter)) TryCreateMap();

            _createButton.Bounds = layout.CreateButton;
            _createButton.Update(_ui.Pointer);

            // Snapshot the entries: a recent button's click runs ActivateRecent, which may RebuildRecentButtons and
            // replace the live list mid-loop, so iterate a copy and stop after the one tap a gesture can produce.
            RecentEntry[] entries = _recent.ToArray();
            for (int i = 0; i < entries.Length && i < layout.RecentButtons.Count; i++)
            {
                entries[i].Button.Bounds = layout.RecentButtons[i];
                if (entries[i].Button.Update(_ui.Pointer)) break;
            }

            _quitButton.Bounds = layout.QuitButton;
            _quitButton.Update(_ui.Pointer);
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

            DrawLeft(batch, font, "Open recent", layout.RecentLabel, LabelColor);
            if (_recent.Count == 0)
            {
                var placeholder = new Rect(layout.RecentLabel.X, layout.RecentLabel.Bottom, layout.RecentLabel.Width, RecentRowHeight);
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
            public readonly Rect Panel, Title, NewMapLabel, NameField, CreateButton, RecentLabel, Note, QuitButton;
            public readonly IReadOnlyList<Rect> RecentButtons;

            public LandingLayout(Rect panel, Rect title, Rect newMapLabel, Rect nameField, Rect createButton,
                Rect recentLabel, IReadOnlyList<Rect> recentButtons, Rect note, Rect quitButton)
            {
                Panel = panel; Title = title; NewMapLabel = newMapLabel; NameField = nameField;
                CreateButton = createButton; RecentLabel = recentLabel; RecentButtons = recentButtons;
                Note = note; QuitButton = quitButton;
            }
        }

        LandingLayout ComputeLayout(float w, float h)
        {
            int recentCount = _recent.Count;
            // Reserve one row for the "No recent maps" placeholder when the list is empty, so the Quit button below
            // does not jump between the empty and populated states.
            float recentBlockH = recentCount > 0
                ? recentCount * RecentRowHeight + (recentCount - 1) * RecentGap
                : RecentRowHeight;

            float contentH =
                TitleRowHeight + RowGap +
                SectionLabelHeight + FieldRowHeight + RowGap +
                SectionLabelHeight + recentBlockH + RowGap +
                NoteRowHeight + RowGap +
                QuitRowHeight;
            float panelH = contentH + Pad * 2f;
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

            var recentLabel = new Rect(innerX, y, innerW, SectionLabelHeight);
            y += SectionLabelHeight;
            var recentButtons = new Rect[recentCount];
            for (int i = 0; i < recentCount; i++)
                recentButtons[i] = new Rect(innerX, y + i * (RecentRowHeight + RecentGap), innerW, RecentRowHeight);
            y += recentBlockH + RowGap;

            var note = new Rect(innerX, y, innerW, NoteRowHeight);
            y += NoteRowHeight + RowGap;

            var quitButton = new Rect(innerX, y, innerW, QuitRowHeight);

            return new LandingLayout(panel, title, newMapLabel, nameField, createButton, recentLabel, recentButtons, note, quitButton);
        }
    }
}
