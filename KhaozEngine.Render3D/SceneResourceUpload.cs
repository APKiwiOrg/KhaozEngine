using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>One admitted upload. Publication transfers ownership to the scene. Cancellation retires behind its
/// completion barrier and releases its remaining lease only after destruction.
/// <para>
/// Its material maps are the scene's resident textures (issue #1776): one per image identity, shared with every other
/// upload and convenience load that names the image, and held by reference from preparation until the upload is
/// cancelled or its resource is released. The first upload to name an image creates the texture and its claim pays
/// for it. Every upload naming the image writes its level-0 rows after its own buffers, so an image a batch shares is
/// written once, and the first to finish generates its chain in the recording its completion fence proves. A glow
/// owns a parameter block written with the upload's buffers.
/// </para></summary>
public sealed class SceneResourceUpload : IDisposable
{
    readonly Scene3D _scene;
    readonly IGpuDevice _device;
    readonly ISceneUploadReservation _reservation;
    readonly SceneUploadLease _lease;
    readonly long _meshOrder;
    SceneUploadData? _data;
    SceneUploadPublication? _publication;
    readonly List<SceneUploadTransfer> _transfers = new();
    readonly List<IDisposable> _owned = new();
    // The resident textures this upload holds a reference to, and the fills of those still owed writes. The ones it
    // created survive under its reservation until destroyed, so they count in its retained inventory.
    readonly List<Scene3D.TextureHandle> _resident = new();
    readonly List<MaterialTextureFill> _fills = new();
    int _created;
    IGpuBuffer? _vertices, _normals, _indices, _clip, _foliage, _groundParams, _anchor, _materialParams;
    Scene3D.TextureHandle _albedo, _normal, _roughness, _emissive;
    IGpuTexture? _groundAlbedo;
    IGpuFence? _completion;
    IGpuCommandList? _barrier;
    int _nextTransfer;
    bool _submitted, _published, _disposed, _retiring, _transferSettled;
    internal bool HoldsInputsForTests => _data is not null;
    internal int TransferCapacityForTests => _transfers.Capacity;
    internal bool HoldsCompletionResourcesForTests => _barrier is not null || _completion is not null;
    internal int OwnedCapacityForTests => _owned.Capacity;
    internal MeshHandle PublishedMesh { get; private set; }
    internal FoliageBatch? PublishedFoliage { get; private set; }
    public SceneUploadDemand Demand { get; }
    public ScenePreparedFoliage? PreparedFoliage { get; private set; }

    internal SceneResourceUpload(Scene3D scene, IGpuDevice device, SceneUploadData data,
        SceneUploadDemand demand, ISceneUploadReservation reservation)
    {
        _scene = scene; _device = device; _data = data; Demand = demand; _reservation = reservation;
        _lease = new SceneUploadLease(reservation);
        _meshOrder = data.BorrowMesh is null ? 0 : scene.ReserveMeshOrder();
        if (data.BorrowMesh is { } mesh)
            _publication = SceneUploadPublication.From(mesh) with
            {
                AlphaCutoff = data.BorrowMaps?.AlphaCutoff ?? 0,
                Ground = data.GroundMaterial is not null,
                Sampler = data.GroundMaterial?.Sampler,
                OpaqueFoliage = FoliageMaterial.IsOpaque(data.BorrowMaps is { } maps && HasMaterial(maps),
                    data.BorrowMaps?.AlphaCutoff ?? 0, mesh.Vertices)
            };
    }

    // Whether the maps bind a model material set: any map, or a glow, which binds its own parameter block.
    static bool HasMaterial(in GltfMaterialMaps maps) =>
        maps.Albedo is not null || maps.Normal is not null || maps.Roughness is not null || maps.Emissive is not null ||
        maps.EmissiveFactor != Vector3.Zero;

    bool BindsMaterial => _albedo.IsValid || _normal.IsValid || _roughness.IsValid || _emissive.IsValid || _materialParams is not null;

