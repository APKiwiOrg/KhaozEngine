using System;
using System.Linq;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>
    /// What the world rebuild does with a document that is invalid mid-edit (#77). ViewportWorld.BuildPropLayers
    /// throws <see cref="MapDocumentException"/> for a companion layer naming a host scatter layer the document
    /// does not declare, and the rebuild runs inside OnUpdate, so the throw escaped the frame and took the editor
    /// with it. In its own file because the test class itself is at its file-size baseline and may not grow.
    /// </summary>
    public partial class MapEditorSceneTests
    {
        // A document mid-edit into an invalid state: one companion layer whose host scatter layer is not declared.
        // That is exactly the shape ViewportWorld.BuildPropLayers rejects.
        static MapDocument SampleWithDanglingCompanionHost()
        {
            var doc = new MapDocument
            {
                Id = "dangling-host",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "rocks", HostLayer = "trees" });
            return doc;
        }

        // The full-rebuild seam runs the same host-resolution rule ViewportWorld.BuildPropLayers does and throws
        // the same exception with the same message, so the scene sees what a device would hand it without one.
        // Controller.Field stands in for the built world: a rebuild that completes re-points it, one that throws
        // must leave the previous one standing.
        sealed class InvalidRebuildScene : MapEditorScene
        {
            readonly Func<MapDocument> _factory;
            public int FullRebuilds;
            public InvalidRebuildScene(Func<MapDocument> factory) => _factory = factory;
            protected override MapDocument CreateDocument(MapDocRegistry registry) => _factory();
            protected override void BuildWorld() => Controller.Field = FlatField();
            protected override void TeardownWorld() { }

            protected override bool RebuildWorld()
            {
                foreach (MapCompanionLayer cl in Document.Doc.CompanionLayers)
                    if (!Document.Doc.ScatterLayers.Any(l => l.Name == cl.HostLayer))
                        throw new MapDocumentException(
                            $"companion layer '{cl.Name}' names unknown host scatter layer '{cl.HostLayer}' in map '{Document.Doc.Id}'.");
                FullRebuilds++;
                Controller.Field = FlatField();
                return true;
            }

            public void RunRebuildCheck(float dt = 0f) => CheckWorldRebuild(dt);

            static TerrainField FlatField() => new(new TerrainConfig { GentleAmplitude = 0f });
        }

        static InvalidRebuildScene PushInvalidRebuildScene()
        {
            var scene = new InvalidRebuildScene(SampleWithDanglingCompanionHost);
            scene.Init(null!, null!, null!, new MapEditorOptions());
            new SceneManager().Push(scene);
            scene.Document.AcknowledgeWorldRebuild();   // ignore any pending state from the initial load
            return scene;
        }

        [Fact]
        public void CheckWorldRebuild_InvalidDocument_SurfacesTheErrorAndKeepsThePreviousWorld()
        {
            InvalidRebuildScene scene = PushInvalidRebuildScene();
            TerrainField? before = scene.Controller.Field;
            Assert.NotNull(before);
            scene.Document.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: 3f));   // whole-world edit
            Assert.True(scene.Document.WorldRebuildPending);

            scene.RunRebuildCheck();   // this is the call that used to throw straight out of OnUpdate

            Assert.Equal(0, scene.FullRebuilds);
            Assert.Same(before, scene.Controller.Field);   // the world the editor was showing is still standing
            Assert.Contains("unknown host scatter layer 'trees'", scene.StatusText);
            // Consumed, so an invalid document does not re-throw once a frame for as long as it stays invalid.
            Assert.False(scene.Document.WorldRebuildPending);
        }

        [Fact]
        public void CheckWorldRebuild_AfterAFailedRebuild_TheNextEditOnAFixedDocumentRebuilds()
        {
            InvalidRebuildScene scene = PushInvalidRebuildScene();
            scene.Document.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: 3f));
            scene.RunRebuildCheck();
            Assert.Equal(0, scene.FullRebuilds);

            // The author declares the missing host layer and edits again: the editor is alive and catches up.
            scene.Document.Doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
            scene.Document.Execute(new EditTerrainCommand(newWaterLevel: 7f, oldWaterLevel: 5f));

            scene.RunRebuildCheck();

            Assert.Equal(1, scene.FullRebuilds);
            Assert.False(scene.Document.WorldRebuildPending);
        }
    }
}
