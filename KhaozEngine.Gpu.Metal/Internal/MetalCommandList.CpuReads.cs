namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalCommandList
{
    readonly MetalVertexReadRecords _readStreams = new();
    MetalBuffer? _readIndices;
    MetalCpuRead _lastIndexRead;
    bool _hasIndexRead;

    // Deterministic work diagnostic for the real draw path, without driver timing.
    internal long ReadProbeStepsForTests => _recordedReads.ProbeStepsForTests;
    internal long ReadNodeWritesForTests => _recordedReads.NodeWritesForTests;
    internal long ReadRotationsForTests => _recordedReads.RotationsForTests;
    internal long ReadHashLookupsForTests => _recordedReads.HashLookupsForTests;
    internal long ReadShaderLookupsForTests => _graphicsBinds.ReadShaderLookupsForTests;
    internal bool ReadOverlapsForTests(in MetalCpuRead access) => _recordedReads.Overlaps(access);

    void RecordGraphicsReads(MetalGraphicsPipeline pipeline, uint vertexStart, uint vertexCount,
        uint instanceStart, uint instanceCount, bool indexed)
    {
        if (_prefixFrame != 0 || vertexCount == 0 || instanceCount == 0) return;
        _graphicsBinds.RecordReads(_recordedReads, _segment);
        _readStreams.RecordReads(_recordedReads, pipeline.Plan.ReadLayout,
            vertexStart, vertexCount, instanceStart, instanceCount, indexed);
    }

    void RecordIndexRead(MetalBuffer buffer, ulong offset, ulong length)
    {
        if (_prefixFrame != 0 || buffer.Handle.IsNull) return;
        var read = new MetalCpuRead(buffer.CpuResource, offset, length);
        if (_hasIndexRead && _lastIndexRead == read) return;
        _recordedReads.Add(buffer.CpuResource, offset, length);
        _lastIndexRead = read;
        _hasIndexRead = true;
    }

    void RecordStagingAccess(MetalTexture source, uint level, uint layer, uint width, uint height)
    {
        if (_prefixFrame != 0 || !source.IsStaging) return;
        var layout = source.SubresourceLayout(level, layer);
        _recordedReads.AddStrided(source.CpuResource, layout.Offset,
            MetalStagingLayout.RowPitch(width, source.Format), layout.RowPitch, height);
    }
}
