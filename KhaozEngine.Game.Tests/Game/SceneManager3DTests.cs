using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// The <c>KhaozEngine.Game.Render3D</c> bridge: <see cref="SceneManager3DExtensions.Draw3D"/> dispatches a 3D
    /// pass to the visible scenes implementing <see cref="IGameScene3D"/> - the same visible set as Draw2D, and
    /// only the 3D-capable scenes. Runs headless: the recording scenes ignore the <see cref="Scene3D"/> arg, so
    /// the test passes <c>null</c> (no GPU needed).
    /// </summary>
    public class SceneManager3DTests
    {
        sealed class Scene3DRec : GameScene, IGameScene3D
        {
            public int Draws;
            public Scene3DRec(bool drawBelow = false) { DrawBelow = drawBelow; }
            public void OnDraw3D(Scene3D scene) => Draws++;   // ignores scene -> null-safe in tests
        }

        sealed class Plain2DScene : GameScene
        {
            public Plain2DScene(bool drawBelow = false) { DrawBelow = drawBelow; }
        }

        static SceneManager Push(params GameScene[] scenes)
        {
            var m = new SceneManager();
            foreach (var s in scenes) m.Push(s);
            return m;
        }

        [Fact]
        public void Draw3D_Calls_The_Single_Visible_3D_Scene()
        {
            var s = new Scene3DRec();
            Push(s).Draw3D(null!);
            Assert.Equal(1, s.Draws);
        }

        [Fact]
        public void Draw3D_Skips_Scenes_That_Are_Not_IGameScene3D()
        {
            var plain = new Plain2DScene();
            var three = new Scene3DRec();
            // plain on top, opaque -> it hides 'three' below; nothing 3D is visible, no throw.
            Push(three, plain).Draw3D(null!);
            Assert.Equal(0, three.Draws);
        }

        [Fact]
        public void Draw3D_Respects_Visibility_OpaqueOverlay_Hides_Below()
        {
            var bottom = new Scene3DRec();
            var opaqueTop = new Scene3DRec(drawBelow: false); // hides bottom
            Push(bottom, opaqueTop).Draw3D(null!);
            Assert.Equal(0, bottom.Draws);   // hidden
            Assert.Equal(1, opaqueTop.Draws);
        }

        [Fact]
        public void Draw3D_TransparentOverlay_Reveals_Below()
        {
            var bottom = new Scene3DRec();
            var glassTop = new Scene3DRec(drawBelow: true); // reveals bottom
            Push(bottom, glassTop).Draw3D(null!);
            Assert.Equal(1, bottom.Draws);
            Assert.Equal(1, glassTop.Draws);
        }

        [Fact]
        public void Draw3D_EmptyStack_Is_NoOp()
        {
            new SceneManager().Draw3D(null!); // must not throw
        }
    }
}
