using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;

namespace KhaozEngine.Tests.MapDoc;

internal static class TransactionFixtures
{
    internal static EditorDocument NativeWithSurfaces()
    {
        var e = new EditorDocument(SurfaceStorageFixtures.ThreeSurfaces());
        e.BindNativeAssets(SurfaceStorageFixtures.Assets());
        return e;
    }

    internal static string State(EditorDocument e) => string.Join("\n",
        MapDocumentFile.SaveText(e.Doc, e.Registry), e.History.UndoLabel, e.History.RedoLabel,
        e.IsDirty.ToString(), SurfaceStorageFixtures.SemanticSnapshot(e.Doc), e.LastNativeEffects?.Describe());
}

internal sealed class TestPatchHeightCommand : EditorCommand, INativeDocumentCommand
{
    readonly MapPatchKey _key;
    readonly int _index;
    readonly int _value;
    int? _before;

    internal TestPatchHeightCommand(MapPatchKey key, int index, int value)
    {
        _key = key;
        _index = index;
        _value = value;
    }

    public override string Label => "Patch height";
    internal override bool AffectsWorld => true;

    public override void Apply(MapDocument doc) =>
        NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);

    public override void Revert(MapDocument doc) =>
        NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);

    NativeDocumentPreparation INativeDocumentCommand.Prepare(MapDocument candidate, bool undo)
    {
        MapSurfacePatch patch = candidate.Surfaces.Patches[_key];
        int before = patch.Heights[_index];
        patch.Heights[_index] = undo
            ? _before ?? throw new InvalidOperationException("Revert called before Apply.")
            : _value;
        var patches = Array.AsReadOnly(new[] { _key });
        var writeSet = new MapNativeWriteSet(patches, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<MapCornerOwnerChange>(), Array.Empty<ushort>(), false);
        var effects = new MapNativeEditEffects(null, null, patches, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<MapDigestChange>(),
            MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav);
        return new NativeDocumentPreparation(writeSet, effects, () =>
        {
            if (!undo) _before ??= before;
        });
    }
}
