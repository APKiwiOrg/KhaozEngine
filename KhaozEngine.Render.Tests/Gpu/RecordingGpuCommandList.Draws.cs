using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    internal sealed partial class RecordingGpuCommandList
    {
        internal readonly record struct IndexedDraw(
            uint IndexCount, IGpuPipeline? Pipeline, IGpuBuffer? VertexBuffer, IGpuBuffer? IndexBuffer,
            uint IndexStart = 0, int VertexOffset = 0);

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
        void NoteIndexedDraw(uint indexCount, uint indexStart, int vertexOffset)
            => _indexedDraws.Add(new IndexedDraw(indexCount, _currentPipeline, _currentVertexBuffer,
                _currentIndexBuffer, indexStart, vertexOffset));
        void ClearDraws() => _indexedDraws.Clear();
    }
}
