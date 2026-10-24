using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Account at the existing allocation/release owners, never at staging subleases or frame rotation.</summary>
internal sealed class MetalMemoryStagingSource(IMetalStagingSource native, MetalMemoryDiagnostics memory) : IMetalStagingSource
{
    public MetalStagingBlock Create(ulong sizeBytes)
    {
        var block = native.Create(sizeBytes);
        if (!block.IsValid) return block;
        try { memory.OwnStaging(block.Buffer, block.SizeBytes); return block; }
        catch { native.Destroy(block); throw; }
    }

    public void Destroy(in MetalStagingBlock block)
    {
        memory.ReleaseStaging(block.Buffer);
        native.Destroy(block);
    }
}

internal interface IMetalStagingAbandon
{
    void AbandonStaging(MTLBuffer staged);
}

internal sealed class MetalMemorySetupNative(IMetalSetupNative native, MetalMemoryDiagnostics memory) : IMetalSetupNative, IMetalStagingAbandon
{
    public MTLCommandBuffer BeginBatch() => native.BeginBatch();
    public MTLBuffer Stage(System.ReadOnlySpan<byte> data)
    {
        var buffer = native.Stage(data);
        if (buffer.IsNull) return buffer;
        try { memory.OwnStaging(buffer.Handle, (ulong)data.Length); return buffer; }
        catch { native.ReleaseStaging(buffer); throw; }
    }
    public void Encode(MTLCommandBuffer batch, MTLBuffer staged, MTLTexture destination, ulong sourceRowPitch,
        in MetalTextureUpload upload) => native.Encode(batch, staged, destination, sourceRowPitch, upload);
    public void Commit(MTLCommandBuffer batch) => native.Commit(batch);
    public void ReleaseBatch(MTLCommandBuffer batch) => native.ReleaseBatch(batch);
    public void ReleaseStaging(MTLBuffer staged)
    {
        memory.ReleaseStaging(staged.Handle);
        native.ReleaseStaging(staged);
    }
    public MetalCommandBufferFault ReadFault(MTLCommandBuffer batch) => native.ReadFault(batch);
    public void AbandonStaging(MTLBuffer staged) => memory.ReleaseStaging(staged.Handle);
}
