using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REAL EMITTER'S BIND ARITHMETIC WITH NO DEVICE UNDER IT: which view object a bound resource offers a
    /// register file, which framebuffer object carries the render targets, and how a span of engine binds is
    /// transposed into the parallel arrays <c>*SetConstantBuffers1</c> takes. Everything the emitter does between
    /// receiving a bind and making the call, minus the call.
    /// <para>
    /// WHY IT IS A SEPARATE TYPE AND NOT THE EMITTER'S PRIVATE METHODS. The emitter's own bodies are Windows-only
    /// and cannot run in the headless suite, so anything left inside them is verified by a WARP leg and by nothing
    /// else. What lives here names no Direct3D type (every view is an <c>object</c>, see
    /// <see cref="ID3D11BindableViews"/>), so the decisions that are actually easy to get wrong (a
    /// <see cref="GpuBufferRange"/> arriving where a buffer was expected, a texture bound at a sampler register, a
    /// resource whose declared usage never earned it the view its layout asks for, a first-constant that does not
    /// fit an <c>int</c>) are plain <c>[Fact]</c>s on macOS. The emitter is left with a cast and a call.
    /// </para>
    /// <para>
    /// A NULL IS A HOLE AND IS PASSED THROUGH; A RESOURCE WITHOUT THE VIEW IS A REFUSAL. Those are different
    /// things and conflating them is the bug this type exists to make impossible. An array bind covers a
    /// contiguous register span that may contain a register the set does not fill, and Direct3D 11 wants a null
    /// there. A resource that IS bound and has no view for the file it landed in is a layout or usage mismatch
    /// that would bind nothing the shader reads, so it throws and names both.
    /// </para>
    /// </summary>
    internal static class D3D11BindResolve
    {
        /// <summary>
        /// The view object <paramref name="resource"/> offers <paramref name="file"/>, or null when
        /// <paramref name="resource"/> is null (a hole in the span).
        /// <para>
        /// A <see cref="GpuBufferRange"/> resolves to its BUFFER's view, which is the one unwrapping this does. A
        /// set stores the resource exactly as the caller bound it, so a structured buffer bound as a range arrives
        /// here as the range rather than as the buffer, and the window it carries was already resolved into the
        /// binding at set creation.
        /// </para>
        /// </summary>
        internal static object? ViewOf(IGpuBindableResource? resource, D3D11RegisterFile file)
        {
            switch (resource)
            {
                case null:
                    return null;

                case GpuBufferRange range when range.Buffer is not null:
                    return ViewOf(range.Buffer, file);

                case ID3D11BindableViews views:
                {
                    object? view = file switch
                    {
                        D3D11RegisterFile.ConstantBuffer => views.BufferObject,
                        D3D11RegisterFile.ShaderResource => views.ShaderResourceViewObject,
                        D3D11RegisterFile.Sampler => views.SamplerStateObject,
                        D3D11RegisterFile.UnorderedAccess => views.UnorderedAccessViewObject,
                        _ => throw new ArgumentOutOfRangeException(nameof(file), file,
                            "Unmapped Direct3D 11 register file in a bind."),
                    };

                    return view ?? throw new ArgumentException(
                        $"A {resource.GetType().Name} was bound at a '{Letter(file)}' register of the native "
                        + "Direct3D 11 backend and has no view for it. Views follow from a resource's DECLARED "
                        + "usage at creation (decision X1 creates them all eagerly and the emitter creates none), "
                        + "so this is either a layout element of the wrong kind or a resource created without the "
                        + "usage bit its layout asks for.", nameof(resource));
                }

                default:
                    throw new ArgumentException(
                        $"A {resource.GetType().Name} was bound into the native Direct3D 11 backend. A resource "
                        + "this backend created answers ID3D11BindableViews, which is where the view a register "
                        + "file wants comes from, and a resource from another backend carries another backend's "
                        + "handles.", nameof(resource));
            }
        }

        /// <summary>
        /// The framebuffer's output-merger surface. BOTH framebuffer types this backend has answer it, and the
        /// refusal is by name rather than a cast that throws <see cref="InvalidCastException"/>: casting to one
        /// concrete type would work for every offscreen pass and fail on the first frame that renders to the
        /// swapchain, which is the review finding this seam exists for.
        /// </summary>
        internal static ID3D11RenderTargetSurface RenderTargets(IGpuFramebuffer framebuffer)
        {
            if (framebuffer is null) throw new ArgumentNullException(nameof(framebuffer));

            return framebuffer as ID3D11RenderTargetSurface ?? throw new ArgumentException(
                $"A {framebuffer.GetType().Name} reached the native Direct3D 11 emitter as a framebuffer. Both of "
                + "this backend's framebuffer types answer ID3D11RenderTargetSurface, which is what carries the "
                + "render target and depth-stencil views, and a framebuffer from another backend carries another "
                + "backend's views.", nameof(framebuffer));
        }

        /// <summary>
        /// TRANSPOSE a span of constant-buffer binds into the two parallel <c>int</c> arrays
        /// <c>*SetConstantBuffers1</c> takes. The buffers themselves go into a typed array the caller owns, one
        /// <see cref="ViewOf"/> per entry, because that array names a Direct3D type and this method does not.
        /// <para>
        /// A HOLE MUST CARRY ZERO AND ZERO. Direct3D 11 requires a null buffer's first-constant and
        /// constant-count to be zero, and rejects the whole call otherwise, which loses every OTHER register in
        /// the span with it. <see cref="D3D11ConstantBufferBind"/>'s default value is exactly that pair, so this
        /// holds by construction and is asserted rather than fixed up: a non-zero window against no buffer means
        /// something upstream built a bind wrongly, and silently zeroing it would hide that.
        /// </para>
        /// </summary>
        internal static void Constants(ReadOnlySpan<D3D11ConstantBufferBind> binds, int[] firstConstants,
            int[] constantCounts)
        {
            ArgumentNullException.ThrowIfNull(firstConstants);
            ArgumentNullException.ThrowIfNull(constantCounts);
            if (firstConstants.Length < binds.Length || constantCounts.Length < binds.Length)
            {
                throw new ArgumentException(
                    $"The constant-buffer scratch holds {Math.Min(firstConstants.Length, constantCounts.Length)} "
                    + $"entries and this bind covers {binds.Length} registers. The scratch is grown before the "
                    + "transposition, so a short one here is a defect in the emitter rather than an oversized "
                    + "bind.", nameof(firstConstants));
            }

            for (int i = 0; i < binds.Length; i++)
            {
                // A constants window is a 16-byte count, so an int holds every window a Direct3D 11 buffer can
                // have several times over. Checked anyway, because an unchecked cast of a wrapped uint lands as a
                // NEGATIVE first constant, which the runtime reads as an enormous one.
                firstConstants[i] = checked((int)binds[i].FirstConstant);
                constantCounts[i] = checked((int)binds[i].ConstantCount);

                if (binds[i].Buffer is null && (firstConstants[i] != 0 || constantCounts[i] != 0))
                {
                    throw new ArgumentException(
                        $"Register {i} of a Direct3D 11 constant-buffer bind has no buffer but carries the window "
                        + $"[{firstConstants[i]}, +{constantCounts[i]}). A null entry must carry a first constant "
                        + "and a constant count of zero, and the runtime rejects the whole call otherwise, which "
                        + "loses every other register in the same span.", nameof(binds));
                }
            }
        }

        /// <summary>
        /// The capacity a scratch array grows to for <paramref name="count"/> entries: the next power of two at or
        /// above it, never below eight. Geometric, so a scratch is reallocated a handful of times over a process
        /// rather than once per widening bind, which is what "zero per-call allocation" on the hot path means in
        /// practice. Shared with <see cref="D3D11SetActivation"/> so the two scratches grow on one curve.
        /// </summary>
        internal static int RoundedCapacity(int count)
        {
            int capacity = 8;
            while (capacity < count) capacity <<= 1;
            return capacity;
        }

        // The HLSL register letter for a file, for a message a reader can match against a shader.
        static string Letter(D3D11RegisterFile file) => file switch
        {
            D3D11RegisterFile.ConstantBuffer => "b",
            D3D11RegisterFile.ShaderResource => "t",
            D3D11RegisterFile.Sampler => "s",
            _ => "u",
        };
    }
}
