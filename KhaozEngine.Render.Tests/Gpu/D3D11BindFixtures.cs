using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE THREE SHIPPED RESOURCE SETS THE BIND FLUSH IS MEASURED AGAINST, transcribed from the renderers that
    /// declare them, plus the scaffolding a device-free flush needs.
    /// <para>
    /// TRANSCRIBED RATHER THAN INVENTED, because the numbers decision R6 is judged on are properties of these
    /// exact layouts. The model set costs four native calls and the WATER set costs six, and both are seven
    /// elements: the difference is that <c>WaterRenderer</c> declares its bathymetry texture, its ocean map and
    /// their samplers at <c>Vertex | Fragment</c>, so the vertex stage needs a shader-resource array and a sampler
    /// array of its own. A made-up seven-element set would have proved neither number. The shadow set is the
    /// offsets-only hot path, one dynamic UBO visible to the vertex stage alone, rebound thousands of times a
    /// frame.
    /// </para>
    /// <para>
    /// Shared by the schedule tests and the native-call budget, because both need the same fixtures and the budget
    /// would otherwise freeze numbers taken from a set the schedule tests never bind.
    /// </para>
    /// </summary>
    internal static class D3D11BindFixtures
    {
        /// <summary>A uniform-buffer element.</summary>
        internal static GpuResourceLayoutElement U(string name, GpuShaderStages stages, bool dynamic = false)
            => new(name, GpuResourceKind.UniformBuffer, stages, dynamic);

        /// <summary>A sampled-texture element.</summary>
        internal static GpuResourceLayoutElement T(string name, GpuShaderStages stages)
            => new(name, GpuResourceKind.TextureReadOnly, stages);

        /// <summary>A sampler element.</summary>
        internal static GpuResourceLayoutElement S(string name, GpuShaderStages stages)
            => new(name, GpuResourceKind.Sampler, stages);

        /// <summary>A read-write structured-buffer element, which is the only kind that reaches the <c>u</c> file
        /// in shipped code and does so from compute alone.</summary>
        internal static GpuResourceLayoutElement StructRW(string name)
            => new(name, GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute);

        internal static D3D11ResourceLayout Layout(params GpuResourceLayoutElement[] elements)
            => new(new GpuResourceLayoutDescription(elements));

        internal static D3D11ResourceSet Set(D3D11ResourceLayout layout, params IGpuBindableResource[] resources)
            => new(new GpuResourceSetDescription(layout, resources));

        internal static FakeTexture Texture() => new(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

        /// <summary><c>ModelRenderer._layout</c> verbatim: one UBO both stages read, four textures and two
        /// samplers the pixel stage reads. Registers <c>b0 t0 t1 t2 s0 t3 s1</c>.</summary>
        internal static D3D11ResourceLayout ModelLayout() => Layout(
            U("U", GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            T("Albedo", GpuShaderStages.Fragment),
            T("NormalMap", GpuShaderStages.Fragment),
            T("RoughnessMap", GpuShaderStages.Fragment),
            S("Sampler", GpuShaderStages.Fragment),
            T("ShadowMap", GpuShaderStages.Fragment),
            S("ShadowSamp", GpuShaderStages.Fragment));

        /// <summary>A set on <see cref="ModelLayout"/>. Distinct resource instances per call, so two sets built
        /// here are two materials rather than the same one twice.</summary>
        internal static D3D11ResourceSet ModelSet(D3D11ResourceLayout layout, IGpuBuffer? ubo = null) => Set(layout,
            ubo ?? new FakeBuffer(256), Texture(), Texture(), Texture(), new FakeSampler(), Texture(),
            new FakeSampler());

        /// <summary><c>WaterRenderer._layout</c> verbatim, and the worst case in the engine at six native calls.
        /// Registers <c>t0 s0 t1 s1 t2 s2 b0</c>, with the first two texture-and-sampler pairs and the dynamic UBO
        /// visible to BOTH stages.</summary>
        internal static D3D11ResourceLayout WaterLayout() => Layout(
            T("BathyTex", GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            S("BathySamp", GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            T("OceanMap", GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            S("OceanSamp", GpuShaderStages.Vertex | GpuShaderStages.Fragment),
            T("DepthTex", GpuShaderStages.Fragment),
            S("Samp", GpuShaderStages.Fragment),
            U("Water", GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true));

        internal static D3D11ResourceSet WaterSet(D3D11ResourceLayout layout, IGpuBuffer? ubo = null) => Set(layout,
            Texture(), new FakeSampler(), Texture(), new FakeSampler(), Texture(), new FakeSampler(),
            ubo ?? new FakeBuffer(1024));

        /// <summary><c>ShadowMapRenderer._layout</c> verbatim: one dynamic UBO, vertex stage only. The
        /// offsets-only hot path.</summary>
        internal static D3D11ResourceLayout ShadowLayout()
            => Layout(U("U", GpuShaderStages.Vertex, dynamic: true));

        /// <summary>A set on <see cref="ShadowLayout"/>, bound as a 64-byte window into a larger buffer exactly as
        /// the renderer does, so the first-constant arithmetic has a non-zero range offset to carry.</summary>
        internal static D3D11ResourceSet ShadowSet(D3D11ResourceLayout layout, IGpuBuffer? ubo = null)
            => Set(layout, new GpuBufferRange(ubo ?? new FakeBuffer(4096), 0, 64));

        /// <summary>A pipeline over <paramref name="layouts"/>, in pipeline-array order, whose seven state objects
        /// are distinct. That is what a first bind of a frame meets, and it means a pipeline switch in a test
        /// costs a known seven calls rather than an accidental number.</summary>
        internal static D3D11StateCacheTests.FakeD3D11Pipeline Pipeline(params D3D11ResourceLayout[] layouts)
            => new(new object(), new object(), new object(), new object(), new object(), new object(), 4u, layouts);

        /// <summary>
        /// A CONSTANT-BUFFER RING'S MEMORY THAT WRITES ITS TWO CONTEXT CALLS INTO THE NATIVE TRACE, which is what
        /// makes decision T2's "zero <c>Map</c> or <c>Unmap</c> during replay" an executable invariant rather than
        /// a vacuous one. Without it the budget's trace has no vocabulary for a map, so the assertion would hold
        /// by having nothing to fail on.
        /// <para>
        /// <see cref="ID3D11RingMemory"/> is exactly the seam the real path puts those two calls behind, so a ring
        /// driven through this one maps and unmaps in the same places at the same moments as one driven through
        /// <c>D3D11BufferRingMemory</c>. It wraps <see cref="FakeD3D11RingMemory"/> rather than reimplementing it,
        /// so the double-map and double-unmap refusals still apply.
        /// </para>
        /// </summary>
        internal sealed class TracedRingMemory : ID3D11RingMemory, IDisposable
        {
            readonly FakeD3D11RingMemory _inner;
            readonly D3D11NativeCallLog _log;

            internal TracedRingMemory(uint totalBytes, D3D11NativeCallLog log)
            {
                _inner = new FakeD3D11RingMemory(totalBytes);
                _log = log;
            }

            /// <summary>Maps taken, for a test that wants the count rather than the trace position.</summary>
            internal int MapCount => _inner.MapCount;

            /// <summary>Mappings released.</summary>
            internal int UnmapCount => _inner.UnmapCount;

            /// <inheritdoc/>
            public IntPtr MapWriteNoOverwrite()
            {
                _log.Record(D3D11NativeCall.Map, "NO_OVERWRITE");
                return _inner.MapWriteNoOverwrite();
            }

            /// <inheritdoc/>
            public void Unmap()
            {
                _log.Record(D3D11NativeCall.Unmap);
                _inner.Unmap();
            }

            public void Dispose() => _inner.Dispose();
        }

        /// <summary>Everything a device-free flush needs, wired the way a device wires it: one state object
        /// carrying one bind flush, and one emitter over both.</summary>
        internal sealed class Harness
        {
            internal Harness(bool unsetConstantBuffersBeforeSet = false, D3D11RingAllocator? rings = null)
            {
                Log = new D3D11NativeCallLog();
                State = new D3D11DeviceState(new D3D11BindFlush(unsetConstantBuffersBeforeSet, rings));
                Emitter = new D3D11NativeTraceEmitter(State, Log);
                Emitter.Begin();
                Log.Reset();
            }

            /// <summary>The native calls, in order.</summary>
            internal D3D11NativeCallLog Log { get; }

            /// <summary>What is bound on the context, and the bind flush inside it.</summary>
            internal D3D11DeviceState State { get; }

            /// <summary>The emitter under test. Already opened, with the opening <c>ClearState</c> cleared out of
            /// the log so a test's expected trace is about the test.</summary>
            internal D3D11NativeTraceEmitter Emitter { get; }

            /// <summary>The bind flush, for the assertions about the record itself.</summary>
            internal D3D11BindFlush Binds => State.Binds;

            /// <summary>The trace lines that are BINDS, with the draws, dispatches and pending markers removed.
            /// That is the shape the fan-out assertions are about, and it is what makes an instance-count
            /// comparison meaningful: the instance count is a draw ARGUMENT and legitimately differs.</summary>
            internal string[] BindTrace()
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (string line in Log.Trace)
                {
                    if (line.StartsWith("DrawInstanced(", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("DrawIndexedInstanced(", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("Dispatch(", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("ResourceSetPending(", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("VertexBufferPending(", StringComparison.Ordinal)) continue;
                    lines.Add(line);
                }

                return lines.ToArray();
            }
        }
    }
}
