using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>The world-rebuild seams: which one a pending edit dispatches to, the gesture throttle around the full
/// path, and what happens when the document is invalid when the rebuild reads it. In their own file because the
/// class itself is at its file-size baseline and may not grow.</summary>
public partial class MapEditorScene
{
    /// <summary>Consumes a pending world rebuild after every edit source this frame (tools, then chrome, which
    /// covers the property-grid inspector), so an edit from either one lands in the streamed world before the
    /// next frame's pick. A pending edit that reported a bounded region
    /// (<see cref="EditorDocument.PendingRebuildRegion"/>) rebuilds ONLY the chunks that region overlaps via
    /// <see cref="PartialRebuildWorld"/>, never throttled (it is cheap by construction). A null region (a
    /// whole-world edit, or the partial path declining because the world is not built) falls through to the full
    /// <see cref="RebuildWorld"/>, which IS throttled while a drag or draw gesture is live
    /// (<see cref="EditorToolController.IsDragging"/> / <see cref="EditorToolController.IsDrawing"/>): a full
    /// rebuild only runs once <see cref="MapEditorOptions.GestureRebuildInterval"/> seconds have accumulated since
    /// the last one, so a fast mid-gesture edit stream does not re-mesh the whole world every frame. The pending
    /// flag is left untouched on a throttled-skip frame (not acknowledged), so the very next check after the
    /// gesture ends falls straight through to the unthrottled branch and performs the final full rebuild with no
    /// extra plumbing. Either way a rebuild that actually ran is acknowledged so it fires once. Overridable for
    /// headless order tests, and it dispatches through the two rebuild seams so a headless test can observe the
    /// routing without a device.
    /// <para>A rebuild reads the document as it stands, which mid-edit can be invalid: both rebuild paths reach
    /// code that throws <see cref="MapDocumentException"/> (ViewportWorld's prop-layer build rejects a companion
    /// layer naming a host scatter layer the document does not declare). This runs inside OnUpdate, so that throw
    /// is caught here rather than escaping the frame: the message goes to the status strip, the previously built
    /// world stays up, and the pending flag is consumed so an invalid document does not throw once a frame.</para>
    /// </summary>
    protected virtual void CheckWorldRebuild(float dt)
    {
        if (!_document.WorldRebuildPending) return;
        try
        {
            CheckWorldRebuildCore(dt);
        }
        catch (MapDocumentException ex)
        {
            // A rebuild reads the LIVE document, and an edit can leave it momentarily invalid (a companion layer
            // naming a host scatter layer nothing declares is the shape ViewportWorld.BuildPropLayers rejects).
            // This runs inside OnUpdate, so letting that escape takes the editor down with the frame. Surface it
            // on the status strip and keep the world the editor was already showing: nothing was swapped, because
            // both seams build before they re-point anything.
            _statusText = ex.Message;
            // Consumed even though nothing rebuilt, so a document that stays invalid does not throw once a frame.
            // The next edit marks it pending again and the world catches up as soon as the document is valid.
            _document.AcknowledgeWorldRebuild();
            _gestureRebuildAccumulator = 0f;
        }
    }

    void CheckWorldRebuildCore(float dt)
    {
        if (_document.PendingRebuildRegion is RectArea dirty && PartialRebuildWorld(dirty))
        {
            _document.AcknowledgeWorldRebuild();
            return;
        }

        if (_controller.IsDragging || _controller.IsDrawing)
        {
            _gestureRebuildAccumulator += dt;
            if (_gestureRebuildAccumulator < _options.GestureRebuildInterval) return;   // throttled: stays pending
        }

        if (RebuildWorld())
        {
            _document.AcknowledgeWorldRebuild();
            _gestureRebuildAccumulator = 0f;
        }
    }

    /// <summary>Partial-rebuild seam: re-mesh only the loaded chunks overlapping <paramref name="dirty"/> and
    /// re-point the tool controller at the swapped field. Returns false when the viewport is not built (the
    /// <see cref="ViewportWorld.PartialRebuild"/> not-built contract), so <see cref="CheckWorldRebuild"/> falls back
    /// to a full rebuild. Overridable so a headless test can observe the dispatch without a device.</summary>
    protected virtual bool PartialRebuildWorld(RectArea dirty)
    {
        if (!_viewport.PartialRebuild(_document.Doc, _document.Registry, dirty)) return false;
        _controller.Field = _viewport.Field;
        return true;
    }

    /// <summary>Full-rebuild seam for a pending edit with no bounded region: rebuild the whole streamed world and
    /// re-point the tool controller at the fresh field. Returns false (a no-op) when the viewport is not built, so
    /// <see cref="CheckWorldRebuild"/> leaves the rebuild pending rather than throwing. Overridable so a headless
    /// test can observe the dispatch without a device. <see cref="CheckWorldRebuild"/> wraps its gesture throttle
    /// around this full path only, never around <see cref="PartialRebuildWorld"/>.</summary>
    protected virtual bool RebuildWorld()
    {
        if (!_viewport.IsBuilt) return false;
        _viewport.Rebuild(_document.Doc, _document.Registry);
        _controller.Field = _viewport.Field;
        return true;
    }
}