    internal void Prepare()
    {
        var data = _data!;
        if (data.BorrowMesh is { } mesh)
        {
            _vertices = Buffer(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).ToArray(), GpuBufferUsage.VertexBuffer);
            _normals = Buffer(MemoryMarshal.AsBytes(OutlineNormalBuilder.Build(mesh.Vertices, mesh.Indices32).AsSpan()).ToArray(), GpuBufferUsage.VertexBuffer);
            _indices = mesh.IndexFormat == GpuIndexFormat.UInt32
                ? Buffer(MemoryMarshal.AsBytes(mesh.Indices32.AsSpan()).ToArray(), GpuBufferUsage.IndexBuffer)
                : Buffer(MemoryMarshal.AsBytes(MeshIndices.Narrow(mesh.Indices32).AsSpan()).ToArray(), GpuBufferUsage.IndexBuffer);
            if (data.BorrowMaps is { } maps)
            {
                _albedo = Resident(maps.Albedo); _normal = Resident(maps.Normal);
                _roughness = Resident(maps.Roughness); _emissive = Resident(maps.Emissive);
                if (maps.EmissiveFactor != Vector3.Zero)
                    _materialParams = Buffer(MaterialParams(maps.EmissiveFactor), GpuBufferUsage.UniformBuffer);
            }
            if (!data.BorrowClipData.IsEmpty) _clip = Buffer(data.BorrowClipData.ToArray(), GpuBufferUsage.StructuredBufferReadOnly, 16);
            if (data.GroundMaterial is { } ground)
            {
                _groundAlbedo = Own(_device.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                    (uint)ground.Width, (uint)ground.Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled,
                    (uint)ground.BorrowLayers.Count, TileGroundMaterialConfig.MipLevelCount(ground.Width, ground.Height))));
                for (int i = 0; i < ground.BorrowLayers.Count; i++)
                    Mips(_groundAlbedo, ground.BorrowLayers[i].AlbedoRgba, ground.Width, ground.Height, (uint)i);
                _groundParams = Buffer(MemoryMarshal.AsBytes(TileGroundMaterialConfig.BuildParams(
                    ground.BorrowLayers, ground.BaseSpecularStrength).AsSpan()).ToArray(), GpuBufferUsage.UniformBuffer);
            }
        }
        if (data.Foliage is { } source)
        {
            PreparedFoliage = _scene.PrepareFoliageUpload(source, Demand);
            _anchor = Buffer(new byte[256], GpuBufferUsage.UniformBuffer);
            if (!PreparedFoliage.BorrowPackedInstances.IsEmpty)
                _foliage = Buffer(PreparedFoliage.BorrowPackedInstances.ToArray(), GpuBufferUsage.VertexBuffer);
        }
        if (_device.Capabilities.SupportsCompletionFences)
        {
            _completion = Own(_device.Factory.CreateFence());
            _barrier = Own(_device.Factory.CreateCommandList());
        }
        if (_transfers.Count == 0 && _fills.TrueForAll(fill => fill.Written)) CompleteTransfer();
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
    IGpuBuffer Buffer(byte[] bytes, GpuBufferUsage usage, uint stride = 0)
    {
        var buffer = Own(_device.Factory.CreateBuffer(new(checked((uint)Scene3D.Align(bytes.Length, 4)), usage, stride)));
        _transfers.Add(new SceneUploadTransfer(buffer, bytes));
        return buffer;
    }

    // A reference to the scene's texture for a map, created by this upload when the scene does not hold its identity.
    Scene3D.TextureHandle Resident(DecodedImage? image)
    {
        if (image is not { } i) return default;
        (Scene3D.TextureHandle handle, MaterialTextureFill? fill, bool created) = _scene.AcquireAdmittedTexture(i, _lease);
        _resident.Add(handle);
        if (created) _created++;
        if (fill is not null && !_fills.Contains(fill)) _fills.Add(fill);
        return handle;
    }

    static byte[] MaterialParams(Vector3 emissive)
    {
        var block = new ModelRenderer.MaterialParamsUbo { Emissive = new Vector4(emissive, 0f) };
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref block, 1)).ToArray();
    }

    // A ground array is never shared, so it writes every level of its chain by bounded rows.
    void Mips(IGpuTexture texture, byte[] pixels, int width, int height, uint layer)
    {
        uint mip = 0;
        while (true)
        {
            _transfers.Add(new SceneUploadTransfer(texture, pixels, width, mip++, layer));
            if (width == 1 && height == 1) break;
            int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
            pixels = SceneUploadMips.Downsample(pixels, width, height, w, h);
            (width, height) = (w, h);
        }
    }

    /// <summary>True only when every copy completed, including the final GPU completion fence.</summary>
    public bool Ready
    {
        get
        {
            if (_disposed || !_submitted || _completion is { Signaled: false }) return false;
            if (!_transferSettled)
            {
                // Completion machinery is transfer workspace too. Some backends retain command arrays here.
                if (_barrier is not null) { _barrier.Dispose(); _owned.Remove(_barrier); _barrier = null; }
                if (_completion is not null) { _completion.Dispose(); _owned.Remove(_completion); _completion = null; }
                ReleaseInputs();
                int bindings = (_publication?.BindingCount(_albedo.IsValid, BindsMaterial) ?? 0) +
                    (PreparedFoliage is not null ? 1 : 0);
                _owned.Capacity = checked(_owned.Count + bindings);
                _reservation.TransferCompleted();
                _reservation.InputsReleased(SceneResourceCpuInventory.Measure(_owned, _publication.HasValue, PreparedFoliage,
                    bindings, _created));
                _transferSettled = true;
            }
            return true;
        }
    }

    /// <summary>Copy at most allowance bytes. Writes progress within bounded rows: this upload's buffers and ground
    /// array first, then the level-0 rows its shared material textures still owe. An allowance too small for one
    /// RGBA texel makes no progress. Admission is independent of this budget.</summary>
    public long Advance(long allowance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (allowance < 0) throw new ArgumentOutOfRangeException(nameof(allowance));
        if (_published || _submitted) return 0;
        long copied = 0;
        try
        {
            while (_nextTransfer < _transfers.Count && copied < allowance)
            {
                var transfer = _transfers[_nextTransfer];
                long written = transfer.Advance(_device, allowance - copied);
                copied += written;
                if (transfer.Finished) _nextTransfer++;
                else if (written == 0) break;
            }
            // A fill another upload began, or was cancelled out of, finishes here as readily as one this upload began.
            if (_nextTransfer == _transfers.Count)
                foreach (MaterialTextureFill fill in _fills)
                {
                    while (!fill.Written && copied < allowance)
                    {
                        long written = fill.Advance(_device, allowance - copied);
                        if (written == 0) break;
                        copied += written;
                    }
                    if (!fill.Written) break;
                }
            if (_nextTransfer == _transfers.Count && _fills.TrueForAll(fill => fill.Written)) CompleteTransfer();
            return copied;
        }
        catch { Dispose(); throw; }
    }

    void CompleteTransfer()
    {
        if (_submitted) return;
        if (_completion is not null)
        {
            using (GpuRecording.Open(_device, _barrier!, "Scene3D.ResourceUpload")) GenerateChains(_barrier!);
            _device.Submit(_barrier!, _completion);
        }
        else
        {
            // Without a completion fence a chain still needs a list of its own, and the drain proves it.
            IGpuCommandList? chains = null;
            try
            {
                if (!_fills.TrueForAll(fill => fill.ChainGenerated))
                {
                    chains = _device.Factory.CreateCommandList();
                    using (GpuRecording.Open(_device, chains, "Scene3D.ResourceUpload")) GenerateChains(chains);
                    _device.Submit(chains);
                }
                if (Demand.TransferBytes > 0 || chains is not null) _device.WaitForIdle();
            }
            finally { chains?.Dispose(); }
        }
        _submitted = true;
    }

    void GenerateChains(IGpuCommandList list)
    {
        foreach (MaterialTextureFill fill in _fills) fill.Generate(list);
    }

    /// <summary>Publish once, after readiness. Draw submissions can only resolve this accepted handle.</summary>
    public SceneResourceHandle Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published || !Ready) throw new InvalidOperationException("Resource must be ready and unpublished.");
        SceneResourceHandle handle = default;
        try
        {
            if (_publication is { } mesh)
                PublishedMesh = _scene.PublishUploadedMesh(mesh, _vertices!, _normals!, _indices!,
                    _scene.MaterialTextureOf(_albedo), _scene.MaterialTextureOf(_normal), _scene.MaterialTextureOf(_roughness),
                    _scene.MaterialTextureOf(_emissive), _materialParams, _groundAlbedo, _groundParams, _owned, _meshOrder);
            if (_clip is not null) _scene.PublishWaterDomain(PublishedMesh, _clip);
            if (PreparedFoliage is { } prepared) PublishedFoliage = _scene.PublishPreparedFoliage(prepared, _foliage, _anchor!, _owned);
            handle = _scene.PublishResource(this);
            _reservation.Published(handle);
            _published = true;
            return handle;
        }
        catch
        {
            if (handle.Value != 0) _scene.ForgetFailedPublication(handle);
            Dispose(); throw;
        }
    }
    /// <summary>Cancel before publication. A published upload is owned by ReleaseResource instead.</summary>
    public void Dispose()
    {
        if (_disposed || _published) return;
        Retire();
    }
    internal void Retire()
    {
        if (_retiring) return;
        _retiring = true; _disposed = true;
        _reservation.Retiring();
        // Mesh and foliage entries borrow this upload's buffers. Remove their visibility before retiring the
        // complete ownership group, whose final callback releases admission after every object is destroyed.
        _scene.DetachUploadedResource(PublishedMesh, PublishedFoliage);
        _publication = null;
        PublishedFoliage = null; PreparedFoliage = null;
        _scene.RetireUpload(this, new Retirement(_owned, _lease));
        // The sets naming the resident textures are queued above. A texture whose last reference this was retires
        // behind them, and one this upload created settles its lease only once destroyed.
        foreach (Scene3D.TextureHandle texture in _resident) _scene.ReleaseMaterialTexture(texture);
        _resident.Clear(); _fills.Clear();
        ReleaseInputs();
        _vertices = _normals = _indices = _clip = _foliage = _groundParams = _anchor = _materialParams = null;
        _albedo = _normal = _roughness = _emissive = default;
        _groundAlbedo = null;
        _completion = null; _barrier = null;
    }
    void ReleaseInputs() { _transfers.Clear(); _transfers.Capacity = 0; _data = null; }
    internal void RebindMaterialSets(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
    {
        for (int i = 0; i < _owned.Count; i++)
            if (_owned[i] is IGpuResourceSet set) _owned[i] = replacementFor(set);
    }
    sealed class Retirement(List<IDisposable> owned, SceneUploadLease lease) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); }
            finally { owned.Clear(); owned.Capacity = 0; lease.Settle(); }
        }
    }
}
