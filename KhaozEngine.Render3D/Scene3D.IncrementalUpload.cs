using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static long _nextResourceId;
    readonly Dictionary<SceneResourceHandle, SceneResourceUpload> _sceneResources = new();
    readonly HashSet<SceneResourceUpload> _sceneUploads = new();
    readonly HashSet<MeshHandle> _publishedResourceMeshes = new();
    bool _sceneResourcesDisposed;

    /// <summary>Measure complete aligned GPU storage, mip chains and preparation/staging peaks without factories
    /// or payload allocation. The returned demand is the aggregate reservation's exact claim token.</summary>
    public SceneUploadDemand MeasureResourceUpload(SceneUploadData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MeasureResourceUpload(data.Description, data.InputDigest);
    }
    public SceneUploadDemand MeasureResourceUpload(SceneUploadDescription data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MeasureResourceUpload(data, SceneUploadFingerprint.Compute(data));
    }
    /// <summary>Price unresolved foliage by declared part counts. This capacity has no claim token and never
    /// relaxes live-handle validation on the preparation path.</summary>
    public SceneUploadCapacity MeasureResourceUploadCapacity(SceneUploadDescription data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MeasureResourceUpload(data, string.Empty, requireLiveParts: false).Capacity;
    }
    SceneUploadDemand MeasureResourceUpload(SceneUploadDescription data, string digest, bool requireLiveParts = true)
    {
        ObjectDisposedException.ThrowIf(_sceneResourcesDisposed, this);
        bool mesh = data.Mesh is not null, foliage = data.Foliage is not null;
        if ((mesh && foliage) || (!mesh && (data.Maps.HasValue || !data.ClipData.IsEmpty || data.GroundMaterial is not null)) ||
            (data.GroundMaterial is not null && (data.Maps.HasValue || !data.ClipData.IsEmpty)))
            throw new ArgumentException("Resource payload kinds cannot be combined.", nameof(data));
        long gpu = 0, staging = 0, cpu = 4096, transfer = 0;
        void Buffer(long bytes)
        {
            if (bytes <= 0 || bytes > uint.MaxValue - 3) throw new ArgumentOutOfRangeException(nameof(data));
            gpu = checked(gpu + Align(bytes, 4)); staging = checked(staging + Align(bytes, 256));
            transfer = checked(transfer + bytes); cpu = checked(cpu + bytes + 256);
        }
        void Texture(int width, int height, int layers)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Texture dimensions must be positive.", nameof(data));
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            {
                long bytes = checked((long)w * h * 4 * layers);
                gpu = checked(gpu + bytes); transfer = checked(transfer + bytes);
                staging = checked(staging + Align((long)w * 4, 256) * h * layers);
                cpu = checked(cpu + bytes * 2 + 256 * layers); // Mip chain plus bounded texture copy staging.
                if (w == 1 && h == 1) break;
            }
        }
        void Image(DecodedImage? image)
        {
            if (image is not { } i) return;
            if (i.Rgba is null || i.Rgba.LongLength != checked((long)i.Width * i.Height * 4))
                throw new ArgumentException("Texture data must match its RGBA8 dimensions.", nameof(data));
            Texture(i.Width, i.Height, 1);
            cpu = checked(cpu + i.Rgba.LongLength); // Detached base image remains live beside mip preparation.
        }
        if (data.Mesh is { } m)
        {
            if (m.Indices32.Length % 3 != 0) throw new ArgumentException("Mesh requires complete triangles.", nameof(data));
            foreach (var index in m.Indices32)
                if (index >= m.Vertices.Length) throw new ArgumentException("Mesh index is outside its vertex stream.", nameof(data));
            cpu = checked(cpu + m.Vertices.LongLength * ModelVertex.SizeInBytes + m.Indices32.LongLength * 4 + data.ClipData.Length);
            Buffer(checked(m.Vertices.LongLength * ModelVertex.SizeInBytes));
            Buffer(checked(m.Vertices.LongLength * 12));
            Buffer(checked(m.Indices32.LongLength * (m.IndexFormat == GpuIndexFormat.UInt16 ? 2 : 4)));
            // Outline normal welding retains positions, groups, dictionary capacity and output streams.
            cpu = checked(cpu + m.Vertices.LongLength * 512 + m.Indices32.LongLength * 4);
            if (data.Maps is { } maps) { Image(maps.Albedo); Image(maps.Normal); Image(maps.Roughness); }
            if (!data.ClipData.IsEmpty)
            {
                WaterSurfaceDomain.Validate(data.ClipData.Span);
                var bounds = MeshBounds.FromVertices(m.Vertices);
                if (bounds.Min.Y != bounds.Max.Y) throw new ArgumentException("Water requires a planar mesh.", nameof(data));
                Buffer(data.ClipData.Length);
            }
            if (data.GroundMaterial is { } ground)
            {
                if (ground.Layers.Count is < 1 or > TileGroundMaterialConfig.MaxMaterials ||
                    ground.Layers.Any(l => l.AlbedoRgba is { } pixels && pixels.LongLength != checked((long)ground.Width * ground.Height * 4)))
                    throw new ArgumentException("Ground layers require equal RGBA8 dimensions.", nameof(data));
                Texture(ground.Width, ground.Height, ground.Layers.Count);
                // Detached layers and temporary synthesized white layers coexist during detachment.
                cpu = checked(cpu + (long)ground.Width * ground.Height * 4 * ground.Layers.Count * 2);
                Buffer(TileGroundMaterialConfig.ParamsBytes);
            }
        }
        long expanded = 0;
        if (data.Foliage is { } source)
        {
            if (!Enum.IsDefined(source.TransformSpace) || source.Transforms.Length != source.Ranks.Length || source.ModelIndices.Length != source.Ranks.Length)
                throw new ArgumentException("Invalid foliage rows or models.", nameof(data));
            foreach (float rank in source.Ranks.Span)
                if (!float.IsFinite(rank) || rank < 0 || rank >= 1) throw new ArgumentException("Invalid foliage rank.", nameof(data));
            for (int a = 0; a < source.Models.Count; a++)
                for (int b = a + 1; b < source.Models.Count; b++)
                    if (source.Models[a].ModelId == source.Models[b].ModelId) throw new ArgumentException("Duplicate foliage model.", nameof(data));
            for (int model = 0; model < source.Models.Count; model++)
            {
                var parts = source.Models[model].Parts;
                // Detached model references and constructor validation metadata coexist with prepared streams.
                cpu = checked(cpu + 256L + parts.Count * 16L);
                for (int part = 0; part < parts.Count; part++)
                    if (requireLiveParts && FoliageBounds(parts[part]) is null) throw new ArgumentException("Foliage requires live scene mesh parts.", nameof(data));
            }
            var indices = source.ModelIndices.Span;
            for (int row = 0; row < indices.Length; row++)
            {
                if (indices[row] < 0 || indices[row] >= source.Models.Count) throw new ArgumentException("Invalid foliage model index.", nameof(data));
                expanded = checked(expanded + source.Models[indices[row]].Parts.Count);
            }
            if (expanded > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(data));
            if (expanded > 0) Buffer(checked(expanded * ScenePreparedFoliage.InstanceStride));
            Buffer(256); // Per-resource anchor uniform, independent of the legacy foliage block.
            cpu = checked(cpu + expanded * 1024 + source.Transforms.Length * 144L);
        }
        return new(digest, gpu, staging, cpu, gpu, transfer, SceneResourceCpuInventory.UpperBound(data, expanded));
    }

    internal static long Align(long bytes, int alignment) => checked((bytes + alignment - 1) / alignment * alignment);

    /// <summary>Claim the measured aggregate lease before expansion, staging or any GPU factory.</summary>
    public SceneResourceUpload PrepareResourceUpload(SceneUploadData data, ISceneUploadReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var demand = MeasureResourceUpload(data);
        if (demand != reservation.Demand) throw new ArgumentException("Upload input or measured demand changed.", nameof(reservation));
        try { reservation.Claim(demand); }
        catch { reservation.Dispose(); throw; }
        return PrepareClaimedResourceUpload(data, demand, reservation);
    }

    /// <summary>Validate and claim borrowed inputs before allocating detached payloads or synthesized images.</summary>
    public SceneResourceUpload PrepareResourceUpload(SceneUploadDescription description, ISceneUploadReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var demand = MeasureResourceUpload(description);
        if (demand != reservation.Demand) throw new ArgumentException("Upload input or measured demand changed.", nameof(reservation));
        try { reservation.Claim(demand); }
        catch { reservation.Dispose(); throw; }
        SceneUploadData data;
        try
        {
            data = description.Detach();
            if (MeasureResourceUpload(data) != demand) throw new ArgumentException("Borrowed upload inputs changed during detachment.", nameof(description));
        }
        catch { reservation.Retiring(); reservation.Released(); throw; }
        return PrepareClaimedResourceUpload(data, demand, reservation);
    }
    SceneResourceUpload PrepareClaimedResourceUpload(SceneUploadData data, SceneUploadDemand demand, ISceneUploadReservation reservation)
    {
        var upload = new SceneResourceUpload(this, _gd, data, demand, reservation);
        _sceneUploads.Add(upload);
        try { upload.Prepare(); return upload; }
        catch { upload.Dispose(); throw; }
    }

    internal SceneResourceHandle PublishResource(SceneResourceUpload upload)
    {
        ObjectDisposedException.ThrowIf(_sceneResourcesDisposed, this);
        var handle = new SceneResourceHandle(Interlocked.Increment(ref _nextResourceId));
        _sceneResources.Add(handle, upload);
        _sceneUploads.Remove(upload);
        return handle;
    }
    internal void ForgetFailedPublication(SceneResourceHandle handle) => _sceneResources.Remove(handle);
    internal SceneResourceUpload ResourceOf(SceneResourceHandle handle)
    {
        ObjectDisposedException.ThrowIf(_sceneResourcesDisposed, this);
        if (handle.Value <= 0 || !_sceneResources.TryGetValue(handle, out var resource))
            throw new ArgumentException("Resource is unready, released or belongs to another scene.", nameof(handle));
        return resource;
    }
    internal MeshHandle ResourceMesh(SceneResourceHandle handle)
    {
        var mesh = ResourceOf(handle).PublishedMesh;
        if (mesh.Generation <= 0 || !_slots.IsValid(mesh.Index, mesh.Generation))
            throw new ArgumentException("Resource does not own a live mesh.", nameof(handle));
        return mesh;
    }
    /// <summary>Release a published resource once. Its lease remains charged behind actual GPU retirement.</summary>
    public void ReleaseResource(SceneResourceHandle handle)
    {
        if (handle.Value == 0 || _sceneResourcesDisposed) return;
        if (!_sceneResources.Remove(handle, out var resource))
            throw new ArgumentException("Resource is released or belongs to another scene.", nameof(handle));
        resource.Retire();
    }
    internal void RetireUpload(SceneResourceUpload upload, IDisposable retirement)
    {
        _sceneUploads.Remove(upload);
        _retired.Retire(retirement);
    }
    void DisposeSceneResources()
    {
        foreach (var resource in _sceneResources.Values) resource.Retire();
        _sceneResources.Clear();
        while (_sceneUploads.Count > 0)
        {
            using var iterator = _sceneUploads.GetEnumerator();
            iterator.MoveNext(); iterator.Current.Dispose();
        }
        _sceneResourcesDisposed = true;
    }
}
