using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>The editor's own view preferences: the persisted <see cref="EditorSettings"/>, the modal settings menu
/// bare Escape opens, and the two things a setting actually drives - the host scene's environment (sky, lighting,
/// ocean) and the viewport's render distance.
/// <para>Render distance is the coupled one. The multiplier scales
/// <see cref="MapEditorOptions.RenderDistance"/> as one coherent set via
/// <see cref="RenderDistanceProfile.Scaled"/>, and every consumer of that set has to move with it: the viewport's
/// streamer ring and prop cull (baked at build time, so a change drives the existing rebuild path), the camera far
/// clip (an independent copy), and the tiled-document load window (read once at open time, so the multiplier is
/// loaded BEFORE the document is).</para></summary>
public partial class MapEditorScene
{
    // The live preferences. Owned by MapEditorOptions.Settings when a head wired a store, otherwise a session-only
    // instance so an embedder that never wants persistence still gets a working menu.
    EditorSettings _settings = new();

    // The modal settings menu (bare Escape), non-null only while it is open. Same ownership shape as _exitDialog:
    // OnUpdate gates every other editor step off it, it draws last, and its Close action nulls it.
    MapEditorSettingsDialog? _settingsDialog;

    // Set whenever the settings change (and on enter), so the environment apply runs on the next draw rather than
    // rewriting the whole post-process block every frame.
    bool _environmentDirty = true;

    // Whether the tool layer would have consumed THIS frame's bare Escape as a gesture cancel, sampled in OnUpdate
    // BEFORE the tool step runs. See the comment at the sample site: by the time HandleShortcuts runs the tool has
    // already cancelled, so the answer has to be captured ahead of it.
    bool _toolOwnsEscape;

    // The surf depth field and the two inputs it was built from. A rebuilt world hands out a NEW TerrainField
    // instance and a water-level edit changes the surface it is measured from, so those two are the whole
    // invalidation rule (a reference compare and a float compare per draw, only while surf is on).
    WaterBathymetry? _bathymetry;
    TerrainField? _bathymetrySource;
    float _bathymetryWaterLevel;

    /// <summary>The live editor settings. Exposed for tests (and for a head that wants to read the operator's
    /// current choices).</summary>
    internal EditorSettings Settings => _settings;

    /// <summary>The modal settings menu while it is open, or null when it is closed. Exposed for tests.</summary>
    internal MapEditorSettingsDialog? SettingsDialog => _settingsDialog;

    /// <summary>The render-distance profile the viewport and camera are actually using: the head's configured
    /// <see cref="MapEditorOptions.RenderDistance"/> scaled by the operator's multiplier. Exposed for tests.</summary>
    internal RenderDistanceProfile ScaledRenderDistance =>
        _options.RenderDistance.Scaled(_settings.RenderDistanceMultiplier);

    /// <summary>The render distance the viewport world is streaming and culling at. Exposed for tests, so the apply
    /// path can be asserted without a device.</summary>
    internal RenderDistanceProfile ViewportRenderDistance => _viewport.RenderDistance;

    /// <summary>The tile radius a windowed tiled document actually opens with: the head's configured
    /// <see cref="MapEditorOptions.EditorWindowRadius"/> scaled by the same multiplier the view radii take, rounded
    /// UP to whole tiles. This is the document-residency half of the render-distance set. Without it a 4x horizon
    /// would reach four times as far across a world whose loaded window never grew, so the far field would render
    /// unauthored ground right where the operator asked to see more. Rounding up matches
    /// <see cref="RenderDistanceProfile.Scaled"/>'s own chunk-radius rounding: residency may only grow.
    /// <para>Read once, when the document opens, since re-windowing a live document would mean reloading it and
    /// discarding unsaved edits. A multiplier changed mid-session therefore reports the window it could not grow
    /// (see <see cref="ApplyRenderDistance"/>) instead of silently under-loading.</para>
    /// Exposed for tests.</summary>
    internal int EffectiveWindowRadius =>
        (int)MathF.Ceiling(_options.EditorWindowRadius * _settings.RenderDistanceMultiplier);

    // Point the scene at the head's persisted settings, or at a session-only instance when no store is wired.
    // Runs FIRST in OnEnter, before the document is created: EffectiveWindowRadius reads the multiplier.
    void LoadSettings()
    {
        _settings = _options.Settings?.Settings ?? new EditorSettings();
        _settings.Sanitize();
        _environmentDirty = true;
        _bathymetry = null;
        _bathymetrySource = null;
    }

    /// <summary>Applies the operator's settings to <paramref name="post"/>: the sky / lighting preset with the sun
    /// and intensity overrides on top, and the ocean preset with the swell and foam overrides. Skipped entirely
    /// when <see cref="MapEditorOptions.DriveEnvironment"/> is false, so an embedding host keeps ownership of its
    /// own look.
    /// <para>Runs from <c>OnDraw3D</c> but does NOT rewrite the block every frame: it applies on the first draw and
    /// then only when a setting changed, or when the surf depth field it installed was replaced (a world rebuild
    /// hands out a new one). Internal rather than private so a headless test can drive it against a plain
    /// <see cref="PixelPostProcessSettings"/>, which is the only way to exercise it without a GPU device (a
    /// <see cref="Scene3D"/> cannot be constructed outside its own assembly).</para></summary>
    internal void ApplyEnvironment(PixelPostProcessSettings? post)
    {
        if (post is null || !_options.DriveEnvironment) return;
        WaterBathymetry? bathymetry = ResolveBathymetry();
        if (!_environmentDirty && ReferenceEquals(post.Water.Bathymetry, bathymetry)) return;
        _environmentDirty = false;
        MapEditorEnvironment.Apply(_settings, post, bathymetry);
    }

