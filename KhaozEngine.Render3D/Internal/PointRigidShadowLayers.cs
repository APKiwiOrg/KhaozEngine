using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Owner-bound knowledge of the geometry actually stored in each base row.</summary>
internal sealed class PointRigidShadowLayers
{
    struct Row
    {
        public long Owner;
        public bool Known;
        public bool ContainsTransient;
        public bool HadMovingRigid;
        public long StaticSignature;
    }

    Row[] _rows = Array.Empty<Row>();

    internal void Reset(int capacity) => _rows = new Row[capacity];

    internal PointRigidBaseWork Plan(int slot, long owner, bool rendered, long staticSignature,
        bool movingRigid, bool skinned, bool hasTransient)
    {
        ref Row row = ref _rows[slot];
        if (!rendered || !row.Known || row.Owner != owner)
            row = new Row { Owner = owner, Known = true };
        bool fallback = !hasTransient && (movingRigid || (row.ContainsTransient && skinned));
        bool transition = row.ContainsTransient || (rendered && (movingRigid || row.HadMovingRigid)
            && row.StaticSignature != staticSignature);
        row.HadMovingRigid = movingRigid;
        return fallback ? PointRigidBaseWork.Fallback
            : transition ? PointRigidBaseWork.Transition : PointRigidBaseWork.None;
    }

    internal void Rendered(int slot, long staticSignature, bool containsTransient)
    {
        _rows[slot].StaticSignature = staticSignature;
        _rows[slot].ContainsTransient = containsTransient;
    }
}

internal enum PointRigidBaseWork : byte { None, Fallback, Transition }
