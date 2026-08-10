using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT ROW 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) READS OFF A RESOLVED BINDING, AND WHEN IT
    /// READS IT. A set resolves everything at creation, and exactly two of the values a bind uses are read at the
    /// bind instead: the Objective-C handle and the uniform ring, both through the wrapper's own disposal guard.
    ///
    /// <para><b>THIS IS THE ROW THAT WOULD PASS OVER A SNAPSHOT AND MUST NOT.</b> Disposing a ring-backed uniform
    /// buffer releases the <c>MTLBuffer</c> and takes the ring out of the allocator, and
    /// <see cref="MetalBuffer.Ring"/> answers null from that moment precisely so both write paths branch off it
    /// before anything else. A binding that had COPIED the ring and the handle at creation would compose a base
    /// off a forgotten ring and put a released pointer in the argument table, with the guard one field read away
    /// and unreachable. So the assertion below is not about disposal hygiene: it is that the guard is still the
    /// bind's predicate.</para>
    ///
    /// <para><b>NO DEVICE, WHICH IS WHY IT IS A PLAIN <c>[Fact]</c>.</b> <see cref="MetalRingHarness"/> builds a
    /// real buffer over a pinned array with a fabricated handle, so the whole resolution runs on every leg. The
    /// device-side half, where the handles are real Objective-C objects, is
    /// <c>MetalResourceSetGpuTests</c>.</para>
    /// </summary>
    public sealed class MetalBoundResourceTests
    {
        [Fact]
        public void DisposingABoundBuffer_TakesItsRingAndItsHandleWithIt()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer buffer = harness.NewBuffer(256, GpuBufferUsage.UniformBuffer);

            using var layout = new MetalResourceLayout(
                harness.Liveness, new GpuResourceLayoutDescription(Element("Frame", dynamic: true)));
            using var set = new MetalResourceSet(
                harness.Liveness, new GpuResourceSetDescription(layout, buffer));

            // A COPY OF THE RECORD, taken the way row 13 takes one out of the span, so what follows is asserted
            // about the value a binder holds rather than about the array element.
            MetalBoundResource bound = set.Bindings[0];

            Assert.NotNull(bound.Ring);
            Assert.NotEqual(IntPtr.Zero, bound.Handle);

            harness.DisposeWithoutRelease(buffer);

            // EXACTLY WHAT A BIND READS, in the order it reads it: the ring first, because the ringed arm is the
            // one that would compose a base, then the handle the array setter would write.
            Assert.Null(bound.Ring);
            Assert.Equal(IntPtr.Zero, bound.Handle);

            // And everything resolved ONCE is untouched, so this is a live read of two values rather than a set
            // that re-resolves itself.
            Assert.Equal(MetalIndexSpace.Buffer, bound.Space);
            Assert.Equal(0u, bound.RangeOffset);
            Assert.Equal(256u, bound.Range);
            Assert.True(bound.AppliesCallerOffset);
        }

        /// <summary>A resource that is disposed BEFORE the set is built is refused instead, because a set that
        /// starts out nil is a caller error with no later point at which it could come right.</summary>
        [Fact]
        public void ABufferDisposedBeforeTheSet_IsRefusedAtCreation()
        {
            using var harness = new MetalRingHarness();
            MetalBuffer buffer = harness.NewBuffer(256, GpuBufferUsage.UniformBuffer);
            harness.DisposeWithoutRelease(buffer);

            using var layout = new MetalResourceLayout(
                harness.Liveness, new GpuResourceLayoutDescription(Element("Frame")));

            ArgumentException failed = Assert.Throws<ArgumentException>(() => new MetalResourceSet(
                harness.Liveness, new GpuResourceSetDescription(layout, buffer)));

            Assert.Contains("'Frame' at binding 0", failed.Message, StringComparison.Ordinal);
            Assert.Contains("already been disposed", failed.Message, StringComparison.Ordinal);
        }

        static GpuResourceLayoutElement Element(string name, bool dynamic = false)
            => new(name, GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment,
                dynamic);
    }
}
