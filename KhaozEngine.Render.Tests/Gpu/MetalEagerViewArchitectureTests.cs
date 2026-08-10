using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STRUCTURAL ENFORCEMENT OF DECISION M-M10: no texture-view factory exists anywhere in
    /// <c>KhaozEngine.Gpu.Metal</c>, so a draw-time view is not merely a compile error but unwritable.
    ///
    /// <para><b>THE DESIGN ASKS FOR SOMETHING WEAKER AND THIS ASSERTS SOMETHING STRONGER, deliberately.</b> M-M10
    /// says no view factory is reachable from the RECORDING TYPE, asserted over the type graph, which is V-D2's
    /// and V-M11's shape. That form cannot be written yet, because the recording type is the command-list row's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573). The form below implies it: if the package declares
    /// no view factory at all, no type in the package can reach one. The day a row genuinely needs a view, THIS
    /// test is what fails, and the right response is to narrow it to the design's own wording rather than to
    /// delete it.</para>
    ///
    /// <para><b>WHY THE SET IS EMPTY IN THE FIRST PLACE, because a reader will assume it is an oversight.</b> The
    /// GPU seam has no texture-view type: <see cref="IGpuResourceFactory"/> creates none, a resource set binds an
    /// <see cref="IGpuTexture"/> whole, <c>CreateFramebuffer</c> carries no mip or layer parameter, and per-face
    /// cubemap rendering is not expressible. So nothing can NARROW a texture by mip, layer or format, which is
    /// exactly the condition under which <c>Veldrid.MTL.MTLTextureView</c> takes its <c>else</c> branch and reuses
    /// the target's own <c>DeviceTexture</c>. The incumbent still pays a MANAGED wrapper for that, allocated
    /// lazily on the draw path by <c>Util.GetTextureView</c> from <c>MTLCommandList</c>'s bind path, and all 25
    /// <c>DEVICE_REMOVED</c> stacks in https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside that
    /// lazy constructor. Here the bindable handle IS the texture and it is decided at creation.</para>
    /// </summary>
    public sealed class MetalEagerViewArchitectureTests
    {
        readonly ITestOutputHelper _output;

        public MetalEagerViewArchitectureTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE RULE. No member of the package declares a texture-view factory, on the interop layer or above it.
        /// A member whose name says "view" and whose return type is an <c>MTLTexture</c> handle is the only shape
        /// <c>-newTextureViewWithPixelFormat:textureType:levels:slices:</c> can arrive in, because every
        /// Objective-C call in this package goes through a typed handle member.
        /// </summary>
        [Fact]
        public void ThePackage_DeclaresNoTextureViewFactory()
        {
            string[] factories = PackageMethods()
                .Where(m => m.Name.Contains("View", StringComparison.Ordinal))
                .Where(m => m is MethodInfo { ReturnType: var r } && r == typeof(MTLTexture))
                .Select(m => (m.DeclaringType?.Name ?? "?") + "." + m.Name)
                .ToArray();

            Assert.True(factories.Length == 0,
                "KhaozEngine.Gpu.Metal declares a texture-view factory, which decision M-M10 says it must not: "
                + "every view a resource set can name is created at RESOURCE creation, and on this seam that "
                + "number is zero because nothing can narrow a texture by mip, layer or format. If a row now "
                + "genuinely needs one, narrow this test to the design's own wording (no view factory reachable "
                + "from the recording type) rather than deleting it, and add the creation-time call site.\n"
                + string.Join("\n", factories));
        }

        /// <summary>
        /// THE POSITIVE CONTROL, and without it the row above could pass because the reflection found nothing at
        /// all. It proves the walk sees the package's real members and that <see cref="MTLTexture"/> is a type
        /// this walk can recognise as a return type.
        /// </summary>
        [Fact]
        public void TheWalk_SeesTheInteropLayersRealMembers()
        {
            string[] members = PackageMethods()
                .Where(m => m.DeclaringType == typeof(MTLTexture))
                .Select(m => m.Name)
                .ToArray();

            _output.WriteLine(string.Join(", ", members.OrderBy(n => n, StringComparer.Ordinal)));

            Assert.Contains("Release", members, StringComparer.Ordinal);
            Assert.Contains(PackageMethods(), m => m is MethodInfo { ReturnType: var r } && r == typeof(MTLTexture));
        }

        /// <summary>
        /// And the plan really is what creation reads, rather than a value nothing consumes: every usage the seam
        /// can express answers zero views. <c>MetalResourcePolicyTests</c> enumerates the whole power set, and
        /// this row is the one that ties the number to the ARCHITECTURE claim above rather than to the policy.
        /// </summary>
        [Fact]
        public void TheCreationPlan_AnswersZeroViews()
        {
            Assert.Equal(0, MetalViewPolicy.ForTexture(
                GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.GenerateMipmaps,
                arrayLayers: 6, sampleCount: 1).ViewCount);
        }

        static IReadOnlyList<MethodBase> PackageMethods()
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var methods = new List<MethodBase>();
            foreach (Type type in typeof(MetalViewPolicy).Assembly.GetTypes())
            {
                methods.AddRange(type.GetMethods(Flags));
            }
            return methods;
        }
    }
}
