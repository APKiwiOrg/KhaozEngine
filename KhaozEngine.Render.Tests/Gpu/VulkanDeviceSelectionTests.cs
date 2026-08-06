using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decisions V-N3 and V-G2: <c>KE_VULKAN_DEVICE</c>, and the policy that turns it into one physical device.
    /// Everything here is device-free, over a list a test writes by hand, which is the split that lets a policy
    /// about GPUs be asserted on a machine with no Vulkan loader.
    /// <para>
    /// The hole this closes is worse than the Direct3D 11 one it mirrors. There, <c>KE_D3D11_ADAPTER</c> guards
    /// against a runner image growing a paravirtual adapter. Here the Linux leg pins lavapipe at the LOADER
    /// level through <c>VK_ICD_FILENAMES</c>, a pin the workflow has already had to repair once, and the
    /// incumbent then takes device zero unconditionally.
    /// </para>
    /// </summary>
    public sealed class VulkanDeviceSelectionTests
    {
        static VulkanPhysicalDeviceInfo Eligible(string name, VulkanPhysicalDeviceClass kind,
            bool llvmpipe = false)
            => new(name, kind, llvmpipe, MeetsRequirements: true, RejectionReason: null);

        static VulkanPhysicalDeviceInfo Ineligible(string name, VulkanPhysicalDeviceClass kind, string why)
            => new(name, kind, IsLlvmpipe: false, MeetsRequirements: false, RejectionReason: why);

        static readonly VulkanPhysicalDeviceInfo[] TwoGpusAndLlvmpipe =
        [
            Eligible("NVIDIA GeForce RTX 4080", VulkanPhysicalDeviceClass.Discrete),
            Eligible("Intel UHD Graphics 770", VulkanPhysicalDeviceClass.Integrated),
            Eligible("llvmpipe (LLVM 17.0.6, 256 bits)", VulkanPhysicalDeviceClass.Cpu, llvmpipe: true),
        ];

        // ---- the parse ----

        /// <summary>Every recognized token, case-insensitively and with the whitespace a shell leaves
        /// behind.</summary>
        [Theory]
        [InlineData("llvmpipe", (int)VulkanDeviceRequestKind.Llvmpipe)]
        [InlineData("  LLVMpipe  ", (int)VulkanDeviceRequestKind.Llvmpipe)]
        [InlineData("discrete", (int)VulkanDeviceRequestKind.Discrete)]
        [InlineData("DISCRETE", (int)VulkanDeviceRequestKind.Discrete)]
        [InlineData("integrated", (int)VulkanDeviceRequestKind.Integrated)]
        [InlineData("cpu", (int)VulkanDeviceRequestKind.Cpu)]
        // The expected kind travels as an int because the enum is internal to the backend package and a public
        // xUnit test method may not name it in its signature. The same reason D3D11DeviceLossLatchTests passes
        // its HRESULTs as ints.
        public void TheFourNamedTokens_Parse(string value, int expected)
            => Assert.Equal(expected, (int)VulkanPhysicalDeviceSelection.Parse(value).Kind);

        /// <summary>Unset and blank are the default, and carry no raw value, so nothing warns about a variable
        /// nobody set.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnsetOrBlank_IsTheDefault(string? value)
        {
            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse(value);

            Assert.Equal(VulkanDeviceRequestKind.Default, request.Kind);
            Assert.Null(request.RawValue);
        }

        /// <summary>An integer is an index, read in the invariant culture so a machine with a comma decimal
        /// separator reads "2" the way every other machine does.</summary>
        [Theory]
        [InlineData("0", 0)]
        [InlineData(" 2 ", 2)]
        [InlineData("-1", -1)]
        [InlineData("99", 99)]
        public void AnInteger_IsAnIndex(string value, int expected)
        {
            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse(value);

            Assert.Equal(VulkanDeviceRequestKind.Index, request.Kind);
            Assert.Equal(expected, request.Index);
        }

        /// <summary>
        /// Anything else is a name substring, and there is deliberately no unrecognized case: that is the only
        /// reading under which somebody typing their GPU's name gets what they meant. The raw value is kept
        /// verbatim, quotes and stray spaces and all, because that is what a warning has to make visible.
        /// </summary>
        [Fact]
        public void AnythingElse_IsANameSubstring()
        {
            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse(" GeForce ");

            Assert.Equal(VulkanDeviceRequestKind.NameSubstring, request.Kind);
            Assert.Equal("GeForce", request.Name);
            Assert.Equal(" GeForce ", request.RawValue);
        }

        // ---- the choice ----

        /// <summary>
        /// THE DEFAULT REPRODUCES <c>physicalDevices[0]</c>, which is decision V-N3 and section 2.9's continuity
        /// prior. Scoring devices was rejected: it changes which GPU the engine runs on for reasons unrelated to
        /// swapping the backend, and it breaks <c>DeviceName</c> parity in a design demanding zero capability
        /// differences.
        /// </summary>
        [Fact]
        public void TheDefault_TakesDeviceZero()
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse(null), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(0, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// The default FILTERS by the requirements, and the substitution says so in as many words. That wording
        /// is the whole reason the default path logs at all where the Direct3D 11 equivalent stays quiet: a soak
        /// session has to be able to tell "this run chose device 1" from "device 0 cannot run the backend", and
        /// only one of those is comparable with an incumbent run that took device zero unconditionally.
        /// </summary>
        [Fact]
        public void TheDefault_SubstitutesPastAnIneligibleZero_AndSaysSo()
        {
            VulkanPhysicalDeviceInfo[] devices =
            [
                Ineligible("Old Radeon", VulkanPhysicalDeviceClass.Discrete, "its Vulkan apiVersion is 1.2.0"),
                Eligible("llvmpipe", VulkanPhysicalDeviceClass.Cpu, llvmpipe: true),
            ];

            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse(null);
            int chosen = VulkanPhysicalDeviceSelection.Choose(request, devices, out string? warning);

            Assert.Equal(1, chosen);
            Assert.Null(warning);

            string described = VulkanPhysicalDeviceSelection.Describe(chosen, request, devices);
            Assert.Contains("SUBSTITUTED", described, StringComparison.Ordinal);
            Assert.Contains("1.2.0", described, StringComparison.Ordinal);
        }

        /// <summary>An index in range and eligible is honoured exactly.</summary>
        [Fact]
        public void AnIndex_IsHonoured()
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("1"), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(1, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// A name substring matches case-insensitively, which is what makes the variable usable by somebody who
        /// typed their GPU's name off a settings screen rather than out of the driver string.
        /// </summary>
        [Fact]
        public void ANameSubstring_MatchesCaseInsensitively()
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("intel uhd"), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(1, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// THE VALUE CI PINS. <c>llvmpipe</c> finds the software rasterizer wherever the loader happens to
        /// enumerate it, which is the belt to the loader-level brace the leg relies on today, and it is a
        /// device-level pin rather than an ICD-file one precisely because the ICD manifest has moved before.
        /// </summary>
        [Fact]
        public void Llvmpipe_FindsTheSoftwareRasterizerWhereverItIs()
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("llvmpipe"), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(2, chosen);
            Assert.Null(warning);
            Assert.True(TwoGpusAndLlvmpipe[chosen].IsSoftwareRasterizer);
        }

        /// <summary>The three device-class tokens each pick their own class.</summary>
        [Theory]
        [InlineData("discrete", 0)]
        [InlineData("integrated", 1)]
        [InlineData("cpu", 2)]
        public void TheClassTokens_PickTheirClass(string value, int expected)
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse(value), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(expected, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// A NAMED-BUT-ABSENT DEVICE IS A WARN PLUS THE DEFAULT, never a hard failure, which V-N3 says in as many
        /// words. A name substring is machine-specific by nature, so a value that is right on the machine it was
        /// written on is wrong on the next one, and turning that into a refusal to start would make a diagnostic
        /// lever into a way of bricking a session. The warning lists what WAS enumerated, because "nothing
        /// matched" without the list sends the reader to check their spelling when the machine changed.
        /// </summary>
        [Fact]
        public void AnAbsentName_WarnsAndTakesTheDefault()
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("Radeon"), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("Radeon", warning, StringComparison.Ordinal);
            Assert.Contains("NVIDIA GeForce RTX 4080", warning, StringComparison.Ordinal);
        }

        /// <summary>An out-of-range index warns with the count and the list, and takes the default.</summary>
        [Theory]
        [InlineData("9")]
        [InlineData("-1")]
        public void AnOutOfRangeIndex_WarnsAndTakesTheDefault(string value)
        {
            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse(value), TwoGpusAndLlvmpipe, out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("enumerated", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// AN INELIGIBLE DEVICE IS NEVER CHOSEN, even by an explicit index, and the warning quotes the device's
        /// own rejection reason. Honouring the pin would trade a warning now for a crash on frame one, which is
        /// the trade the whole requirement filter exists to refuse.
        /// </summary>
        [Fact]
        public void AnExplicitIndexOntoAnIneligibleDevice_IsRefusedWithItsReason()
        {
            VulkanPhysicalDeviceInfo[] devices =
            [
                Eligible("llvmpipe", VulkanPhysicalDeviceClass.Cpu, llvmpipe: true),
                Ineligible("Old Radeon", VulkanPhysicalDeviceClass.Discrete, "it reports no dynamicRendering"),
            ];

            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("1"), devices, out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("dynamicRendering", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// No eligible device at all reports <see cref="VulkanPhysicalDeviceSelection.NoDevice"/> and warns about
        /// NOTHING, because the caller's own failure names every device and its own reason, which is strictly more
        /// than a warning about a variable could say.
        /// </summary>
        [Fact]
        public void NoEligibleDevice_ReportsNoDeviceAndDoesNotWarn()
        {
            VulkanPhysicalDeviceInfo[] devices =
            [
                Ineligible("Old Radeon", VulkanPhysicalDeviceClass.Discrete, "its Vulkan apiVersion is 1.2.0"),
            ];

            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("llvmpipe"), devices, out string? warning);

            Assert.Equal(VulkanPhysicalDeviceSelection.NoDevice, chosen);
            Assert.Null(warning);
        }

        /// <summary>An empty enumeration is answered rather than indexed into, which is the shape a machine whose
        /// loader resolved with no ICD behind it actually presents.</summary>
        [Fact]
        public void AnEmptyEnumeration_IsAnswered()
        {
            var empty = Array.Empty<VulkanPhysicalDeviceInfo>();

            int chosen = VulkanPhysicalDeviceSelection.Choose(
                VulkanPhysicalDeviceSelection.Parse("2"), empty, out string? warning);

            Assert.Equal(VulkanPhysicalDeviceSelection.NoDevice, chosen);
            Assert.Null(warning);
            Assert.Contains("no physical device", VulkanPhysicalDeviceSelection.Describe(
                chosen, VulkanPhysicalDeviceSelection.Parse("2"), empty), StringComparison.Ordinal);
        }

        /// <summary>
        /// V-G2's telemetry read: <c>deviceType == Cpu || driverID == MesaLlvmpipe</c>, which is what lands in the
        /// EXISTING <c>softwareAdapter</c> field rather than in a new one. Both halves are asserted because a
        /// virtual GPU running lavapipe is real and reads software through the driver id alone.
        /// </summary>
        [Theory]
        [InlineData((int)VulkanPhysicalDeviceClass.Cpu, false, true)]
        [InlineData((int)VulkanPhysicalDeviceClass.Virtual, true, true)]
        [InlineData((int)VulkanPhysicalDeviceClass.Discrete, false, false)]
        [InlineData((int)VulkanPhysicalDeviceClass.Integrated, false, false)]
        public void SoftwareRasterizer_IsCpuOrLlvmpipe(int kind, bool llvmpipe, bool expected)
        {
            VulkanPhysicalDeviceInfo device = Eligible("whatever", (VulkanPhysicalDeviceClass)kind, llvmpipe);

            Assert.Equal(expected, device.IsSoftwareRasterizer);
        }

        /// <summary>An explicit selection names the variable it came from, so a capture can tell a pinned session
        /// from a default one without reading the environment it was taken in.</summary>
        [Fact]
        public void AnExplicitChoice_NamesTheVariable()
        {
            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse("llvmpipe");
            int chosen = VulkanPhysicalDeviceSelection.Choose(request, TwoGpusAndLlvmpipe, out _);

            string described = VulkanPhysicalDeviceSelection.Describe(chosen, request, TwoGpusAndLlvmpipe);

            Assert.Contains("KE_VULKAN_DEVICE='llvmpipe'", described, StringComparison.Ordinal);
            Assert.Contains("SOFTWARE", described, StringComparison.Ordinal);
        }

        /// <summary>The default on a machine where device zero qualifies names the variable as a HINT rather than
        /// as a source, so the line reads the same way the Direct3D 11 one does.</summary>
        [Fact]
        public void TheDefaultDescription_OffersTheVariable()
        {
            VulkanDeviceRequest request = VulkanPhysicalDeviceSelection.Parse(null);
            IReadOnlyList<VulkanPhysicalDeviceInfo> devices = TwoGpusAndLlvmpipe;

            string described = VulkanPhysicalDeviceSelection.Describe(0, request, devices);

            Assert.Contains("the default", described, StringComparison.Ordinal);
            Assert.Contains("KE_VULKAN_DEVICE=llvmpipe|discrete|integrated|cpu", described,
                StringComparison.Ordinal);
        }
    }
}
