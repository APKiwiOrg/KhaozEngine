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
            IReadOnlyDictionary<uint, IGpuBuffer> VertexBuffers);

        readonly List<DrawBindings> _drawBindings = new();
        readonly Dictionary<uint, IGpuBuffer> _vertexBuffers = new();

        /// <summary>Record <see cref="Bindings"/> for every indexed draw. Off by default.</summary>
        public bool CaptureBindings { get; set; }

        /// <summary>The bindings of every indexed draw since the last <see cref="Clear"/>, in record order. Empty
        /// unless <see cref="CaptureBindings"/> was on.</summary>
        internal IReadOnlyList<DrawBindings> Bindings => _drawBindings;

        void NoteVertexSlot(uint slot, IGpuBuffer buffer) => _vertexBuffers[slot] = buffer;

        void NoteDrawBindings()
        {
            if (!CaptureBindings) return;
            var sets = new Dictionary<uint, (IGpuResourceSet Set, uint DynamicOffset)>();
            foreach (KeyValuePair<uint, Binding> bound in _graphicsSets)
                sets[bound.Key] = (bound.Value.Set, bound.Value.DynamicOffset);
            _drawBindings.Add(new DrawBindings(_currentPipeline, sets, new Dictionary<uint, IGpuBuffer>(_vertexBuffers)));
        }

        void ClearBindings() => _drawBindings.Clear();
    }
}
