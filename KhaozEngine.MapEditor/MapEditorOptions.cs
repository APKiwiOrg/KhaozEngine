using System;
using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.MapDoc;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Turn-key startup for <see cref="MapEditorScene"/>: which document to open (and save back to on
/// Ctrl+S), the asset manifests the palette + picking read, the feature registry, and the game-supplied spawn
/// archetype list. A per-game editor head fills this and pushes the scene onto its <see cref="SceneManager"/>.
/// </summary>
public sealed class MapEditorOptions
{
    /// <summary>The map document file or tiled directory to load on enter and save back to on Ctrl+S. Empty
    /// starts a blank document.</summary>
    public string DocumentPath = "";

    /// <summary>How the editor leaves when it is the bottom scene on the stack (nothing to pop back to). The head
    /// wires this to its own quit path (a <c>GameApp3D</c> subclass calling the protected <c>GameApp.Quit()</c>),
    /// since a scene never touches window APIs directly. Null (the default) means the editor only ever pops: with
    /// no <see cref="RequestQuit"/> and no scene beneath, the exit dialog's Close leaves an empty stack (a blank
    /// screen), so a head that pushes the editor as its only scene should set this.</summary>
    public Action? RequestQuit;

    /// <summary>Asset manifests parsed into the kit palette and the picking heights.</summary>
    public List<string> ManifestPaths = new();

    /// <summary>The feature registry used to load / save / build the document. Null defaults to
    /// <see cref="MapDocRegistry.CreateDefault"/>.</summary>
    public MapDocRegistry? Registry;

    /// <summary>Spawn archetype ids the game offers in the spawn tool (dropdown content).</summary>
    public List<string> SpawnArchetypes = new();

    /// <summary>Points of extra clearance reserved at the bottom of the window for a host-drawn overlay (for
    /// example the Showcase's F7-F10 display readout line): the status strip and the editor body above it shift
    /// up by this much, so the editor chrome never stacks on the same pixels as the host readout. Default 0 keeps
    /// the status strip flush with the window bottom.</summary>
    public float StatusBottomOffset;

    /// <summary>When true (the default) the viewport draws translucent ground overlays for exclusions (red),
    /// regions (blue), and terrain-feature centers (amber), with the selected element brightened, so those
    /// otherwise-invisible authoring shapes are findable while editing. Set false to hide them.</summary>
    public bool ShowOverlays = true;

    /// <summary>When true (the default, matching gameplay) a manifest entry flagged <see cref="AssetEntry.Textured"/>
    /// loads its textured multi-part form in the viewport, same as the game. Set false to render every prop in its
    /// flattened, load-time-averaged colour instead, regardless of the manifest flag, for editing clarity (a dense
    /// textured forest can be harder to read while placing props than its flat silhouette). Session-level state
    /// only, read at load time via <see cref="ViewportWorld.TexturedPropsEnabled"/>, so flipping it rebuilds the
    /// streamed world (see the Layers-panel "Textured props" row).</summary>
    public bool TexturedProps = true;

    /// <summary>Minimum seconds between FULL viewport rebuilds while a drag or draw gesture is live
    /// (<see cref="EditorToolController.IsDragging"/> / <see cref="EditorToolController.IsDrawing"/>), so a fast
    /// mid-gesture edit stream (dragging a lake's radius, say) does not re-mesh the whole streamed world every
    /// frame. The default 0.25 keeps the viewport visibly live during a drag without paying for a rebuild on
    /// every frame. 0 disables the throttle (rebuilds every frame, the pre-throttle behaviour). Only the FULL
    /// rebuild path is throttled: a bounded-region <see cref="MapEditorScene.PartialRebuildWorld"/> is cheap by
    /// construction and always runs immediately, and once the gesture ends the very next check performs the
    /// final full rebuild regardless of this interval.</summary>
    public float GestureRebuildInterval = 0.25f;

    /// <summary>The viewport's render distance, as one coherent set: the streamed terrain far field, the streamed
    /// prop cull radius, the camera far clip and the ocean plane extent all come from this profile, so the horizon
    /// reads as one piece instead of terrain ending in a void inside the frustum. Default
    /// <see cref="RenderDistanceProfile.Default"/> (the Far tier). A head on a weak machine can dial it down with
    /// <see cref="RenderDistanceProfile.For"/>, e.g. <c>RenderDistanceProfile.For(RenderDistanceTier.Near)</c>, which
    /// trims the streamed ring as well as the visible distance. A hand-rolled profile is checked with
    /// <see cref="RenderDistanceProfile.Validate"/> when the scene builds its world, so an incoherent set throws at
    /// editor start rather than rendering wrong.</summary>
    public RenderDistanceProfile RenderDistance = RenderDistanceProfile.Default;

    /// <summary>Occupied-tile ceiling below which opening a tiled document loads it whole, exactly like a
    /// monolithic one. Above it, the editor opens a window instead (see <see cref="EditorWindowRadius"/>) rather
    /// than paying the whole-load cost the tiled format exists to remove. Default 512,
    /// <see cref="MapDocumentWindowing.DefaultWholeWorldTileLimit"/>.</summary>
    public int WholeWorldTileLimit = MapDocumentWindowing.DefaultWholeWorldTileLimit;

    /// <summary>Tile radius either side of the window center when a tiled document opens windowed (see
    /// <see cref="WholeWorldTileLimit"/>). Default 2, <see cref="MapDocumentWindowing.DefaultEditorWindowRadius"/>.
    /// </summary>
    public int EditorWindowRadius = MapDocumentWindowing.DefaultEditorWindowRadius;
}
