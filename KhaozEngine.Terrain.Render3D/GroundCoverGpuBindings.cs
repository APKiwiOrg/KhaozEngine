using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

// Retained batches expand their immutable placements only when a model binding changes.
internal sealed class GroundCoverGpuBindings
{
    readonly GroundCoverBatch _cover;
    readonly MeshHandle[]?[] _parts;

    public GroundCoverGpuBindings(GroundCoverBatch cover)
    {
        _cover = cover;
        _parts = new MeshHandle[cover.ModelCount][];
    }

    public bool Refresh(IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes)
    {
        bool changed = false;
        for (int model = 0; model < _parts.Length; model++)
        {
            if (!meshes.TryGetValue(_cover.ModelId(model), out IReadOnlyList<MeshHandle>? live)) live = null;
            if (SameParts(_parts[model], live)) continue;
            int count = live?.Count ?? 0;
            MeshHandle[]? snapshot = count == 0 ? null : new MeshHandle[count];
            for (int part = 0; part < count; part++) snapshot![part] = live![part];
            _parts[model] = snapshot;
            changed = true;
        }
        return changed;
    }

    public FoliageInstance[] Expand()
    {
        int count = 0;
        for (int i = 0; i < _cover.Count; i++)
            count = checked(count + (Parts(i)?.Length ?? 0));
        if (count == 0) return Array.Empty<FoliageInstance>();
        var result = new FoliageInstance[count];
        int next = 0;
        for (int i = 0; i < _cover.Count; i++)
        {
            MeshHandle[]? parts = Parts(i);
            if (parts is null) continue;
            GroundCoverInstance placement = _cover.Items[i];
            for (int part = 0; part < parts.Length; part++)
                result[next++] = new FoliageInstance(parts[part], placement.Transform, placement.ThinningRank);
        }
        return result;
    }

    MeshHandle[]? Parts(int placement)
    {
        int model = _cover.ModelIndex(placement);
        return model < 0 ? null : _parts[model];
    }

    static bool SameParts(MeshHandle[]? snapshot, IReadOnlyList<MeshHandle>? live)
    {
        int count = live?.Count ?? 0;
        if ((snapshot?.Length ?? 0) != count) return false;
        for (int part = 0; part < count; part++)
        {
            MeshHandle old = snapshot![part];
            MeshHandle current = live![part];
            if (old.Index != current.Index || old.Generation != current.Generation) return false;
        }
        return true;
    }
}
