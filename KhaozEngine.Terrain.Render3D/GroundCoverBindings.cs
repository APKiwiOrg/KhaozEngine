using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

// Model bindings belong to one submission. Cached placements outlive mesh unloads and replacements,
// so only the model indices are persistent and the live handles are resolved again each frame.
internal readonly struct GroundCoverBindings : IDisposable
{
    struct Binding
    {
        public bool Resolved;
        public IReadOnlyList<MeshHandle>? Parts;
    }

    readonly GroundCoverBatch? _batch;
    readonly IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> _meshes;
    readonly Binding[]? _bindings;

    public GroundCoverBindings(GroundCoverBatch? batch,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes)
    {
        _batch = batch;
        _meshes = meshes;
        _bindings = batch is { ModelCount: > 0 } ? ArrayPool<Binding>.Shared.Rent(batch.ModelCount) : null;
        if (_bindings is not null) _bindings.AsSpan(0, batch!.ModelCount).Clear();
    }

    public IReadOnlyList<MeshHandle>? Resolve(int instanceIndex, string modelId)
    {
        int modelIndex = _batch?.ModelIndex(instanceIndex) ?? -1;
        if (modelIndex < 0)
            return _meshes.TryGetValue(modelId, out IReadOnlyList<MeshHandle>? parts) ? parts : null;
        ref Binding binding = ref _bindings![modelIndex];
        if (!binding.Resolved)
        {
            if (!_meshes.TryGetValue(_batch!.ModelId(modelIndex), out binding.Parts)) binding.Parts = null;
            binding.Resolved = true;
        }
        return binding.Parts;
    }

    public void Dispose()
    {
        if (_bindings is not null) ArrayPool<Binding>.Shared.Return(_bindings, clearArray: true);
    }
}
