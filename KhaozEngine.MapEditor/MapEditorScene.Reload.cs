using System;
using System.IO;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Explicit reload of the current on-disk document, with dirty-state and rollback guards.</summary>
public partial class MapEditorScene
{
    /// <summary>Reloads a clean document from <see cref="MapEditorOptions.DocumentPath"/> and atomically swaps
    /// the editor state after the replacement viewport has built. Unsaved edits and focused editor fields are
    /// left untouched. A load or candidate-world failure keeps the current document and viewport.</summary>
    internal bool ReloadDocument()
    {
        if (_document.IsDirty)
        {
            _statusText = MapEditorStrings.Resolve(MapEditorStrings.ReloadUnsaved);
            return false;
        }
        if (AnyEditorFocused)
        {
            _statusText = MapEditorStrings.Resolve(MapEditorStrings.ReloadFocused);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_options.DocumentPath) ||
            MapDocumentFile.DetectForm(_options.DocumentPath) == MapDocumentForm.None)
        {
            _statusText = MapEditorStrings.Resolve(MapEditorStrings.ReloadMissing);
            return false;
        }

        ViewportWorld? replacementViewport = null;
        EditorDocument replacementDocument;
        MapTileRect? replacementWindow;
        try
        {
            MapDocument replacementMap = MapDocumentWindowing.Load(_options.DocumentPath,
                new MapDocumentLoadOptions { Registry = _document.Registry },
                _options.WholeWorldTileLimit, EffectiveWindowRadius, _options.PlayerSpawnSearchTileLimit,
                out _, out replacementWindow);
            replacementDocument = new EditorDocument(replacementMap, _document.Registry);
            replacementViewport = BuildReloadViewport(replacementDocument);
        }
        catch (Exception ex) when (ex is MapDocumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            replacementViewport?.Dispose();
            _statusText = MapEditorStrings.Resolve(MapEditorStrings.ReloadFailed, ex.Message);
            return false;
        }

        CommitReload(replacementDocument, replacementViewport, replacementWindow);
        _statusText = MapEditorStrings.Resolve(MapEditorStrings.Reloaded, _options.DocumentPath);
        return true;
    }

    /// <summary>Builds a replacement viewport without touching the live one. Returns null when the scene's
    /// viewport is not built, which is the headless-test path.</summary>
    protected virtual ViewportWorld? BuildReloadViewport(EditorDocument candidate)
    {
        if (!_viewport.IsBuilt) return null;
        var replacement = new ViewportWorld(_scene, _options.ManifestPaths)
        {
            ScatterLayerVisible = _visibility.GetLayer,
            RenderDistance = _viewport.RenderDistance,
            TexturedPropsEnabled = () => _options.TexturedProps,
        };
        try
        {
            replacement.Build(candidate.Doc, candidate.Registry);
            return replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    void CommitReload(EditorDocument replacement, ViewportWorld? replacementViewport, MapTileRect? replacementWindow)
    {
        EditorDocument previousDocument = _document;
        ViewportWorld previousViewport = _viewport;
        EditorToolController previousController = _controller;
        var replacementController = new EditorToolController(replacement)
        {
            HeightOf = KindHeight,
            IsVisible = _visibility.IsElementVisible,
            PlaceKind = previousController.PlaceKind,
            SpawnArchetype = previousController.SpawnArchetype,
            PlacingPlayerSpawn = previousController.PlacingPlayerSpawn,
            PlaceFeatureType = previousController.PlaceFeatureType,
            Brush = previousController.Brush,
            BrushRadius = previousController.BrushRadius,
            BrushStrength = previousController.BrushStrength,
            SetHeight = previousController.SetHeight,
        };

        UnsubscribeDocument(previousDocument);
        _document = replacement;
        _controller = replacementController;
        _window = replacementWindow;
        if (replacementViewport is not null)
        {
            _viewport = replacementViewport;
            _controller.Field = replacementViewport.Field;
        }
        SubscribeDocument(replacement);

        _pendingSelectId = null;
        _nameRow = null;
        _lastChromeMode = EditorToolMode.Select;
        _gestureRebuildAccumulator = 0f;
        _environmentDirty = true;
        RebuildOutline();
        RebuildInspector();

        if (replacementViewport is not null)
            previousViewport.Dispose();
    }

    void SubscribeDocument(EditorDocument document)
    {
        document.DocumentChanged += OnDocumentChanged;
        document.CommandApplied += OnCommandVisibilityForward;
        document.CommandRedone += OnCommandVisibilityForward;
        document.CommandUndone += OnCommandVisibilityInverse;
        document.Selection.Changed += OnSelectionChanged;
    }

    void UnsubscribeDocument(EditorDocument document)
    {
        document.DocumentChanged -= OnDocumentChanged;
        document.CommandApplied -= OnCommandVisibilityForward;
        document.CommandRedone -= OnCommandVisibilityForward;
        document.CommandUndone -= OnCommandVisibilityInverse;
        document.Selection.Changed -= OnSelectionChanged;
    }
}
