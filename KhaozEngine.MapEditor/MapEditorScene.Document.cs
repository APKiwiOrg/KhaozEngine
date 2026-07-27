using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Document load/save, form-aware: a tiled directory and a monolithic file are both first class, and
/// a save always goes back in the form and directory a document was opened from. See
/// <see cref="MapDocumentWindowing"/> for the whole-load-versus-windowed policy this loads through.</summary>
public partial class MapEditorScene
{
    /// <summary>The load window when the current document opened windowed (see
    /// <see cref="MapEditorOptions.WholeWorldTileLimit"/>), null for a whole-loaded or blank document. Exposed
    /// so <see cref="StatusLine"/> can show the window extent and a headless test can assert the dispatch.</summary>
    internal MapTileRect? Window => _window;
    MapTileRect? _window;

    /// <summary>Loads the document at <see cref="MapEditorOptions.DocumentPath"/> or starts a blank one. A path
    /// with no form at all (nothing on disk) starts blank, same as an empty path. A monolithic file or a tiled
    /// directory at or under <see cref="MapEditorOptions.WholeWorldTileLimit"/> occupied tiles loads whole. A
    /// larger tiled directory opens windowed (<see cref="MapDocumentWindowing"/>) at
    /// <see cref="EffectiveWindowRadius"/>, which is the head's configured radius scaled by the operator's
    /// render-distance multiplier so the loaded slice keeps up with the visible horizon. A seam so a headless test
    /// can inject a document without touching the file system.</summary>
    protected virtual MapDocument CreateDocument(MapDocRegistry registry)
    {
        _window = null;
        if (string.IsNullOrWhiteSpace(_options.DocumentPath) ||
            MapDocumentFile.DetectForm(_options.DocumentPath) == MapDocumentForm.None)
        {
            return new MapDocument
            {
                Id = "untitled",
                Bounds = new MapBounds { MinX = -128f, MinZ = -128f, MaxX = 128f, MaxZ = 128f },
            };
        }

        MapDocument doc = MapDocumentWindowing.Load(_options.DocumentPath,
            new MapDocumentLoadOptions { Registry = registry },
            _options.WholeWorldTileLimit, EffectiveWindowRadius,
            out _, out MapTileRect? window);
        _window = window;
        return doc;
    }

    /// <summary>Saves the document back to <see cref="MapEditorOptions.DocumentPath"/>, in the form it was
    /// opened in (<see cref="MapDocumentFile.SaveAuto"/>): a tiled directory saves tiled, a monolithic file
    /// saves monolithic, never converting implicitly. A path with nothing on it yet (a brand new document that
    /// has never been saved) writes monolithic, matching what the path held before this document ever existed.
    /// Surfaces a <see cref="MapDocumentException"/> (invalid content, or content that moved into a tile a
    /// windowed load never loaded, see <see cref="MapEditorOptions.WholeWorldTileLimit"/>) or a missing path
    /// into the status strip instead of throwing. Returns true only when the save actually wrote and the
    /// document was marked clean. The exit dialog relies on that so a failure never quits or dismisses.
    /// Internal so the Ctrl+S handler, the toolbar button, and the tests share one path.</summary>
    internal bool SaveDocument()
    {
        if (string.IsNullOrWhiteSpace(_options.DocumentPath))
        {
            _statusText = "No document path set";
            return false;
        }
        try
        {
            if (MapDocumentFile.DetectForm(_options.DocumentPath) == MapDocumentForm.None)
                MapDocumentFile.Save(_document.Doc, _options.DocumentPath, _document.Registry);
            else
                MapDocumentFile.SaveAuto(_document.Doc, _options.DocumentPath, _document.Registry);
            _document.MarkSaved();
            _statusText = "Saved " + _options.DocumentPath;
            return true;
        }
        catch (MapDocumentException ex)
        {
            _statusText = "Save failed: " + ex.Message;
            return false;
        }
    }
}
