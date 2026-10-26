using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public sealed partial class MapFoliageResource
{
    IGroundCoverInstanceSource? _source;
    bool _resident, _released;
    MapFoliageResource(MapSubmissionAnchor anchor, IGroundCoverInstanceSource source, IReadOnlyList<MapFoliageModelBinding> models)
    {
        Anchor = anchor; TransformSpace = MapFoliageTransformSpace.World; _source = source; _resident = true;
        Models = Array.AsReadOnly(models.ToArray()); ValidateRows();
    }
    internal static MapFoliageResource FromResident(MapSubmissionAnchor anchor, IGroundCoverInstanceSource source,
        IReadOnlyList<MapFoliageModelBinding> models) => new(anchor, source, models);
    internal void MarkResident() => _resident = true;
    void RequireLive() => ObjectDisposedException.ThrowIf(_released, this);
    void ValidateRows()
    {
        var transforms = RequireSpace(TransformSpace);
        var ranks = BorrowRanks.Span; var indices = BorrowModelIndices.Span;
        if (transforms.Length != ranks.Length || indices.Length != ranks.Length || Models.Any(m => m is null) ||
            Models.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() != Models.Count)
            throw new ArgumentException("Foliage rows require equal lengths, finite ranks and valid model bindings.");
        for (int i = 0; i < ranks.Length; i++)
            if (indices[i] < 0 || indices[i] >= Models.Count || !float.IsFinite(ranks[i]) || ranks[i] < 0 || ranks[i] > 1)
                throw new ArgumentException("Foliage rows require equal lengths, finite ranks and valid model bindings.");
    }
    internal MapFoliageResource Detached() => TransformSpace == MapFoliageTransformSpace.World
        ? new(Anchor, BorrowWorldTransforms, BorrowRanks, BorrowModelIndices, Models)
        : FromAnchorLocal(Anchor, BorrowAnchorLocalTransforms, BorrowRanks, BorrowModelIndices, Models);
    internal void ReleaseResident()
    {
        if (!_resident || _released) return;
        _source = null; _transforms = null; _ranks = null; _indices = null; Models = []; _released = true;
    }
}
