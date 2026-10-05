namespace KhaozEngine.MapEditor;

public sealed partial class EditorToolController
{
    /// <summary>The active tool. Setting it to a different value cancels any in-flight gesture and seals the
    /// undo stack (the Task 2 gesture barrier), so a later edit never coalesces across the tool switch.</summary>
    public EditorToolMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _dragging = false;
            _drawing = false;
            _pendingBody = false;
            _placing = false;
            _sculpting = false;
            _document.SealGesture();
            _mode = value;
        }
    }

    // A rejected UI edit cancels transient input state, without applying a command or changing history. It still
    // ends the UI gesture, so the merge barrier is raised: the release that would normally seal never arrives, and
    // an accepted Add or Move before the rejection must not absorb a later edit of the same element.
    internal void CancelRejectedGesture(EditorToolMode mode)
    {
        _dragging = false;
        _drawing = false;
        _pendingBody = false;
        _placing = false;
        _sculpting = false;
        _sculptBase = null;
        _document.SealGesture();
        _mode = mode;
    }
}
