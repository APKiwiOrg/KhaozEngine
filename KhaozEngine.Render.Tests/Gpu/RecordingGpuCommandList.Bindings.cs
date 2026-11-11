using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BIND HALF: what each indexed draw had bound at every graphics set slot and every vertex buffer slot, so a
    /// test can pin which sets and streams a draw path binds and which slots it leaves alone. Opt-in through
    /// <see cref="CaptureBindings"/>, because a whole frame's draws would otherwise each copy two small maps.
    /// A bind stays in force until its slot is bound again, as on a real command list.
    /// </summary>
    internal sealed partial class RecordingGpuCommandList
    {
        /// <summary>One indexed draw's bindings: its pipeline, each bound graphics set with its dynamic offset by
        /// slot, and each bound vertex buffer by slot.</summary>
        internal sealed record DrawBindings(IGpuPipeline? Pipeline,
            IReadOnlyDictionary<uint, (IGpuResourceSet Set, uint DynamicOffset)> Sets,
            IReadOnlyDictionary<uint, IGpuBuffer> VertexBuffers)
        {
            internal int DrawOrdinal { get; init; }
            internal uint ElementCount { get; init; }
        }

        internal sealed record ComputeDispatchBindings(IGpuComputePipeline Pipeline,
            IReadOnlyDictionary<uint, (IGpuResourceSet Set, uint DynamicOffset)> Sets, int DrawOrdinal);
        internal readonly List<ComputeDispatchBindings> ComputeBindings = new();
        internal bool CaptureComputeBindings { get; set; }
        IGpuComputePipeline? _currentComputePipeline;

        readonly List<DrawBindings> _drawBindings = new();
        readonly Dictionary<uint, IGpuBuffer> _vertexBuffers = new();

        /// <summary>Record <see cref="Bindings"/> for every indexed draw. Off by default.</summary>
        public bool CaptureBindings { get; set; }

        /// <summary>With <see cref="CaptureBindings"/>, also record instanced draws without an index buffer, such as the
        /// caustic sheets. Off by default, so tests that index the indexed draws' bindings keep their positions.</summary>
        public bool CaptureInstancedBindings { get; set; }

        /// <summary>The bindings of every indexed draw since the last <see cref="Clear"/>, in record order. Empty
        /// unless <see cref="CaptureBindings"/> was on.</summary>
        internal IReadOnlyList<DrawBindings> Bindings => _drawBindings;

        void NoteVertexSlot(uint slot, IGpuBuffer buffer) => _vertexBuffers[slot] = buffer;

        void NoteDrawBindings(uint elementCount = 0)
        {
            if (!CaptureBindings) return;
            var sets = new Dictionary<uint, (IGpuResourceSet Set, uint DynamicOffset)>();
            foreach (KeyValuePair<uint, Binding> bound in _graphicsSets)
                sets[bound.Key] = (bound.Value.Set, bound.Value.DynamicOffset);
            _drawBindings.Add(new DrawBindings(_currentPipeline, sets, new Dictionary<uint, IGpuBuffer>(_vertexBuffers))
            { DrawOrdinal = _draws, ElementCount = elementCount });
        }

        void NoteComputeDispatch()
        {
            if (!CaptureComputeBindings || _currentComputePipeline is null) return;
            var sets = new Dictionary<uint, (IGpuResourceSet Set, uint DynamicOffset)>();
            foreach (var bound in _computeSets) sets[bound.Key] = (bound.Value.Set, bound.Value.DynamicOffset);
            ComputeBindings.Add(new(_currentComputePipeline, sets, _draws));
        }

        void ClearBindings()
        {
            _drawBindings.Clear();
            ComputeBindings.Clear(); _currentComputePipeline = null;
            _vertexBuffers.Clear();   // ClearReads forgets the bound sets, so the vertex streams go with them
        }
    }
}
