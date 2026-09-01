using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// Headless coverage for <see cref="SceneManager.SetFrameContext"/>: the one call that carries a frame's
    /// whole scene context, so a host cannot wire five of the seven fields and leave the rest silently at
    /// their defaults (which is what strands BootScreen's own retry/quit UI with an empty InputState and a
    /// never-updated fallback pointer).
    /// </summary>
    public class SceneManagerFrameContextTests
    {
        sealed class Probe : GameScene
        {
        }

        static InputState FocusedInput() => new(
            down: new HashSet<Key> { Key.Enter }, pressed: new HashSet<Key> { Key.Enter },
            released: new HashSet<Key>(),
            mouseDown: new HashSet<MouseButton>(), mousePressed: new HashSet<MouseButton>(),
            mousePosition: new Vector2(12f, 34f), mouseDelta: Vector2.Zero, scrollDelta: 0f,
            width: 1280, height: 720);

        [Fact]
        public void SetFrameContext_CarriesEveryField()
        {
            var manager = new SceneManager();
            var pointer = new Pointer();
            var viewport = new DesignViewport(320, 200, ScaleMode.Fit);
            var ui = new UiViewport();
            var uiPointer = new Pointer();
            InputState input = FocusedInput();

            manager.SetFrameContext(input, pointer, viewport, ui, uiPointer, 1280, 720);

            Assert.Equal(input.KeysDown, manager.Input.KeysDown);
            Assert.Same(pointer, manager.Pointer);
            Assert.Same(viewport, manager.Viewport);
            Assert.Same(ui, manager.UiViewport);
            Assert.Same(uiPointer, manager.UiPointer);
            Assert.Equal(1280, manager.FrameWidth);
            Assert.Equal(720, manager.FrameHeight);
        }

        [Fact]
        public void SetFrameContext_ReachesAScene()
        {
            var manager = new SceneManager();
            var scene = new Probe();
            manager.Push(scene);

            var uiPointer = new Pointer();
            manager.SetFrameContext(FocusedInput(), null, null, null, uiPointer, 800, 600);

            Assert.NotNull(scene.Manager);
            Assert.Same(uiPointer, scene.Manager!.UiPointer);
            Assert.True(scene.Manager.Input.IsDown(Key.Enter));
        }

        [Fact]
        public void AManagerNobodyWiredStillReadsAsEmpty()
        {
            // The failure this call exists to prevent, pinned as the before picture: an unwired manager hands a
            // scene an empty InputState and a null UiPointer, which is exactly what silently disables
            // BootScreen's retry/quit UI.
            var manager = new SceneManager();
            Assert.False(manager.Input.IsDown(Key.Enter));
            Assert.Null(manager.UiPointer);
        }
    }
}
