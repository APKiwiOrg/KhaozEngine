using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The complete point-light receiver list and the transaction that grows its GPU buffer.</summary>
internal sealed partial class ModelRenderer
{
    internal const uint PointLightRecordBytes = 48;
    const int InitialPointLightCapacity = MaxPointLights;
    const int MaximumPointLightCapacity = (int)(uint.MaxValue / PointLightRecordBytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointLightGpuData
    {
        internal Vector4 PosRadius;
        internal Vector4 ColorIntensity;
        internal Vector4 ShadowParams;
    }

    IGpuBuffer _pointLightBuffer = null!;
    PointLightGpuData[] _pointLightRecords = Array.Empty<PointLightGpuData>();
    int _pointLightCapacity;

    internal IGpuBuffer PointLightBuffer => _pointLightBuffer;
    internal int UploadedPointLightCount { get; private set; }
    internal bool RequiresPointLightGrowth(int required) => required > _pointLightCapacity;

    void CreatePointLightBuffer(IGpuResourceFactory factory)
    {
        _pointLightCapacity = InitialPointLightCapacity;
        _pointLightRecords = new PointLightGpuData[_pointLightCapacity];
        _pointLightBuffer = factory.CreateBuffer(new GpuBufferDescription(
            checked((uint)(_pointLightCapacity * PointLightRecordBytes)),
            GpuBufferUsage.StructuredBufferReadOnly, PointLightRecordBytes));
    }

    internal static int BuildPointLightRecords(ReadOnlySpan<PointLightData> lights,
        Span<PointLightGpuData> records, ReadOnlySpan<int> baseRows, ReadOnlySpan<int> transientRows,
        float bias, float slopeBias,
        Vector3 renderOrigin = default)
    {
        if (records.Length < lights.Length)
            throw new ArgumentException("The point-light record destination is shorter than the submitted list.",
                nameof(records));

        var origin = new Vector4(renderOrigin, 0f);
        for (int i = 0; i < lights.Length; i++)
        {
            float baseRow = i < baseRows.Length ? baseRows[i] : -1f;
            float transientRow = i < transientRows.Length ? transientRows[i] : -1f;
            records[i] = new PointLightGpuData
            {
                PosRadius = lights[i].PosRadius - origin,
                ColorIntensity = lights[i].ColorIntensity,
                ShadowParams = new Vector4(baseRow, bias, slopeBias, transientRow),
            };
        }
        records[lights.Length..].Clear();
        return lights.Length;
    }

    static int ResolvePointLightCapacity(int current, int required)
    {
        if (required < 0 || required > MaximumPointLightCapacity)
            throw new ArgumentOutOfRangeException(nameof(required), required,
                $"A point-light structured buffer can hold at most {MaximumPointLightCapacity} 48-byte records.");
        if (required <= current) return current;

        int capacity = Math.Max(current, InitialPointLightCapacity);
        while (capacity < required)
        {
            if (capacity > MaximumPointLightCapacity / 2)
                return MaximumPointLightCapacity;
            capacity *= 2;
        }
        return capacity;
    }

    /// <summary>Grow the shared receiver buffer and atomically replace every resource set that captured it.</summary>
    internal void EnsurePointLightCapacity(int required,
        IReadOnlyList<IGpuResourceSet> liveMaterialSets,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        int capacity = ResolvePointLightCapacity(_pointLightCapacity, required);
        if (capacity == _pointLightCapacity) return;

        var records = new PointLightGpuData[capacity];
        _pointLightRecords.AsSpan().CopyTo(records);
        IGpuBuffer? buffer = null;
        IGpuResourceSet? splatFrameSet = null;
        IGpuResourceSet? tileGroundFrameSet = null;
        IGpuResourceSet? skinnedMainSet = null;
        var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
        var replacementBindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);

        try
        {
            buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                checked((uint)(capacity * PointLightRecordBytes)),
                GpuBufferUsage.StructuredBufferReadOnly, PointLightRecordBytes));
            _gd.WaitForIdle();
            AddReplacement(_defaultSet, _shadowMap.ShadowTexture, _pointShadowTexture, buffer,
                replacements, replacementBindings);
            AddReplacement(_skinnedDefaultFragSet, _shadowMap.ShadowTexture, _pointShadowTexture, buffer,
                replacements, replacementBindings);
            foreach (IGpuResourceSet set in liveMaterialSets)
                AddReplacement(set, _shadowMap.ShadowTexture, _pointShadowTexture, buffer,
                    replacements, replacementBindings);

            splatFrameSet = _gd.Factory.CreateResourceSet(
                new GpuResourceSetDescription(_splatFrameLayout, _ubo, buffer, _pointLightClusterBuffer));
            tileGroundFrameSet = _gd.Factory.CreateResourceSet(
                new GpuResourceSetDescription(_tileGroundFrameLayout, _ubo, buffer, _pointLightClusterBuffer));
            if (_skinnedMainSet is not null)
            {
                skinnedMainSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                    _skinnedMainLayout, _ubo, buffer, _pointLightClusterBuffer,
                    new GpuBufferRange(_skinnedMainUbo!, 0, SkinnedMainSlotBytes)));
            }
        }
        catch
        {
            foreach (IGpuResourceSet replacement in replacements.Values) replacement.Dispose();
            splatFrameSet?.Dispose();
            tileGroundFrameSet?.Dispose();
            skinnedMainSet?.Dispose();
            buffer?.Dispose();
            throw;
        }

        IGpuBuffer oldBuffer = _pointLightBuffer;
        IGpuResourceSet oldSplatFrameSet = _splatFrameSet;
        IGpuResourceSet oldTileGroundFrameSet = _tileGroundFrameSet;
        IGpuResourceSet? oldSkinnedMainSet = _skinnedMainSet;

        _pointLightBuffer = buffer;
        _pointLightRecords = records;
        _pointLightCapacity = capacity;
        _splatFrameSet = splatFrameSet;
        _tileGroundFrameSet = tileGroundFrameSet;
        if (oldSkinnedMainSet is not null) _skinnedMainSet = skinnedMainSet;
        CommitShadowSamplingSets(replacements, replacementBindings, commitMaterialSets);

        oldSkinnedMainSet?.Dispose();
        oldSplatFrameSet.Dispose();
        oldTileGroundFrameSet.Dispose();
        oldBuffer.Dispose();
    }

    void UploadPointLights(IGpuCommandList cl, ReadOnlySpan<PointLightData> lights, Vector3 renderOrigin)
    {
        if (lights.Length > _pointLightCapacity)
            throw new InvalidOperationException(
                $"The frame submitted {lights.Length} point lights to a buffer with capacity {_pointLightCapacity}. "
                + "Grow the point-light buffer during frame preparation before recording receiver draws.");
        UploadedPointLightCount = BuildPointLightRecords(lights, _pointLightRecords,
            _pointShadowSlots.AsSpan(0, _pointShadowSlotCount),
            _pointShadowTransientSlots.AsSpan(0, _pointShadowSlotCount),
            _pointShadowBias, _pointShadowSlopeBias, renderOrigin);
        if (UploadedPointLightCount > 0)
            cl.UpdateBuffer(_pointLightBuffer, 0, _pointLightRecords.AsSpan(0, UploadedPointLightCount));
    }
}
