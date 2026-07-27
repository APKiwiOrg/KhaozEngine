using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Covers the one wiring the render-distance profile exists for in the editor: the viewport camera's far
    /// clip comes from the SAME <see cref="RenderDistanceProfile"/> the viewport streams and culls with. A far clip
    /// set independently of the terrain far field is exactly the bug this replaces (a 240 m terrain horizon inside a
    /// 500 m frustum, so the ground ended in a void with props still drawing over it). Uses the FakeScene idiom from
    /// <c>MapEditorSceneTests</c>: <see cref="MapEditorScene.BuildWorld"/> is overridden away so
    /// <see cref="MapEditorScene.OnEnter"/> runs headless with no device.</summary>
    public class MapEditorRenderDistanceTests
    {
        // Skips every device call so OnEnter constructs the camera + viewport headless, like MapEditorSceneTests.
        sealed class HeadlessScene : MapEditorScene
        {
            protected override void BuildWorld() { }
            protected override void TeardownWorld() { }
            protected override MapDocument CreateDocument(MapDocRegistry registry) =>
                new MapDocument { Id = "render-distance", Bounds = new MapBounds { MinX = -64f, MinZ = -64f, MaxX = 64f, MaxZ = 64f } };
        }

        static MapEditorScene Entered(MapEditorOptions options)
        {
            var scene = new HeadlessScene();
            scene.Init(null!, null!, null!, options);
            new SceneManager().Push(scene);   // OnEnter builds the camera + viewport
            return scene;
        }

        [Fact]
        public void Options_DefaultToTheFarTier()
        {
            Assert.Equal(RenderDistanceProfile.Default, new MapEditorOptions().RenderDistance);
        }

        [Fact]
        public void Camera_TakesItsFarClipFromTheDefaultProfile()
        {
            MapEditorScene scene = Entered(new MapEditorOptions());

            Assert.Equal(RenderDistanceProfile.Default.FarClip, scene.Camera.FarPlane);
        }

        [Fact]
        public void Camera_TakesItsFarClipFromADialledDownProfile()
        {
            // A head on a weak machine dials the whole set down, and the frustum shrinks with the streamed ring
            // rather than staying at the camera's stock 500 m.
            RenderDistanceProfile near = RenderDistanceProfile.For(RenderDistanceTier.Near);

            MapEditorScene scene = Entered(new MapEditorOptions { RenderDistance = near });

            Assert.Equal(near.FarClip, scene.Camera.FarPlane);
            Assert.NotEqual(RenderDistanceProfile.Default.FarClip, scene.Camera.FarPlane);
        }

        [Fact]
        public void Camera_FarClipStaysInsideTheStreamedTerrainFarField()
        {
            // The coherence that matters end to end: whatever tier a head picks, the frustum must not reach past the
            // terrain the viewport streams, or the ground ends in a void mid-frustum again.
            foreach (RenderDistanceTier tier in new[]
                     { RenderDistanceTier.Near, RenderDistanceTier.Medium, RenderDistanceTier.Far, RenderDistanceTier.Ultra })
            {
                RenderDistanceProfile p = RenderDistanceProfile.For(tier);
                MapEditorScene scene = Entered(new MapEditorOptions { RenderDistance = p });

                Assert.True(scene.Camera.FarPlane <= p.DecorRadiusMeters,
                    $"{tier}: far clip {scene.Camera.FarPlane} m reaches past the {p.DecorRadiusMeters} m terrain far field");
            }
        }
    }
}
