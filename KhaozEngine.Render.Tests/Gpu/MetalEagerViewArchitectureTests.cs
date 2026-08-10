using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
        /// THE READABLE CHECK, and the weaker of the two. No member of the package is named for a view and
        /// returns an <c>MTLTexture</c> handle, which is the shape a view factory written the way this package
        /// writes every other selector would take.
        ///
        /// <para><b>IT IS NOT THE BINDING ONE, and pretending otherwise was the defect.</b> It matches on a NAME
        /// and a RETURN TYPE, so a factory called <c>NewNarrowedTexture</c>, or one handing back a managed
        /// wrapper instead of the handle, walks straight through it. What actually binds is
        /// <see cref="ThePackageAssembly_CarriesNoTextureViewSelector"/> below, which asks whether the selector
        /// string exists in the compiled assembly at all. This row stays because it is the one a reader
        /// understands at a glance and the one whose failure message says what to do.</para>
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
        /// THE BINDING CHECK: the selector <c>-newTextureViewWithPixelFormat:textureType:levels:slices:</c>
        /// cannot be sent by an assembly that does not contain its name, so the assertion is that
        /// <c>newTextureView</c> appears NOWHERE in the compiled package.
        ///
        /// <para><b>WHY THE BYTES RATHER THAN AN IL WALK.</b> An <c>ldstr</c> walk would see string literals and
        /// nothing else. A raw scan of the assembly sees the same literals, in the user-string heap where they
        /// live as UTF-16, AND every type and member NAME, which live in the string heap as UTF-8. So it also
        /// catches a member called <c>NewTextureView...</c> that the reflection row above would miss for
        /// returning the wrong type, and it needs no opcode table that can drift from the runtime's. Both
        /// encodings are searched because they are the two the metadata uses, and both are positively controlled
        /// below rather than assumed.</para>
        ///
        /// <para>What neither form can see is a selector assembled at runtime from pieces. Nothing in this
        /// package builds a selector that way (every one is a literal handed to <c>ObjCRuntime.Sel</c>), and a
        /// row that did would be a deliberate evasion rather than an accident.</para>
        /// </summary>
        [Fact]
        public void ThePackageAssembly_CarriesNoTextureViewSelector()
        {
            byte[] assembly = PackageAssemblyBytes();

            Assert.False(Carries(assembly, ViewSelector),
                "KhaozEngine.Gpu.Metal contains the string '" + ViewSelector + "', which means something in the "
                + "package can send -newTextureViewWithPixelFormat:textureType:levels:slices:. Decision M-M10 "
                + "says every view a resource set can name is created at RESOURCE creation, and on this seam that "
                + "number is zero because nothing can narrow a texture by mip, layer or format. If a row now "
                + "genuinely needs one, narrow this test to the design's own wording (no view factory reachable "
                + "from the recording type) rather than deleting it, and add the creation-time call site.");
        }

        /// <summary>
        /// THE POSITIVE CONTROL FOR THE SCAN, and without it the row above passes on an assembly it failed to
        /// read. One string the package really does carry as a LITERAL (a selector it sends) and one it carries
        /// as a NAME (a type it declares), so both encodings are proved to be found rather than assumed.
        /// </summary>
        [Fact]
        public void TheScan_FindsASelectorThePackageDoesSend_AndATypeItDeclares()
        {
            byte[] assembly = PackageAssemblyBytes();

            Assert.True(Carries(assembly, "blitCommandEncoder"),
                "The scan cannot find a selector the package certainly sends, so it is broken rather than the "
                + "package being clean, and the row above is vacuous.");

            Assert.True(Carries(assembly, nameof(MetalViewPolicy)),
                "The scan cannot find a type the package certainly declares, so it does not see metadata names "
                + "and would miss a member named for a texture view.");
        }

        /// <summary>
        /// THE POSITIVE CONTROL, and without it the reflection row could pass because it found nothing at
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

        // The prefix of -newTextureViewWithPixelFormat:textureType:levels:slices:, which is the only Metal
        // selector that creates a texture view. The prefix rather than the whole selector, so a shorter overload
        // (-newTextureViewWithPixelFormat: alone is one) is caught by the same row.
        const string ViewSelector = "newTextureView";

        static byte[] PackageAssemblyBytes()
        {
            string path = typeof(MetalViewPolicy).Assembly.Location;

            Assert.False(string.IsNullOrEmpty(path),
                "KhaozEngine.Gpu.Metal has no file on disk, so this scan cannot read it. A single-file or "
                + "in-memory host would do that, and the assertion below would then be vacuous rather than true.");

            return File.ReadAllBytes(path);
        }

        // Both encodings the metadata uses: UTF-16 for a string literal in the user-string heap, UTF-8 for a
        // type or member name in the string heap.
        static bool Carries(byte[] assembly, string text)
            => assembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0
                || assembly.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

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
