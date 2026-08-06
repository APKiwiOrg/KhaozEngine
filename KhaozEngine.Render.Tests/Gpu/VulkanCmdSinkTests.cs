using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-T2's SEAM, and the counting sink the budget test will be taken over. Row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) owns the budget itself, because the numbers it
    /// freezes are produced by the bind flush that row builds. What this file pins is the SEAM's own properties,
    /// which have to hold before any number taken over it means anything.
    /// </summary>
    public sealed class VulkanCmdSinkTests
    {
        /// <summary>
        /// THE SEAM COVERS EXACTLY THE THREE CALL CLASSES THAT SCALE WITH DRAW COUNT AND NOTHING ELSE. A member
        /// added here is a member the budget starts gating on, and clears, copies, mip generation, resolves and
        /// the rendering begin and end pair are deliberately outside it: none of them scales per draw, and
        /// freezing numbers over them would gate on figures nobody should gate on.
        /// <para>
        /// Above all, <c>vkAllocateDescriptorSets</c> and <c>vkUpdateDescriptorSets</c> must never appear here.
        /// "Zero of both between Begin and End" is the Vulkan #418 protection, and it is enforced STRUCTURALLY by
        /// the descriptor pool being unreachable from the recording type (V-D2, rows 10 and 11). A call that
        /// cannot be made is a stronger guarantee than a call that is counted and found to be zero, and adding
        /// either member here would quietly trade the first for the second.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSeamNamesExactlyTheThreeCallClasses()
        {
            string[] members = typeof(IVkCmdSink).GetMethods()
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[] { "BindDescriptorSets", "Dispatch", "Draw", "DrawIndexed", "PipelineBarrier" },
                members);
        }

        /// <summary>
        /// EVERY SINK IS A READONLY STRUCT, which is what makes the generic constraint monomorphize and what stops
        /// a copy of one carrying its own state. The other backend enforces the same rule on its emitters with the
        /// same kind of reflection check, and for the same reason: C# has no <c>where T : readonly struct</c>, so
        /// the constraint is invisible at the call site and the compiler cannot express it.
        /// </summary>
        [Fact]
        public void EverySinkInTheBackend_IsAReadonlyStruct()
        {
            Type[] sinks = typeof(IVkCmdSink).Assembly.GetTypes()
                .Where(t => typeof(IVkCmdSink).IsAssignableFrom(t) && t != typeof(IVkCmdSink))
                .ToArray();

            // A scan that finds nothing passes without checking anything, which is how this test would rot the day
            // the sinks move or are renamed.
            Assert.Contains(typeof(VulkanCmdSink), sinks);
            Assert.Contains(typeof(VulkanCountingCmdSink), sinks);

            foreach (Type sink in sinks)
            {
                Assert.True(sink.IsValueType, sink.Name + " implements IVkCmdSink but is not a struct, so it "
                    + "cannot satisfy the seam's struct constraint and every call through it would be an "
                    + "interface dispatch on the per-draw path.");

                bool isReadOnly = sink.GetCustomAttributesData().Any(
                    a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");

                Assert.True(isReadOnly, sink.Name + " is a MUTABLE struct. A sink is copied into whatever recorder "
                    + "drives it, so inline state would be per-copy: two copies would tally two different totals "
                    + "and a budget taken over one of them would be measuring half the frame. Make it a readonly "
                    + "struct and put any state in a class the struct points at.");
            }
        }

        /// <summary>The counting sink tallies each class separately, so a budget can say "one bind carrying four
        /// sets" rather than only "one call".</summary>
        [Fact]
        public void TheCountingSink_TalliesCallsSetsAndOffsetsSeparately()
        {
            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);

            Span<DescriptorSet> sets = stackalloc DescriptorSet[4];
            Span<uint> offsets = stackalloc uint[6];

            sink.BindDescriptorSets(PipelineBindPoint.Graphics, default, 0, sets, offsets);
            sink.Draw(3, 1, 0, 0);
            sink.DrawIndexed(6, 2, 0, 0, 0);
            sink.Dispatch(8, 1, 1);

            Assert.Equal(1, counts.BindDescriptorSetCalls);
            Assert.Equal(4, counts.DescriptorSetsBound);
            Assert.Equal(6, counts.DynamicOffsetsPassed);
            Assert.Equal(2, counts.DrawCalls);
            Assert.Equal(1, counts.DispatchCalls);
        }

        /// <summary>
        /// BOTH BARRIER NUMBERS ARE KEPT, and this is why. A budget that froze only the CALL count would pass a
        /// recorder emitting one barrier per draw, and one that froze only the barrier count would pass one that
        /// batched a thousand into a single call at the wrong point in the frame. The gated invariant is "no
        /// pipeline barriers on the per-draw path", which is a statement about both.
        /// </summary>
        [Fact]
        public void TheCountingSink_CountsBarrierCallsAndBarriersSeparately()
        {
            var counts = new VulkanCmdCallCounts();
            var sink = new VulkanCountingCmdSink(counts);

            // Field by field off a default rather than through the constructor, which takes the barrier ARRAYS as
            // pointers and would need an unsafe context in a test that has no barriers to point at.
            DependencyInfo dependency = default;
            dependency.SType = StructureType.DependencyInfo;
            dependency.MemoryBarrierCount = 1;
            dependency.BufferMemoryBarrierCount = 2;
            dependency.ImageMemoryBarrierCount = 3;

            sink.PipelineBarrier(in dependency);

            Assert.Equal(1, counts.BarrierCalls);
            Assert.Equal(6, counts.BarriersEmitted);
        }

        /// <summary>A copy of the sink writes to the same tallies, which is the property the readonly-struct rule
        /// buys and the one a per-copy cache would break.</summary>
        [Fact]
        public void ACopyOfTheCountingSink_WritesToTheSameTallies()
        {
            var counts = new VulkanCmdCallCounts();
            var first = new VulkanCountingCmdSink(counts);
            VulkanCountingCmdSink second = first;

            first.Draw(1, 1, 0, 0);
            second.Draw(2, 1, 0, 0);

            Assert.Equal(2, counts.DrawCalls);
            Assert.Equal(new[] { "Draw(1,1)", "Draw(2,1)" }, counts.Trace);
        }

        /// <summary>The generic constraint is what monomorphizes the seam, so a caller written against it must be
        /// able to name <c>where TSink : struct, IVkCmdSink</c>. Asserted through a generic method here, because
        /// the first real caller is row 11 and this is what stops the constraint being unusable before then.
        /// </summary>
        [Fact]
        public void TheSeamIsUsableThroughAStructConstraint()
        {
            var counts = new VulkanCmdCallCounts();

            DriveOne(new VulkanCountingCmdSink(counts));

            Assert.Equal(1, counts.DrawCalls);
        }

        static void DriveOne<TSink>(TSink sink) where TSink : struct, IVkCmdSink
            => sink.Draw(3, 1, 0, 0);

        /// <summary>The real sink refuses a null API rather than making five calls through one, which is the one
        /// thing it can check without a device.</summary>
        [Fact]
        public void TheRealSink_RefusesANullApi()
            => Assert.Throws<ArgumentNullException>(() => new VulkanCmdSink(null!, default));

        /// <summary>And it carries no mutable field at all, which is the strongest form of the rule above: there
        /// is no state for a copy to disagree about.</summary>
        [Fact]
        public void TheRealSink_CarriesNoMutableState()
        {
            FieldInfo[] fields = typeof(VulkanCmdSink)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.All(fields, f => Assert.True(f.IsInitOnly, f.Name + " is not readonly."));
        }
    }
}
