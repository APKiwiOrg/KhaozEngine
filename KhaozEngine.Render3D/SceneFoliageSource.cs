using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed class SceneFoliageModelBinding
{
    public string ModelId { get; }
    public IReadOnlyList<MeshHandle> Parts { get; }
    public FoliageMotionProfile Motion { get; }
    public bool CastShadows { get; }
    public SceneFoliageModelBinding(string modelId, IReadOnlyList<MeshHandle> parts, FoliageMotionProfile motion, bool castShadows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId); ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0 || parts.Any(p => p.Generation <= 0)) throw new ArgumentException("Foliage model needs published mesh parts.", nameof(parts));
        ModelId = modelId; Parts = Array.AsReadOnly(parts.ToArray()); Motion = motion; CastShadows = castShadows;
    }
}

public sealed class SceneFoliageSource
{
    readonly Matrix4x4[] _transforms;
    readonly float[] _ranks;
    readonly int[] _indices;
    public RenderAnchor Anchor { get; }
    public ReadOnlyMemory<Matrix4x4> WorldTransforms => _transforms.ToArray();
    public ReadOnlyMemory<float> Ranks => _ranks.ToArray();
    public ReadOnlyMemory<int> ModelIndices => _indices.ToArray();
    public IReadOnlyList<SceneFoliageModelBinding> Models { get; }
    internal ReadOnlySpan<Matrix4x4> BorrowTransforms => _transforms;
    internal ReadOnlySpan<float> BorrowRanks => _ranks;
    internal ReadOnlySpan<int> BorrowModelIndices => _indices;

    public SceneFoliageSource(RenderAnchor anchor, ReadOnlyMemory<Matrix4x4> worldTransforms,
        ReadOnlyMemory<float> ranks, ReadOnlyMemory<int> modelIndices, IReadOnlyList<SceneFoliageModelBinding> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _transforms = worldTransforms.ToArray(); _ranks = ranks.ToArray(); _indices = modelIndices.ToArray();
        Models = Array.AsReadOnly(models.ToArray()); Anchor = anchor;
        if (_transforms.Length != _ranks.Length || _indices.Length != _ranks.Length ||
            Models.Any(m => m is null) || Models.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() != Models.Count ||
            _indices.Any(i => i < 0 || i >= Models.Count) || _ranks.Any(r => !float.IsFinite(r) || r < 0 || r > 1))
            throw new ArgumentException("Foliage rows require matching transforms, ranks and valid model indices.");
    }
}
