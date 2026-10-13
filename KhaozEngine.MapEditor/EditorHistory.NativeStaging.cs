using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapEditor;

public sealed partial class EditorHistory
{
    internal void RebindNativeAssets(MapAssetClosure? assets, INativePlacementBounds? bounds)
    {
        _nativeAssets = assets;
        _placementBounds = bounds;
    }
    internal NativeStagedPublication StageExecute(MapDocument document, IEditorCommand command)
    {
        var staged = NativeDocumentTransaction.Stage(document, command, false, _nativeAssets, Registry, placementBounds: _placementBounds);
        Action? adopt = !_mergeBarrier && _undo.Count != 0 && _undo[^1] is INativePlacementHistoryMerge merge
            ? merge.PrepareMerge(command, staged) : null;
        _undo.EnsureCapacity(_undo.Count + 1);
        return WithAcceptance(staged, () =>
        {
            staged.Accept();
            adopt?.Invoke();
            if (adopt is null) _undo.Add(command);
            _redo.Clear();
            _mergeBarrier = false;
            AcceptBinding(staged);
        });
    }
    internal NativeStagedPublication? StageUndo(MapDocument document)
    {
        if (_undo.Count == 0) return null;
        IEditorCommand command = _undo[^1];
        var staged = NativeDocumentTransaction.Stage(document, command, true, _nativeAssets, Registry, placementBounds: _placementBounds);
        _redo.EnsureCapacity(_redo.Count + 1);
        return WithAcceptance(staged, () =>
        {
            staged.Accept();
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(command);
            _mergeBarrier = true;
            AcceptBinding(staged);
        });
    }
    internal NativeStagedPublication? StageRedo(MapDocument document)
    {
        if (_redo.Count == 0) return null;
        IEditorCommand command = _redo[^1];
        var staged = NativeDocumentTransaction.Stage(document, command, false, _nativeAssets, Registry, placementBounds: _placementBounds);
        _undo.EnsureCapacity(_undo.Count + 1);
        return WithAcceptance(staged, () =>
        {
            staged.Accept();
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(command);
            _mergeBarrier = true;
            AcceptBinding(staged);
        });
    }
    void AcceptBinding(NativeStagedPublication staged)
    {
        _nativeAssets = staged.Assets;
        LastNativeEffects = staged.Effects;
    }
    static NativeStagedPublication WithAcceptance(NativeStagedPublication staged, Action accept)
    {
        bool accepted = false;
        return staged with { Accept = () => { if (accepted) return; accepted = true; accept(); } };
    }
}
