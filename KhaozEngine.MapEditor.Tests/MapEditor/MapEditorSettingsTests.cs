using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Persistence;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>
    /// Headless tests for the editor settings menu: the persisted <see cref="EditorSettings"/> round trip (including
    /// sanitize-on-load, so a hand-edited file cannot crash the editor), the bare-Escape routing state machine, the
    /// live render-distance apply and its coupled reaches, and the environment application (Day by default, and the
    /// <see cref="MapEditorOptions.DriveEnvironment"/> opt-out leaving the host's post settings alone).
    /// <para>Uses the FakeScene idiom from <c>MapEditorSceneTests</c>: <see cref="MapEditorScene.BuildWorld"/> is
    /// overridden away so <see cref="MapEditorScene.OnEnter"/> runs with no device. The environment apply is driven
    /// through its <see cref="PixelPostProcessSettings"/> seam rather than <c>OnDraw3D</c>, since a
    /// <see cref="Scene3D"/> cannot be constructed outside its own assembly.</para>
    /// </summary>
    public class MapEditorSettingsTests
    {
        // The standard headless scene: no device work, a small valid document, and a flat terrain field on the
        // controller so the tool layer can actually run a gesture (and so the surf depth field has something to
        // sample). RebuildWorldForVisibility is counted rather than run, since there is no world to rebuild.
        class SettingsScene : MapEditorScene
        {
            public int Rebuilds;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => Doc();
            protected override void BuildWorld() =>
                Controller.Field = new TerrainField(new TerrainConfig { GentleAmplitude = 0f });
            protected override void TeardownWorld() { }
            protected override void RebuildWorldForVisibility() => Rebuilds++;
        }

        static MapDocument Doc() => new MapDocument
        {
            Id = "editor-settings",
            Bounds = new MapBounds { MinX = -64f, MinZ = -64f, MaxX = 64f, MaxZ = 64f },
        };

        static (SettingsScene Scene, SceneManager Manager) Push(MapEditorOptions options)
        {
            var scene = new SettingsScene();
            scene.Init(null!, null!, null!, options);
            var manager = new SceneManager();
            manager.Push(scene);
            scene.Rebuilds = 0;   // drop the enter-time apply so a test counts only what it drove
            return (scene, manager);
        }

        // A keyboard frame: the given keys fire their press edge this frame (and read as held), with an optional
        // shift modifier. The MapEditorSceneTests idiom.
        static InputState KeyFrame(bool shiftDown, params Key[] pressed)
        {
            var down = new HashSet<Key>(pressed);
            if (shiftDown) down.Add(Key.LeftShift);
            return new InputState(down, new HashSet<Key>(pressed), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 960, 540);
        }

        static InputState MouseFrame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 1200, 900);
        }

        static ChoiceRow ChoiceRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is ChoiceRow c && c.Label.Resolve() == label) return c;
            Assert.Fail($"no ChoiceRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;
        }

        static FloatRow FloatRowByLabel(PropertyGrid grid, string label)
        {
            foreach (PropertyRow row in grid.Rows)
                if (row is FloatRow f && f.Label.Resolve() == label) return f;
            Assert.Fail($"no FloatRow labeled '{label}' (rows: {grid.Rows.Count})");
            return null!;
        }

        // ---- persistence -------------------------------------------------------------------------------------

        [Fact]
        public void Settings_DefaultsMirrorTheDayAndModeratePresets()
        {
            // The defaults are DERIVED from the presets rather than typed out again, so a fresh settings file shows
            // exactly the preset the editor claims to default to. Proven by applying the presets independently.
            var post = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Day, post);
            var water = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Moderate, water);

            var settings = new EditorSettings();

            Assert.Equal(EnvironmentPresetKind.Day, settings.Environment);
            Assert.Equal(OceanPresetKind.Moderate, settings.Ocean);
            Assert.Equal(1f, settings.RenderDistanceMultiplier);
            Assert.Equal(water.SwellAmplitude, settings.SwellAmplitude, 4);
            Assert.Equal(water.FoamStrength, settings.FoamStrength, 4);
            Assert.False(settings.Surf);

            // The stored angles must reproduce the preset's own key-light direction to within rounding.
            Vector3 fromAngles = EnvironmentPresets.SunLightDirection(
                settings.SunAzimuthDegrees, settings.SunElevationDegrees);
            Assert.True(Vector3.Distance(fromAngles, Vector3.Normalize(post.LightDirection)) < 1e-4f,
                $"round-tripped sun direction {fromAngles} does not match the Day preset's {post.LightDirection}");
        }

        [Fact]
        public void Settings_PersistThroughAStoreAndSurviveAFreshInstance()
        {
            AppDataPaths paths = TempPaths(out string root);
            var queue = new PersistenceQueue();
            try
            {
                var writer = new EditorSettingsStore(new FileSettingsStorage(paths, queue), queue);
                writer.Settings.RenderDistanceMultiplier = 4f;
                writer.Settings.SelectEnvironment(EnvironmentPresetKind.Sunset);
                writer.Settings.KeyLightIntensity = 1.5f;
                writer.Settings.SelectOcean(OceanPresetKind.Rough);
                writer.Settings.FoamStrength = 0.25f;
                writer.Settings.Surf = true;
                writer.Save();
                writer.Flush();   // the store drains its own injected queue

                var reader = new EditorSettingsStore(new FileSettingsStorage(paths, queue));

                Assert.Equal(4f, reader.Settings.RenderDistanceMultiplier);
                Assert.Equal(EnvironmentPresetKind.Sunset, reader.Settings.Environment);
                Assert.Equal(1.5f, reader.Settings.KeyLightIntensity, 4);
                Assert.Equal(OceanPresetKind.Rough, reader.Settings.Ocean);
                Assert.Equal(0.25f, reader.Settings.FoamStrength, 4);
                Assert.True(reader.Settings.Surf);

                // The editor preferences ride their own file, not the game's settings.json and not the recents.
                Assert.True(File.Exists(paths.GetFilePath(EditorSettingsStore.FileName)));
                Assert.False(File.Exists(paths.GetFilePath("settings.json")));
                Assert.False(File.Exists(paths.GetFilePath(EditorRecentFiles.FileName)));
            }
            finally
            {
                queue.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Settings_GarbageOnDiskSanitizesToUsableValuesOnLoad()
        {
            AppDataPaths paths = TempPaths(out string root);
            var queue = new PersistenceQueue();
            try
            {
                // A hand-edited file: an in-between multiplier, out-of-range angles and intensities, negative
                // ocean values, and enum values no build ever defined.
                string json = """
                {
                  "RenderDistanceMultiplier": 3.4,
                  "Environment": 99,
                  "SunAzimuthDegrees": -720,
                  "SunElevationDegrees": 4000,
                  "KeyLightIntensity": 12,
                  "AmbientIntensity": -3,
                  "Ocean": -7,
                  "SwellAmplitude": 900,
                  "FoamStrength": -5
                }
                """;
                Directory.CreateDirectory(Path.GetDirectoryName(paths.GetFilePath(EditorSettingsStore.FileName))!);
                File.WriteAllText(paths.GetFilePath(EditorSettingsStore.FileName), json);

                EditorSettings loaded = new EditorSettingsStore(new FileSettingsStorage(paths, queue)).Settings;

                Assert.Equal(4f, loaded.RenderDistanceMultiplier);   // 3.4 snaps to the nearest offered tier
                Assert.Equal(EditorSettings.DefaultEnvironment, loaded.Environment);
                Assert.Equal(EditorSettings.DefaultOcean, loaded.Ocean);
                Assert.Equal(0f, loaded.SunAzimuthDegrees);
                Assert.Equal(EditorSettings.MaxSunElevationDegrees, loaded.SunElevationDegrees);
                Assert.Equal(EditorSettings.MaxLightIntensity, loaded.KeyLightIntensity);
                Assert.Equal(0f, loaded.AmbientIntensity);
                Assert.Equal(EditorSettings.MaxSwellAmplitude, loaded.SwellAmplitude);
                Assert.Equal(0f, loaded.FoamStrength);

                // And the sanitized values are all usable: applying them throws nothing and leaves a lit scene.
                var post = new PixelPostProcessSettings();
                MapEditorEnvironment.Apply(loaded, post, null);
                Assert.True(post.Sky.Enabled);
            }
            finally
            {
                queue.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Settings_NonFiniteValuesFallBackToThePresetsOwnValues()
        {
            // Math.Clamp propagates NaN, so a NaN slider has to be caught explicitly or it would poison the whole
            // scene's lighting. Each one falls back to the value its (known-good) preset carries.
            var settings = new EditorSettings
            {
                RenderDistanceMultiplier = float.NaN,
                SunAzimuthDegrees = float.NaN,
                SunElevationDegrees = float.PositiveInfinity,
                KeyLightIntensity = float.NaN,
                SwellAmplitude = float.NegativeInfinity,
            };

            settings.Sanitize();

            var fresh = new EditorSettings();
            Assert.Equal(1f, settings.RenderDistanceMultiplier);
            Assert.Equal(fresh.SunAzimuthDegrees, settings.SunAzimuthDegrees, 4);
            // Infinity is no more informative than NaN, so it takes the preset value too rather than clamping to
            // the range end (a finite out-of-range value DOES clamp, see the garbage-file test above).
            Assert.Equal(fresh.SunElevationDegrees, settings.SunElevationDegrees, 4);
            Assert.Equal(1f, settings.KeyLightIntensity);
            Assert.Equal(fresh.SwellAmplitude, settings.SwellAmplitude, 4);
        }

        [Fact]
        public void Settings_PickingAPresetResetsThatSectionsSliders()
        {
            // The slider semantics: a preset pick RESETS its own section to the preset's values, and the sliders
            // then adjust from there, so a Night sun angle never survives onto a Day sky.
            var settings = new EditorSettings();
            settings.SunAzimuthDegrees = 12f;
            settings.KeyLightIntensity = 0.2f;
            settings.SwellAmplitude = 2.9f;

            settings.SelectEnvironment(EnvironmentPresetKind.Night);
            settings.SelectOcean(OceanPresetKind.Rough);

            var post = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Night, post);
            var water = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Rough, water);

            Assert.Equal(1f, settings.KeyLightIntensity);
            Assert.Equal(1f, settings.AmbientIntensity);
            Assert.Equal(water.SwellAmplitude, settings.SwellAmplitude, 4);
            Assert.Equal(water.FoamStrength, settings.FoamStrength, 4);
            Vector3 fromAngles = EnvironmentPresets.SunLightDirection(
                settings.SunAzimuthDegrees, settings.SunElevationDegrees);
            Assert.True(Vector3.Distance(fromAngles, Vector3.Normalize(post.LightDirection)) < 1e-4f);
        }

        // ---- environment application -------------------------------------------------------------------------

        [Fact]
        public void Environment_FreshSettingsPaintTheDaySkyOnTheFirstApply()
        {
            // The horizon-artifact fix: SkySettings.Enabled defaults to false and Starfield to true, so an editor
            // that never touched Post drew terrain against a space backdrop. A fresh editor now shows Day.
            (SettingsScene scene, _) = Push(new MapEditorOptions());
            var post = new PixelPostProcessSettings();
            Assert.True(post.Starfield);        // the engine default this replaces
            Assert.False(post.Sky.Enabled);

            scene.ApplyEnvironment(post);

            Assert.True(post.Sky.Enabled);
            Assert.False(post.Starfield);
            Assert.True(post.Sky.SunEnabled);
        }

        [Fact]
        public void Environment_DriveEnvironmentFalseLeavesThePostSettingsUntouched()
        {
            (SettingsScene scene, _) = Push(new MapEditorOptions { DriveEnvironment = false });
            var post = new PixelPostProcessSettings();
            bool starfield = post.Starfield;
            bool sky = post.Sky.Enabled;
            float swell = post.Water.SwellAmplitude;

            scene.ApplyEnvironment(post);

            Assert.Equal(starfield, post.Starfield);
            Assert.Equal(sky, post.Sky.Enabled);
            Assert.Equal(swell, post.Water.SwellAmplitude);
            Assert.Null(post.Water.Bathymetry);
        }

        [Fact]
        public void Environment_ReAppliesOnlyAfterASettingChanges()
        {
            // The apply is dirty-gated: it must not rewrite the whole post block every frame. Proven by scribbling
            // on Post after an apply and watching the scribble survive until a change marks it dirty again.
            (SettingsScene scene, _) = Push(new MapEditorOptions());
            var post = new PixelPostProcessSettings();
            scene.ApplyEnvironment(post);

            post.Starfield = true;   // stand-in for "something else wrote to Post"
            scene.ApplyEnvironment(post);
            Assert.True(post.Starfield);   // not re-applied

            scene.Settings.SelectEnvironment(EnvironmentPresetKind.Sunset);
            scene.OnSettingsChanged();
            scene.ApplyEnvironment(post);

            Assert.False(post.Starfield);   // re-applied once the setting changed
        }

        [Fact]
        public void Environment_SliderOverridesLandOnTopOfThePreset()
        {
            (SettingsScene scene, _) = Push(new MapEditorOptions());
            var preset = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Day, preset);
            var presetWater = new WaterSettings();
            OceanPresets.Apply(OceanPresetKind.Moderate, presetWater);

            scene.Settings.SunAzimuthDegrees = 20f;
            scene.Settings.SunElevationDegrees = 30f;
            scene.Settings.KeyLightIntensity = 0.5f;
            scene.Settings.AmbientIntensity = 2f;
            scene.Settings.SwellAmplitude = 1.75f;
            scene.Settings.FoamStrength = 0.2f;
            scene.OnSettingsChanged();

            var post = new PixelPostProcessSettings();
            scene.ApplyEnvironment(post);

            Assert.Equal(EnvironmentPresets.SunLightDirection(20f, 30f), post.LightDirection);
            Assert.Equal(preset.LightColor.R * 0.5f, post.LightColor.R, 4);
            Assert.Equal(preset.AmbientColor.G * 2f, post.AmbientColor.G, 4);
            Assert.Equal(1.75f, post.Water.SwellAmplitude, 4);
            Assert.Equal(0.2f, post.Water.FoamStrength, 4);
            // The rest of the ocean bundle still comes from the preset, untouched by the two sliders.
            Assert.Equal(presetWater.SwellWavelength, post.Water.SwellWavelength, 4);
            Assert.Equal(presetWater.GlintStrength, post.Water.GlintStrength, 4);
        }

        [Fact]
        public void Environment_SurfWiresADepthFieldFromTheDocumentTerrain()
        {
            (SettingsScene scene, _) = Push(new MapEditorOptions());
            scene.Document.Doc.Terrain.WaterLevel = 6f;
            var post = new PixelPostProcessSettings();

            scene.ApplyEnvironment(post);
            Assert.Null(post.Water.Bathymetry);   // off by default: surf stays inert

            scene.Settings.Surf = true;
            scene.OnSettingsChanged();
            scene.ApplyEnvironment(post);

            WaterBathymetry? bathymetry = post.Water.Bathymetry;
            Assert.NotNull(bathymetry);
            // The document is 128 m square over a flat field at height 0, so every texel reads the water level as
            // its depth, and the field covers the document rather than some default rectangle.
            Assert.Equal(64f, bathymetry!.HalfExtentX, 3);
            Assert.Equal(64f, bathymetry.HalfExtentZ, 3);
            Assert.All(bathymetry.Depths, d => Assert.Equal(6f, d, 3));

            scene.Settings.Surf = false;
            scene.OnSettingsChanged();
            scene.ApplyEnvironment(post);
            Assert.Null(post.Water.Bathymetry);   // turning it off clears the field again
        }

        // ---- render distance ---------------------------------------------------------------------------------

        [Fact]
        public void RenderDistance_MultiplierScalesTheProfileTheFarPlaneAndTheWindowReach()
        {
            var options = new MapEditorOptions();
            (SettingsScene scene, _) = Push(options);
            RenderDistanceProfile expected = options.RenderDistance.Scaled(2f);

            scene.Settings.RenderDistanceMultiplier = 2f;
            scene.OnSettingsChanged();

            // The viewport streams and culls from the scaled set...
            Assert.Equal(expected, scene.ViewportRenderDistance);
            // ...the camera's independent far-clip copy moves with it...
            Assert.Equal(expected.FarClip, scene.Camera.FarPlane);
            // ...and the world is actually REBUILT, because ViewportWorld bakes its ring and prop cull at build
            // time: setting the profile alone would widen the frustum over a world that never grew (#363).
            Assert.Equal(1, scene.Rebuilds);
            // The document-residency reach scales too, so a wider horizon loads a wider slice of a tiled world.
            Assert.Equal(options.EditorWindowRadius * 2, scene.EffectiveWindowRadius);
        }

        [Fact]
        public void RenderDistance_BaseMultiplierLeavesTheHeadsProfileExactlyAsConfigured()
        {
            var options = new MapEditorOptions { RenderDistance = RenderDistanceProfile.For(RenderDistanceTier.Near) };
            (SettingsScene scene, _) = Push(options);

            Assert.Equal(options.RenderDistance, scene.ViewportRenderDistance);
            Assert.Equal(options.RenderDistance.FarClip, scene.Camera.FarPlane);
            Assert.Equal(options.EditorWindowRadius, scene.EffectiveWindowRadius);
            Assert.Equal(0, scene.Rebuilds);   // nothing changed, so nothing was rebuilt
        }

        [Fact]
        public void RenderDistance_PersistedMultiplierAppliesBeforeTheFirstBuild()
        {
            // The multiplier is loaded ahead of the document and the world, so the first ring is primed at the
            // right size instead of being built small and immediately rebuilt.
            var storage = new InMemorySettingsStorage();
            var store = new EditorSettingsStore(storage);
            store.Settings.RenderDistanceMultiplier = 4f;
            store.Save();

            var options = new MapEditorOptions { Settings = new EditorSettingsStore(storage) };
            (SettingsScene scene, _) = Push(options);

            Assert.Equal(options.RenderDistance.Scaled(4f), scene.ViewportRenderDistance);
            Assert.Equal(options.RenderDistance.Scaled(4f).FarClip, scene.Camera.FarPlane);
            Assert.Equal(options.EditorWindowRadius * 4, scene.EffectiveWindowRadius);
        }

        [Fact]
        public void RenderDistance_ScaledSetStaysCoherentAtEveryOfferedTier()
        {
            // The whole point of scaling as a SET: the frustum must never reach past the terrain the viewport
            // streams, at any tier the menu offers.
            var options = new MapEditorOptions();
            foreach (float multiplier in EditorSettings.RenderDistanceMultipliers)
            {
                (SettingsScene scene, _) = Push(options);
                scene.Settings.RenderDistanceMultiplier = multiplier;
                scene.OnSettingsChanged();

                RenderDistanceProfile p = scene.ViewportRenderDistance;
                p.Validate();   // throws if the set is incoherent
                Assert.True(scene.Camera.FarPlane <= p.DecorRadiusMeters,
                    $"{multiplier}x: far clip {scene.Camera.FarPlane} m reaches past the {p.DecorRadiusMeters} m far field");
            }
        }

        [Fact]
        public void SettingsChange_PersistsThroughTheWiredStore()
        {
            var storage = new InMemorySettingsStorage();
            var options = new MapEditorOptions { Settings = new EditorSettingsStore(storage) };
            (SettingsScene scene, _) = Push(options);

            scene.Settings.SelectOcean(OceanPresetKind.Calm);
            scene.OnSettingsChanged();

            Assert.Equal(OceanPresetKind.Calm, new EditorSettingsStore(storage).Settings.Ocean);
        }

        // ---- Escape routing ----------------------------------------------------------------------------------

        [Fact]
        public void Escape_WithNothingToCancel_OpensTheSettingsMenu()
        {
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            Assert.Equal(EditorToolMode.Select, scene.Controller.Mode);

            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);

            Assert.NotNull(scene.SettingsDialog);
            Assert.Null(scene.ExitDialog);
        }

        [Fact]
        public void Escape_WithAToolGestureActive_CancelsInsteadOfOpeningTheMenu()
        {
            // The ordering trap: the tool step runs BEFORE the shortcut step and cancels the gesture, so asking the
            // controller at shortcut time would always read "nothing active". A gesture-cancelling Escape must not
            // also pop the menu.
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            scene.Controller.Mode = EditorToolMode.DrawExclusion;

            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);

            Assert.Null(scene.SettingsDialog);
            Assert.Equal(EditorToolMode.Select, scene.Controller.Mode);   // the tool really did cancel

            // The NEXT Escape, with nothing left to cancel, opens the menu.
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            Assert.NotNull(scene.SettingsDialog);
        }

        [Fact]
        public void ShiftEscape_StillOpensTheExitDialog_NotTheSettingsMenu()
        {
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());

            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);

            Assert.NotNull(scene.ExitDialog);
            Assert.Null(scene.SettingsDialog);
        }

        [Fact]
        public void Escape_WhileTheExitDialogIsOpen_NeverOpensTheSettingsMenu()
        {
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            m.Input = KeyFrame(shiftDown: true, Key.Escape);
            m.Update(0.016f);
            Assert.NotNull(scene.ExitDialog);

            m.Input = KeyFrame(shiftDown: false, Key.Escape);   // the exit dialog's own Cancel edge
            m.Update(0.016f);

            Assert.Null(scene.ExitDialog);          // dismissed by its Cancel action
            Assert.Null(scene.SettingsDialog);      // and the same keypress did not leak into the menu
        }

        [Fact]
        public void Escape_WhileTheSettingsMenuIsOpen_ClosesIt()
        {
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            Assert.NotNull(scene.SettingsDialog);

            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);

            Assert.Null(scene.SettingsDialog);
        }

        [Fact]
        public void SettingsMenu_WhileOpen_FreezesTheEditorBeneathIt()
        {
            // The modal gate, same as the exit dialog's: no chord, tool pick, or camera step reaches the editor.
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            Assert.NotNull(scene.SettingsDialog);

            m.Input = KeyFrame(shiftDown: false, Key.D1);   // a bookmark chord the editor would otherwise take
            m.Update(0.016f);

            Assert.NotNull(scene.SettingsDialog);   // still open: the chord never reached HandleShortcuts
            Assert.Equal("", scene.StatusText);   // no "Bookmark 1 is empty" note, so the chord never ran
        }

        [Fact]
        public void SettingsMenu_CloseAction_DismissesIt()
        {
            (SettingsScene scene, SceneManager m) = Push(new MapEditorOptions());
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            MapEditorSettingsDialog dialog = scene.SettingsDialog!;

            dialog.CloseButton.OnClick!.Invoke();   // the footer action, with no live viewport needed to click it

            m.Input = InputState.Empty;
            m.Update(0.016f);
            Assert.Null(scene.SettingsDialog);
        }

        // ---- the menu's own rows -----------------------------------------------------------------------------

        [Fact]
        public void SettingsMenu_RowsReadAndWriteTheLiveSettings()
        {
            var settings = new EditorSettings();
            int changes = 0;
            var dialog = new MapEditorSettingsDialog(settings, () => changes++);

            ChoiceRow distance = ChoiceRowByLabel(dialog.Grid, "Render distance");
            Assert.Equal("Base", distance.Selected);
            Assert.Equal(EnvironmentPresetKind.Day.ToString(),
                ChoiceRowByLabel(dialog.Grid, "Sky preset").Selected);
            Assert.Equal(settings.SwellAmplitude,
                FloatRowByLabel(dialog.Grid, "Swell amplitude").Field.Value, 4);

            dialog.ResetButton.OnClick!.Invoke();
            Assert.Equal(1, changes);   // Reset raises the change hook so the host persists and re-applies
        }

        [Fact]
        public void SettingsMenu_PickingARenderDistanceOptionDrivesTheSceneEndToEnd()
        {
            // The full wiring, driven through the real dropdown rather than the internal hook: a pick writes the
            // setting, raises the change hook, and the scene re-applies its render distance.
            var options = new MapEditorOptions();
            (SettingsScene scene, SceneManager m) = Push(options);
            m.Input = KeyFrame(shiftDown: false, Key.Escape);
            m.Update(0.016f);
            MapEditorSettingsDialog dialog = scene.SettingsDialog!;

            var ui = new InputManager();
            var viewport = new Vector2(1200f, 900f);
            var away = new Vector2(-100f, -100f);
            ui.Update(MouseFrame(away, leftDown: false));
            dialog.Update(ui, viewport, 0.016f);   // one frame to establish the grid bounds

            int rowIndex = dialog.Grid.Rows.IndexOf(ChoiceRowByLabel(dialog.Grid, "Render distance"));
            Rect trigger = dialog.Grid.RowEditorBounds(rowIndex);
            var triggerCenter = new Vector2(trigger.X + trigger.Width * 0.5f, trigger.Y + trigger.Height * 0.5f);
            Tap(dialog, ui, viewport, triggerCenter);   // open the list
            // Dropdown stacks its options directly below the trigger, one trigger-height each. Index 2 is "4x".
            Tap(dialog, ui, viewport, new Vector2(triggerCenter.X, trigger.Bottom + trigger.Height * 2.5f));

            Assert.Equal(4f, scene.Settings.RenderDistanceMultiplier);
            Assert.Equal(options.RenderDistance.Scaled(4f), scene.ViewportRenderDistance);
            Assert.Equal(1, scene.Rebuilds);
        }

        // A press-origin tap driven through the dialog (press and release both at `at`), the PropertyGridTests idiom.
        static void Tap(MapEditorSettingsDialog dialog, InputManager ui, Vector2 viewport, Vector2 at)
        {
            ui.Update(MouseFrame(at, leftDown: false)); dialog.Update(ui, viewport, 0.016f);
            ui.Update(MouseFrame(at, leftDown: true)); dialog.Update(ui, viewport, 0.016f);
            ui.Update(MouseFrame(at, leftDown: false)); dialog.Update(ui, viewport, 0.016f);
        }

        static AppDataPaths TempPaths(out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "ke-editor-settings-" + Path.GetRandomFileName());
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            return new AppDataPaths("APKiwi", "EditorSettingsTest", env);
        }
    }
}
