using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

// Retained batches expand their immutable placements only when a model binding or its effective
// motion and casting policy changes. Policies are compared by value, never by map reference.
internal sealed class GroundCoverGpuBindings
{
    readonly GroundCoverBatch _cover;
    readonly MeshHandle[]?[] _parts;
    readonly FoliageMotionProfile[] _motion;
    readonly bool[] _casts;

    public GroundCoverGpuBindings(GroundCoverBatch cover)
    {
        _cover = cover;
        _parts = new MeshHandle[cover.ModelCount][];
        _motion = new FoliageMotionProfile[cover.ModelCount];
        _casts = new bool[cover.ModelCount];
    }

    public bool Refresh(IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes,
        GroundCoverRenderOptions? options = null)
    {
        bool changed = false;
        for (int model = 0; model < _parts.Length; model++)
        {
            string id = _cover.ModelId(model);
            if (!meshes.TryGetValue(id, out IReadOnlyList<MeshHandle>? live)) live = null;
            var policy = options is null ? default : GroundCoverModelPolicy.Resolve(options, id);
            FoliageMotionProfile motion = policy.Motion;
            bool casts = policy.CastShadows;
            bool sameParts = SameParts(_parts[model], live);
            if (sameParts && (_parts[model] is null || (_motion[model] == motion && _casts[model] == casts)))
                continue;
            if (!sameParts)
            {
                int count = live?.Count ?? 0;
                MeshHandle[]? snapshot = count == 0 ? null : new MeshHandle[count];
                for (int part = 0; part < count; part++) snapshot![part] = live![part];
                _parts[model] = snapshot;
            }
            _motion[model] = motion;
            _casts[model] = casts;
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
            int model = _cover.ModelIndex(i);
            MeshHandle[]? parts = model < 0 ? null : _parts[model];
            if (parts is null) continue;
            GroundCoverInstance placement = _cover[i];
            FoliageMotionProfile motion = _motion[model];
            bool casts = _casts[model];
            next += FoliageModelExpansion.WriteParts(parts, placement.Transform, placement.ThinningRank,
                motion, casts, result.AsSpan(next));
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
