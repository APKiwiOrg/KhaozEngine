using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE HOT SELECTORS ARE RESOLVED ONCE PER PROCESS (#1114). <c>ObjCRuntime.Sel</c> is a string hash and a
    /// dictionary probe per call, and <c>ObjCRuntime.ClassNamed</c> encodes its name into a fresh array per call,
    /// so on the per-draw, per-bind and per-pass members both are replaced by fields in a nested
    /// <c>Selectors</c> type, <c>MetalCompletionHandler</c>'s shape. The IL rows run everywhere. The value row
    /// needs libobjc and goes dormant off macOS.
    /// </summary>
    public sealed class MetalHotSelectorTests
    {
        static readonly Type[] SelectorOwners =
        {
            typeof(MTLRenderCommandEncoder), typeof(MTLComputeCommandEncoder), typeof(MTLCommandEncoder),
            typeof(MTLCommandBuffer), typeof(MTLBlitCommandEncoder), typeof(MTLRenderPassDescriptor),
        };

        readonly ITestOutputHelper _output;

        public MetalHotSelectorTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void NoHotMemberLooksASelectorOrAClassUpPerCall()
        {
            string[] lookups = HotMembers()
                .Where(m => IlCallGraph.Callees(m).Any(IsPerCallLookup))
                .Select(IlCallGraph.Describe)
                .ToArray();

            Assert.True(lookups.Length == 0,
                "These per-draw, per-bind or per-pass members still resolve a selector or a class on every call. "
                + "Read it from the type's nested Selectors class instead, which resolves once per process.\n"
                + string.Join("\n", lookups));
        }

        /// <summary>The positive control: the reader does see a per-call lookup where one is deliberately left, so
        /// the row above is green because the hot members are clean rather than because the IL read came back
        /// empty.</summary>
        [Fact]
        public void TheWalk_SeesAPerCallLookupWhereOneIsLeft()
            => Assert.Contains(IlCallGraph.Callees(Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.Commit))),
                IsPerCallLookup);

        [Fact]
        public void EveryCachedSelectorResolvesToADistinctSelector()
        {
            if (!OperatingSystem.IsMacOS())
            {
                _output.WriteLine("dormant: not macOS, so there is no libobjc to register selectors with.");
                return;
            }

            var seen = new HashSet<IntPtr>();
            foreach (Type owner in SelectorOwners)
            {
                Type selectors = owner.GetNestedType("Selectors", BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(owner.Name + " has no nested Selectors type.");

                foreach (FieldInfo field in selectors.GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var value = (IntPtr)field.GetValue(null)!;
                    Assert.NotEqual(IntPtr.Zero, value);
                    Assert.True(seen.Add(value),
                        owner.Name + "." + field.Name + " resolved to a selector another cached field already holds");
                }
            }

            _output.WriteLine($"{seen.Count} cached selectors resolved, all distinct");
        }

        static IEnumerable<MethodBase> HotMembers()
        {
            foreach (Type type in new[] { typeof(MTLRenderCommandEncoder), typeof(MTLComputeCommandEncoder),
                typeof(MTLCommandEncoder) })
            {
                foreach (MethodBase member in IlCallGraph.DeclaredMethods(type)) yield return member;
            }

            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.RenderCommandEncoder));
            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.BlitCommandEncoder));
            yield return Member(typeof(MTLCommandBuffer), nameof(MTLCommandBuffer.ComputeCommandEncoder));
            yield return Member(typeof(MTLBlitCommandEncoder), nameof(MTLBlitCommandEncoder.CopyFromBufferToBuffer));
            yield return Member(typeof(MTLRenderPassDescriptor), nameof(MTLRenderPassDescriptor.Create));
        }

        static MethodBase Member(Type type, string name)
            => IlCallGraph.DeclaredMethods(type).Single(m => m.Name == name);

        static bool IsPerCallLookup(MethodBase callee)
            => callee.DeclaringType == typeof(ObjCRuntime)
                && callee.Name is nameof(ObjCRuntime.Sel) or nameof(ObjCRuntime.ClassNamed);
    }
}
