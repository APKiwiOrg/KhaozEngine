using System.Numerics;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public sealed partial class EditorToolController
{
    // Press-edge Add gives immediate feedback + selection, then the gesture stays held: while down, Move commands
    // for the placed id track the ground hit. AddPlacementCommand.TryMerge absorbs those same-id moves, so the whole
    // place-and-adjust folds into ONE undo step whose undo removes the placement. Release seals. A plain click (no
    // hold-move) lands the lone Add exactly as before.
    void UpdatePlacePlacement(in EditorFrameInput input)
    {
        if (Field is null) return;

        if (input.PointerPressed)
        {
            if (!EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 p)) return;
            string id = UniqueName("placement", PlacementIdExists);
            _document.Execute(new AddPlacementCommand(new MapPlacement { Id = id, Kind = PlaceKind, AssetId = _document.Doc.ResolverIdentity is null ? null : PlaceKind, X = p.X, Z = p.Z, Y = null }));
            _document.Selection.Set(SelectionKind.Placement, id);
            _placing = true; _placeKind = SelectionKind.Placement; _placeId = id;
            return;
        }

        if (!_placing || _placeKind != SelectionKind.Placement) return;
        if (input.PointerReleased) { _document.SealGesture(); _placing = false; return; }
        if (input.PointerDown && FindPlacement(_placeId) is not null
            && EditorPicking.PickTerrain(Field, input.RayOrigin, input.RayDirection, PickDistance, out Vector3 hit))
            _document.Execute(new MovePlacementCommand(_placeId, hit.X, hit.Z, null));
    }

    // The spawn place tool stamps either an NPC spawn or a player start, chosen by PlacingPlayerSpawn (the pinned
    // "player spawn" palette entry). Both share the press-edge-Add-then-hold-to-adjust place-and-adjust path: the
    // matching Add absorbs the same-id Move so the whole gesture is ONE undo step, sealed on release.
}
