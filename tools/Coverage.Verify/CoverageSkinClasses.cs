using System;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Classifies every triangle by complete raw versus published record bytes. A changed record can already
/// have intruded in the raw pose, so this classification makes no claim about causation.</summary>
public sealed class CoverageSkinClasses
{
    readonly bool[] inherited, changed, positionsChanged, attributesOnly;
    public CoverageSkinClasses(ReadOnlySpan<uint> indices, ReadOnlySpan<ModelVertex> raw, ReadOnlySpan<ModelVertex> published)
    {
        if (indices.Length % 3 != 0 || raw.Length != published.Length)
            throw new ArgumentException("Classification requires complete triangles and matching vertex records.");
        inherited = new bool[indices.Length / 3]; changed = new bool[inherited.Length];
        positionsChanged = new bool[inherited.Length]; attributesOnly = new bool[inherited.Length];
        for (int triangle = 0; triangle < inherited.Length; triangle++)
        {
            bool different = false, moved = false;
            for (int corner = 0; corner < 3; corner++)
            {
                uint index = indices[triangle * 3 + corner];
                if (index >= raw.Length) throw new ArgumentException("Triangle index is outside the supplied records.", nameof(indices));
                ref readonly var before = ref raw[(int)index];
                ref readonly var after = ref published[(int)index];
                different |= !Same(before, after);
                moved |= !Same(before.Position, after.Position);
            }
            inherited[triangle] = !different;
            changed[triangle] = different;
            positionsChanged[triangle] = moved;
            attributesOnly[triangle] = different && !moved;
            if (different) ChangedTriangleCount++;
        }
    }
    public ReadOnlySpan<bool> Inherited => inherited;
    public ReadOnlySpan<bool> ChangedRecords => changed;
    public ReadOnlySpan<bool> PositionsChanged => positionsChanged;
    public ReadOnlySpan<bool> AttributesOnly => attributesOnly;
    public int ChangedTriangleCount { get; }
    public int InheritedTriangleCount => inherited.Length - ChangedTriangleCount;
    static bool Same<T>(in T first, in T second) where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in first)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in second)));
}
