using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.MapEditor;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Showcase
{
    /// <summary>Factory for the showcase's map-editor room. <see cref="MapEditorScene"/> is registered directly
    /// (no wrapper <c>GameScene</c>, unlike the "Init-inject then delegate" shape a first read of Room3D suggests):
    /// <c>GameScene.Manager</c> is set ONLY by <c>SceneManager.Push</c> (an internal setter in
    /// <c>KhaozEngine.Game</c>, not visible to this assembly), and <see cref="MapEditorScene"/> already IS a
    /// complete <c>GameScene</c> + <c>IGameScene3D</c> with its own Init-injection chain (<see cref="MapEditorScene.Init"/>)
    /// mirroring Room3D's. A wrapper that merely forwarded OnEnter/OnUpdate/OnDrawUi/OnExit/OnDraw3D calls to an
    /// inner <see cref="MapEditorScene"/> built by `new` (rather than pushed) would leave that inner scene's
    /// <c>Manager</c> permanently null, so its first <c>Manager!.Input</c> read (in <c>OnUpdate</c>) would throw.
    /// Registering a factory that returns <see cref="Create"/>'s result straight onto <c>ShowcaseApp.Rooms</c> is
    /// therefore the only correct wiring, not a style choice. See <see cref="ShowcaseApp.OnLoad"/>.</summary>
    public static class RoomMapEditor
    {
        // Two illustrative NPC archetype ids for the spawn tool's dropdown content. The editor never interprets
        // them itself (see MapEditorOptions.SpawnArchetypes). It only stamps the chosen string onto a new
        // MapSpawn.ArchetypeId for a game to read at load.
        static readonly IReadOnlyList<string> SpawnArchetypes = new[] { "wolf", "boar" };

        /// <summary>Builds the turn-key <see cref="MapEditorScene"/> over the committed showcase demo document
        /// (<c>assets/maps/demo.map.json</c>, copied beside the exe) and the same prop/building kit manifests
        /// <see cref="Room3D"/> loads, so the editor's palette and picking heights match what the 3D room
        /// actually renders.</summary>
        public static MapEditorScene Create(Scene3D scene, Texture2D white, DpiFont font)
        {
            // The outline post effect is off by the engine default, and MapEditorScene never touches Post (no
            // cel/outline key bindings of its own), so the editor gets the plain lit look with nothing to force.

            string assets = Path.Combine(AppContext.BaseDirectory, "assets");
            var options = new MapEditorOptions
            {
                DocumentPath = Path.Combine(assets, "maps", "demo.map.json"),
                ManifestPaths = new List<string>
                {
                    Path.Combine(assets, "props", "props.manifest.json"),
                    Path.Combine(assets, "buildings", "buildings.manifest.json"),
                },
                SpawnArchetypes = new List<string>(SpawnArchetypes),
                // Reserve the bottom band the app's F7-F10 display readout draws in, so the editor's own status
                // strip sits directly above it instead of stacking on the same pixels.
                StatusBottomOffset = ShowcaseApp.DisplayReadoutHeight,
            };
            return new MapEditorScene().Init(scene, white, font, options);
        }
    }
}
