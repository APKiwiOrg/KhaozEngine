using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Terrain;

/// <summary>Immutable original instance columns. Public buffer boundaries are detached snapshots.</summary>
public sealed class GroundCoverInstanceColumns
{
    readonly Matrix4x4[] _transforms;
    readonly Vector3[] _positions;
    readonly float[] _ranks;
    readonly int[] _modelIndices;
    readonly string[] _modelIds;

    public GroundCoverInstanceColumns(ReadOnlyMemory<Matrix4x4> transforms, ReadOnlyMemory<Vector3> positions,
        ReadOnlyMemory<float> ranks, ReadOnlyMemory<int> modelIndices, IReadOnlyList<string> modelIds)
        : this(transforms.ToArray(), positions.ToArray(), ranks.ToArray(), modelIndices.ToArray(), Copy(modelIds), true) => Validate();

    GroundCoverInstanceColumns(Matrix4x4[] transforms, Vector3[] positions, float[] ranks, int[] modelIndices, string[] modelIds, bool owned)
    { _transforms = transforms; _positions = positions; _ranks = ranks; _modelIndices = modelIndices; _modelIds = modelIds; }

    // The one-shot producer has finished all writes before transferring these exact-size arrays.
    internal static GroundCoverInstanceColumns TakeOwnership(Matrix4x4[] transforms, Vector3[] positions,
        float[] ranks, int[] modelIndices, string[] modelIds) => new(transforms, positions, ranks, modelIndices, modelIds, true);

    static string[] Copy(IReadOnlyList<string> ids)
    { ArgumentNullException.ThrowIfNull(ids); return ids.ToArray(); }
    void Validate()
    {
        if (_transforms.Length != _positions.Length || _transforms.Length != _ranks.Length || _transforms.Length != _modelIndices.Length ||
            _modelIds.Any(string.IsNullOrWhiteSpace) || _modelIds.Distinct(StringComparer.Ordinal).Count() != _modelIds.Length ||
            _modelIndices.Any(i => i < 0 || i >= _modelIds.Length) || _ranks.Any(r => !float.IsFinite(r) || r < 0 || r > 1))
            throw new ArgumentException("Instance columns require equal lengths, finite ranks and unique valid model bindings.");
    }
    public int Count => _transforms.Length;
    public int ModelCount => _modelIds.Length;
    public GroundCoverInstance this[int index] => new(_modelIds[_modelIndices[index]], _positions[index], _transforms[index], _ranks[index]);
    public int ModelIndex(int index) => _modelIndices[index];
    public string ModelId(int index) => _modelIds[index];
    public IReadOnlyList<string> ModelIds => Array.AsReadOnly(_modelIds.ToArray());
    public ReadOnlyMemory<Matrix4x4> Transforms => _transforms.ToArray();
    public ReadOnlyMemory<Vector3> Positions => _positions.ToArray();
    public ReadOnlyMemory<float> Ranks => _ranks.ToArray();
    public ReadOnlyMemory<int> ModelIndices => _modelIndices.ToArray();
    internal ReadOnlyMemory<Matrix4x4> BorrowTransforms => _transforms;
    internal ReadOnlyMemory<Vector3> BorrowPositions => _positions;
    internal ReadOnlyMemory<float> BorrowRanks => _ranks;
    internal ReadOnlyMemory<int> BorrowModelIndices => _modelIndices;
    internal ReadOnlySpan<string> BorrowModelIds => _modelIds;
}