    // The depth field the water renderer shoals and breaks surf against, or null while surf is off. Built from the
    // SAME data the viewport already streams (its TerrainField and the document's water level over the document
    // bounds), so nothing new has to be plumbed through ViewportWorld. Rebuilt only when one of those two inputs
    // changed, which is what keeps a per-draw call cheap.
    WaterBathymetry? ResolveBathymetry()
    {
        if (!_settings.Surf)
        {
            _bathymetry = null;
            _bathymetrySource = null;
            return null;
        }

        // The viewport's field while the world is built, the controller's otherwise (a headless test points the
        // controller at a field without ever building a viewport).
        TerrainField? field = _viewport.Field ?? _controller.Field;
        if (field is null) return null;

        float waterLevel = _document.Doc.Terrain.WaterLevel;
        if (_bathymetry is not null && ReferenceEquals(_bathymetrySource, field)
            && _bathymetryWaterLevel.Equals(waterLevel))
            return _bathymetry;

        _bathymetrySource = field;
        _bathymetryWaterLevel = waterLevel;
        _bathymetry = MapEditorEnvironment.BuildBathymetry(_document.Doc.Bounds, field.SampleHeight, waterLevel);
        return _bathymetry;
    }

    /// <summary>Pushes <see cref="ScaledRenderDistance"/> into everything that reads a render distance: the
    /// viewport world (which streams and culls from it), the camera far clip (an independent copy of the same
    /// number), and the streamed ring itself.
    /// <para>The ring is the part that cannot be set: <c>ViewportWorld</c> reads its profile when it BUILDS (the
    /// streamer config and every prop layer's cull radius are baked there), so assigning the profile alone would
    /// widen the frustum and the ocean plane while the world quietly stayed the size it was. So a change to a built
    /// world drives <see cref="RebuildWorldForVisibility"/>, the same rebuild path the Layers panel uses, and pays
    /// its hitch. Before the first build this is a no-op beyond setting the profile, which is exactly right: the
    /// build that follows reads it.</para>
    /// <para>A tiled document that opened windowed is the one thing this cannot grow (see
    /// <see cref="EffectiveWindowRadius"/>), so it says so in the status strip rather than under-loading in
    /// silence.</para></summary>
    void ApplyRenderDistance()
    {
        RenderDistanceProfile profile = ScaledRenderDistance;
        if (_viewport.RenderDistance.Equals(profile)) return;

        _viewport.RenderDistance = profile;
        _camera.FarPlane = profile.FarClip;
        bool wasBuilt = _viewport.IsBuilt;
        RebuildWorldForVisibility();   // no-op until the world is built, which is why this is safe from OnEnter

        if (wasBuilt && _window is not null)
            _statusText = "Render distance changed. This document opened windowed, so the loaded tile window "
                + "grows on the next open, not now.";
    }

    // Bare Escape: open the settings menu. Reached only when the exit dialog and the menu are both closed (OnUpdate
    // gates on each), no editor field is focused (HandleShortcuts returns before this), and the tool layer had no
    // gesture to cancel with this same keypress.
    void OpenSettingsDialog()
    {
        _settingsDialog = new MapEditorSettingsDialog(_settings, OnSettingsChanged);
        _statusText = "";   // clear any stale status so the menu is the whole story
    }

    // Runs the menu for a frame while it is open (the OnUpdate gate routes here first), mirroring UpdateExitDialog:
    // the keyboard step runs headless, the pointer + widget step needs a live viewport. Keys go FIRST so a row
    // holding a live edit still gets Escape as its own cancel before the menu would take it as a dismiss.
    void UpdateSettingsDialog(float dt)
    {
        MapEditorSettingsDialog dialog = _settingsDialog!;
        dialog.HandleKeys(Manager!.Input);

        UiViewport? ui = Manager.UiViewport;
        if (ui is not null)
        {
            _ui.Update(Manager.Input, ui);
            dialog.Update(_ui, new Vector2(ui.Width, ui.Height), dt);
        }

        if (dialog.CloseRequested) CloseSettingsDialog();
    }

    void CloseSettingsDialog() => _settingsDialog = null;

    /// <summary>The settings menu's change hook: re-clamp (a scrubbed field is already range-limited, but Reset and
    /// a preset pick both rewrite several values at once), persist through the coalesced write queue, re-apply the
    /// render distance, and mark the environment for re-application on the next draw. Internal rather than private
    /// so a headless test can make a settings change take effect without driving the menu's widgets through a live
    /// viewport.</summary>
    internal void OnSettingsChanged()
    {
        _settings.Sanitize();
        _options.Settings?.Save();
        ApplyRenderDistance();
        _environmentDirty = true;
    }

    // Draws the menu over everything else, the exit dialog's slot in the chrome order. The two are mutually
    // exclusive (neither opens while the other is up), so they never stack.
    void DrawSettingsDialog(SpriteBatch batch, SpriteFont font, UiViewport ui) =>
        _settingsDialog?.Draw(batch, _white, font, new Vector2(ui.Width, ui.Height));
}
