using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Render3D;

public readonly record struct SceneFoliagePatch(double CellX, double CellZ, MeshHandle Mesh, bool CastShadows, int Start, int Count);
public readonly record struct SceneFoliageExpandedPart(int SourceRow, int ModelIndex, int PartIndex, MeshHandle Mesh);

/// <summary>The renderer's immutable expansion and packing product. Task-specific producers own its construction.</summary>
public sealed class ScenePreparedFoliage
{
    public const int InstanceStride = 96;
    readonly int[] _rows;
    readonly byte[] _packed;
    public RenderAnchor Anchor { get; }
    public ReadOnlyMemory<int> ExpandedSourceRows => _rows.ToArray();
    public IReadOnlyList<SceneFoliageExpandedPart> ExpandedParts { get; }
    public IReadOnlyList<SceneFoliagePatch> Patches { get; }
    public ReadOnlyMemory<byte> PackedInstances => _packed.ToArray();
    public SceneUploadDemand Demand { get; }
    internal Internal.FoliagePatchLayout? Layout { get; set; }
    internal Internal.FoliagePointShadowStream? PointStream { get; set; }
    internal ReadOnlySpan<int> BorrowExpandedSourceRows => _rows;
    internal ReadOnlyMemory<byte> BorrowPackedInstances => _packed;

    internal ScenePreparedFoliage(RenderAnchor anchor, IReadOnlyList<SceneFoliageExpandedPart> expandedParts,
        IReadOnlyList<SceneFoliagePatch> patches, ReadOnlyMemory<byte> packedInstances, SceneUploadDemand demand)
    {
        ArgumentNullException.ThrowIfNull(expandedParts); ArgumentNullException.ThrowIfNull(patches); ArgumentNullException.ThrowIfNull(demand);
        Anchor = anchor; Demand = demand;
        ExpandedParts = Array.AsReadOnly(expandedParts.ToArray()); Patches = Array.AsReadOnly(patches.ToArray());
        _rows = expandedParts.Select(p => p.SourceRow).ToArray(); _packed = packedInstances.ToArray();
        if (_packed.LongLength != (long)_rows.Length * InstanceStride ||
            patches.Any(p => p.Start < 0 || p.Count < 0 || (long)p.Start + p.Count > _rows.Length))
            throw new ArgumentException("Prepared foliage layout is inconsistent.");
    }
}
