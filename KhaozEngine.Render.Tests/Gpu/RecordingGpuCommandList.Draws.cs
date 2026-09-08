using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    internal sealed partial class RecordingGpuCommandList
    {
        internal readonly record struct IndexedDraw(
            uint IndexCount, IGpuPipeline? Pipeline, IGpuBuffer? VertexBuffer, IGpuBuffer? IndexBuffer);

        readonly List<IndexedDraw> _indexedDraws = new();
        IGpuPipeline? _currentPipeline;
        IGpuBuffer? _currentVertexBuffer;
        IGpuBuffer? _currentIndexBuffer;

        internal IReadOnlyList<IndexedDraw> IndexedDraws => _indexedDraws;

        void NotePipeline(IGpuPipeline pipeline) => _currentPipeline = pipeline;
        void NoteVertexBuffer(uint slot, IGpuBuffer buffer)
        {
            if (slot == 0) _currentVertexBuffer = buffer;
        }
        void NoteIndexBuffer(IGpuBuffer buffer) => _currentIndexBuffer = buffer;
        void NoteIndexedDraw(uint indexCount)
            => _indexedDraws.Add(new IndexedDraw(indexCount, _currentPipeline, _currentVertexBuffer,
                _currentIndexBuffer));
        void ClearDraws() => _indexedDraws.Clear();
    }
}
