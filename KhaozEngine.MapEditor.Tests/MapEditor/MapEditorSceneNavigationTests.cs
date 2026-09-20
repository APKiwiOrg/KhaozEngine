using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    public sealed class MapEditorSceneNavigationTests
    {
        readonly MouseFrames _mouse = new();

        [Fact]
        public void NavigationDoesNotAcquireOverChromeOrFocusedText()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector3 beforeChrome = scene.Camera.Position;

            Step(manager, pointer, Frame(new Vector2(400f, 20f), new[] { MouseButton.Middle },
                delta: new Vector2(40f, 0f)));
            Assert.Equal(beforeChrome, scene.Camera.Position);

            Step(manager, pointer, Frame(new Vector2(400f, 20f)));
            scene.Controller.Mode = EditorToolMode.PlacePlacement;
            scene.PaletteFilter.Focus();
            Vector3 beforeField = scene.Camera.Position;
            Step(manager, pointer, Frame(new Vector2(480f, 270f), new[] { MouseButton.Middle },
                delta: new Vector2(40f, 0f)));
            Assert.Equal(beforeField, scene.Camera.Position);
        }

        [Fact]
        public void ModalCancellationRequiresFreshPressAfterDismissal()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector2 viewportPoint = new(480f, 270f);
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle }));
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(20f, 0f)));

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                keysDown: new[] { Key.LeftShift, Key.Escape }, pressedKeys: new[] { Key.Escape }));
            Assert.NotNull(scene.ExitDialog);
            Vector3 cancelledAt = scene.Camera.Position;

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(50f, 0f)));
            Assert.Equal(cancelledAt, scene.Camera.Position);
            scene.ExitDialog!.FooterButtons[^1].OnClick!.Invoke();

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(50f, 0f)));
            Assert.Equal(cancelledAt, scene.Camera.Position);
            Step(manager, pointer, Frame(viewportPoint));
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(50f, 0f)));
            Assert.NotEqual(cancelledAt, scene.Camera.Position);
        }

        [Fact]
        public void BookmarkRecallCancelsHeldNavigationUntilFreshPress()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector2 viewportPoint = new(480f, 270f);
            Vector3 storedPosition = scene.Camera.Position;
            float storedYaw = scene.Camera.Yaw;
            float storedPitch = scene.Camera.Pitch;
            Step(manager, pointer, Frame(viewportPoint,
                keysDown: new[] { Key.LeftShift, Key.D1 }, pressedKeys: new[] { Key.D1 }));

            scene.Camera.Position = new Vector3(20f, 15f, -10f);
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle }));
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(20f, 0f), keysDown: new[] { Key.D1 }, pressedKeys: new[] { Key.D1 }));
            Assert.Equal(storedPosition, scene.Camera.Position);
            Assert.Equal(storedYaw, scene.Camera.Yaw);
            Assert.Equal(storedPitch, scene.Camera.Pitch);

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(50f, 0f)));
            Assert.Equal(storedPosition, scene.Camera.Position);
            Assert.Equal(storedYaw, scene.Camera.Yaw);
            Assert.Equal(storedPitch, scene.Camera.Pitch);

            Step(manager, pointer, Frame(viewportPoint));
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(20f, 0f)));
            Assert.NotEqual(storedPosition, scene.Camera.Position);
        }

        [Fact]
        public void FocusLossCancelsSceneNavigation()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector2 viewportPoint = new(480f, 270f);
            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle }));
            Vector3 beforeLoss = scene.Camera.Position;

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Middle },
                delta: new Vector2(60f, 0f), focused: false));

            Assert.Equal(beforeLoss, scene.Camera.Position);
        }

        [Fact]
        public void NavigationSuppressesToolMutationIncludingReleaseFrame()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            scene.Controller.Mode = EditorToolMode.PlaceSpawn;
            string beforeHash = MapDocumentHash.OfWorld(scene.Document.Doc);
            Vector2 viewportPoint = new(480f, 270f);

            Step(manager, pointer, Frame(viewportPoint,
                new[] { MouseButton.Left, MouseButton.Middle }));
            Assert.Empty(scene.Document.Doc.Spawns);
            Step(manager, pointer, Frame(viewportPoint));

            Assert.Equal(beforeHash, MapDocumentHash.OfWorld(scene.Document.Doc));
            Assert.Empty(scene.Document.Doc.Spawns);
            Assert.False(scene.Document.History.CanUndo);
        }

        [Fact]
        public void WheelNavigationSuppressesPrimaryPointerMutation()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            scene.Controller.Mode = EditorToolMode.PlaceSpawn;
            string beforeHash = MapDocumentHash.OfWorld(scene.Document.Doc);

            Step(manager, pointer, Frame(new Vector2(480f, 270f), new[] { MouseButton.Left }, scroll: 2f));

            Assert.Equal(beforeHash, MapDocumentHash.OfWorld(scene.Document.Doc));
            Assert.Empty(scene.Document.Doc.Spawns);
        }

        [Fact]
        public void MovementKeysOnlyFlyAfterRightButtonAcquiresViewport()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector2 viewportPoint = new(480f, 270f);
            Vector3 before = scene.Camera.Position;

            Step(manager, pointer, Frame(viewportPoint, keysDown: new[] { Key.W }));
            Assert.Equal(before, scene.Camera.Position);

            Step(manager, pointer, Frame(viewportPoint, new[] { MouseButton.Right },
                keysDown: new[] { Key.W }), dt: 1f);
            Assert.NotEqual(before, scene.Camera.Position);
        }

        [Fact]
        public void PersistedFlySpeedDrivesRightButtonMovementIndependently()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            scene.Settings.FlySpeed = 37f;
            scene.OnSettingsChanged();
            Vector3 before = scene.Camera.Position;

            Step(manager, pointer, Frame(new Vector2(480f, 270f), new[] { MouseButton.Right },
                keysDown: new[] { Key.W }), dt: 1f);

            Assert.True(Vector3.Distance(before, scene.Camera.Position) > 36.9f);
            Assert.True(Vector3.Distance(before, scene.Camera.Position) < 37.1f);
        }

        [Fact]
        public void UnmodifiedFFocusesSupportedSelectionAndNoSelectionIsNoOp()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            Vector3 initial = scene.Camera.Position;

            Step(manager, pointer, Frame(new Vector2(480f, 270f), keysDown: new[] { Key.F },
                pressedKeys: new[] { Key.F }));
            Assert.Equal(initial, scene.Camera.Position);

            scene.Document.Selection.Set(SelectionKind.Placement, "focus-me");
            Step(manager, pointer, Frame(new Vector2(480f, 270f), keysDown: new[] { Key.F },
                pressedKeys: new[] { Key.F }));

            Assert.NotEqual(initial, scene.Camera.Position);
            Vector3 selectionCenter = new(12f, 1f, 8f);
            Assert.True(Vector3.Dot(Vector3.Normalize(selectionCenter - scene.Camera.Position), scene.Camera.Forward) > 0.999f);
        }

        [Fact]
        public void FocusSelectionDoesNotConsumeFWhileTyping()
        {
            (NavigationScene scene, SceneManager manager, Pointer pointer) = PushScene();
            scene.Document.Selection.Set(SelectionKind.Placement, "focus-me");
            scene.Controller.Mode = EditorToolMode.PlacePlacement;
            scene.PaletteFilter.Focus();
            Vector3 before = scene.Camera.Position;

            Step(manager, pointer, Frame(new Vector2(480f, 270f), keysDown: new[] { Key.F },
                pressedKeys: new[] { Key.F }));

            Assert.Equal(before, scene.Camera.Position);
        }

        static (NavigationScene Scene, SceneManager Manager, Pointer Pointer) PushScene()
        {
            var scene = new NavigationScene();
            var options = new MapEditorOptions();
            options.SpawnArchetypes.Add("wolf");
            scene.Init(null!, null!, null!, options);
            var pointer = new Pointer();
            var manager = new SceneManager
            {
                Pointer = pointer,
                UiViewport = new UiViewport(960, 540, 960, 540),
            };
            manager.Push(scene);
            return (scene, manager, pointer);
        }

        static void Step(SceneManager manager, Pointer pointer, InputState frame, float dt = 0.016f)
        {
            pointer.Update(frame);
            manager.Input = frame;
            manager.Update(dt);
        }

        InputState Frame(Vector2 position, IEnumerable<MouseButton>? heldButtons = null, Vector2 delta = default,
            float scroll = 0f,
            IEnumerable<Key>? keysDown = null, IEnumerable<Key>? pressedKeys = null, bool focused = true)
        {
            var held = heldButtons is null ? new HashSet<MouseButton>() : new HashSet<MouseButton>(heldButtons);
            var (pressed, released) = _mouse.Advance(held);
            return new InputState(
                keysDown is null ? new HashSet<Key>() : new HashSet<Key>(keysDown),
                pressedKeys is null ? new HashSet<Key>() : new HashSet<Key>(pressedKeys),
                new HashSet<Key>(), held, pressed, position, delta, scroll, 960, 540,
                windowFocused: focused, mouseReleased: released);
        }

        sealed class NavigationScene : MapEditorScene
        {
            protected override MapDocument CreateDocument(MapDocRegistry registry)
            {
                var doc = new MapDocument
                {
                    Id = "navigation-zone",
                    Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
                };
                doc.Placements.Add(new MapPlacement
                {
                    Id = "focus-me", Kind = "prop", X = 12f, Z = 8f, Y = 0f, Scale = 1f,
                });
                return doc;
            }

            protected override void BuildWorld() => Controller.Field = new TerrainField(new TerrainConfig
            {
                GentleAmplitude = 0f,
                DetailOctaves = 0,
            });

            protected override void TeardownWorld() { }
            protected override void UpdateStreaming(float dt) { }
            protected override bool RebuildWorld() => true;
        }
    }
}
