using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION G2: <c>KE_D3D11_ADAPTER</c>, its parse, and the policy that turns it into one adapter.
    /// <para>
    /// THE ENUMERATION IS WINDOWS-ONLY AND THE CHOICE IS NOT, which is the whole reason
    /// <see cref="D3D11AdapterSelection"/> decides over a list of descriptions and flags rather than over
    /// <c>IDXGIAdapter</c> objects. Every rule below therefore runs under a plain <c>dotnet test</c> on macOS, and
    /// the untested-here pieces are the Windows glue in <c>D3D11DxgiQueries</c> and <c>D3D11AdapterResolution</c>,
    /// which read descriptions, flags and the IDXGIFactory6 ranking and decide nothing.
    /// </para>
    /// <para>
    /// The reason this matters is CI integrity rather than convenience. The Windows golden leg runs on WARP only
    /// because <c>windows-latest</c> carries no hardware adapter and DXGI falls back, so a runner image that grows
    /// a paravirtual adapter would silently change the rasterizer the 36 committed goldens are compared on, and
    /// the failure would arrive as a diff on unrelated goldens with nothing naming the cause.
    /// </para>
    /// </summary>
    public sealed class D3D11AdapterSelectionTests
    {
        static readonly IReadOnlyList<D3D11AdapterInfo> TwoAdapters = new[]
        {
            new D3D11AdapterInfo("NVIDIA GeForce RTX 4070", isSoftware: false),
            new D3D11AdapterInfo("Microsoft Basic Render Driver", isSoftware: true),
        };

        static readonly IReadOnlyList<D3D11AdapterInfo> SoftwareOnly = new[]
        {
            new D3D11AdapterInfo("Microsoft Basic Render Driver", isSoftware: true),
        };

        static readonly IReadOnlyList<D3D11AdapterInfo> None = Array.Empty<D3D11AdapterInfo>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_UnsetOrBlankLeavesDxgiToPick(string? value)
        {
            D3D11AdapterRequest request = D3D11AdapterSelection.Parse(value);

            Assert.Equal(D3D11AdapterRequestKind.Default, request.Kind);
            Assert.Null(request.RawValue);
        }

        [Theory]
        [InlineData("warp")]
        [InlineData("WARP")]
        [InlineData("  Warp  ")]
        public void Parse_ReadsWarpCaseInsensitivelyAndTrimmed(string value)
        {
            Assert.Equal(D3D11AdapterRequestKind.Warp, D3D11AdapterSelection.Parse(value).Kind);
        }

        [Theory]
        [InlineData("hardware")]
        [InlineData("HARDWARE")]
        public void Parse_ReadsHardware(string value)
        {
            Assert.Equal(D3D11AdapterRequestKind.Hardware, D3D11AdapterSelection.Parse(value).Kind);
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("3", 3)]
        [InlineData(" 12 ", 12)]
        [InlineData("-1", -1)]
        public void Parse_ReadsAnIntegerAsAnIndex(string value, int expected)
        {
            D3D11AdapterRequest request = D3D11AdapterSelection.Parse(value);

            Assert.Equal(D3D11AdapterRequestKind.Index, request.Kind);
            Assert.Equal(expected, request.Index);
        }

        /// <summary>
        /// THERE IS DELIBERATELY NO UNRECOGNIZED CASE IN THE PARSE. Anything that is not warp, hardware or an
        /// integer is a name substring, because that is the only reading under which a user typing their GPU's
        /// name gets what they meant. Whether the request can be SATISFIED is <see cref="D3D11AdapterSelection.Choose"/>'s
        /// question, and that is where the WARN lives.
        /// </summary>
        [Fact]
        public void Parse_ReadsAnythingElseAsANameSubstringAndKeepsTheRawValue()
        {
            D3D11AdapterRequest request = D3D11AdapterSelection.Parse("  GeForce  ");

            Assert.Equal(D3D11AdapterRequestKind.NameSubstring, request.Kind);
            Assert.Equal("GeForce", request.Name);
            Assert.Equal("  GeForce  ", request.RawValue);
        }

        /// <summary>
        /// THE DEFAULT PREFERS THE HIGH-PERFORMANCE ADAPTER (https://github.com/APKiwiOrg/KhaozEngine/issues/1115).
        /// DXGI lists the adapter driving the display first, which on a hybrid laptop is the integrated GPU, so
        /// letting DXGI pick ran the engine on the slower of two GPUs. The choice names no index, because the
        /// ranking is IDXGIFactory6's and not the enumeration's.
        /// </summary>
        [Fact]
        public void Choose_UnsetPrefersTheHighPerformanceAdapterWhenIDXGIFactory6IsOffered()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(null), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.HighPerformance, choice.Kind);
            Assert.True(choice.Index < 0);
            Assert.Null(warning);
        }

        /// <summary>The missing-interface fallback. IDXGIFactory6 arrived in Windows 10 1803, and without it the
        /// engine lets DXGI pick as it always did. That is not a warning: nothing was asked for that could not be
        /// given.</summary>
        [Fact]
        public void Choose_UnsetWithoutIDXGIFactory6LetsDxgiPickAndWarnsAboutNothing()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(null), TwoAdapters, gpuPreferenceAvailable: false, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.Null(warning);
        }

        /// <summary>WARP is NOT resolved against the enumeration, on purpose: <c>DriverType.Warp</c> reaches it on
        /// every Windows machine including one whose factory enumerates no software adapter at all, so resolving
        /// it through the list would make the one value CI depends on the one value that can fail to
        /// resolve.</summary>
        [Fact]
        public void Choose_WarpNeverConsultsTheEnumeration()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("warp"), None, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.WarpDriver, choice.Kind);
            Assert.Null(warning);
        }

        [Fact]
        public void Choose_HardwareTakesTheFirstAdapterThatIsNotSoftware()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("hardware"), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.Enumerated, choice.Kind);
            Assert.Equal(0, choice.Index);
            Assert.Null(warning);
        }

        [Fact]
        public void Choose_HardwareWarnsAndFallsBackWhenThereIsNoHardwareAdapter()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("hardware"), SoftwareOnly, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, warning, StringComparison.Ordinal);
            Assert.Contains("Microsoft Basic Render Driver", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void Choose_IndexTakesThatAdapter()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("1"), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.Enumerated, choice.Kind);
            Assert.Equal(1, choice.Index);
            Assert.Null(warning);
        }

        [Theory]
        [InlineData("2")]
        [InlineData("-1")]
        [InlineData("99")]
        public void Choose_AnOutOfRangeIndexWarnsAndFallsBack(string value)
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(value), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
            Assert.Contains(value, warning, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("GeForce", 0)]
        [InlineData("geforce", 0)]
        [InlineData("Basic Render", 1)]
        public void Choose_ANameSubstringMatchesCaseInsensitively(string value, int expected)
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(value), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.Enumerated, choice.Kind);
            Assert.Equal(expected, choice.Index);
            Assert.Null(warning);
        }

        /// <summary>
        /// A NAMED ADAPTER THAT IS NOT PRESENT IS A WARN PLUS DEFAULT ENUMERATION, NEVER A HARD FAILURE, and the
        /// warning lists what WAS there. A name substring is machine-specific by nature, so a value that is
        /// correct on the machine it was written on is wrong on the next one, and "nothing matched" without the
        /// list sends the reader to check their spelling when the real answer is usually that the machine changed.
        /// </summary>
        [Fact]
        public void Choose_ANameThatMatchesNothingWarnsWithTheListAndFallsBack()
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse("Radeon"), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
            Assert.Contains("Radeon", warning, StringComparison.Ordinal);
            Assert.Contains("NVIDIA GeForce RTX 4070", warning, StringComparison.Ordinal);
            Assert.Contains("Microsoft Basic Render Driver", warning, StringComparison.Ordinal);
            Assert.Contains("(software)", warning, StringComparison.Ordinal);
        }

        /// <summary>An empty enumeration is a perfectly good input: every request warns and falls back to letting
        /// DXGI pick, which is the behaviour the engine had before this lever existed.</summary>
        [Theory]
        [InlineData("hardware")]
        [InlineData("0")]
        [InlineData("GeForce")]
        public void Choose_NeverThrowsOnAMachineThatEnumeratesNothing(string value)
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(value), None, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
        }

        /// <summary>Decision G2's telemetry half, as far as the CHOICE can decide it. The default case answers
        /// false and that is NOT a claim the adapter is hardware: nothing here knows which one DXGI picked, which
        /// is why the device reads the flag off the created device and ORs the two.</summary>
        [Fact]
        public void IsSoftwareChoice_IsTrueForWarpAndForAFlaggedAdapterOnly()
        {
            Assert.True(D3D11AdapterSelection.IsSoftwareChoice(D3D11AdapterChoice.Warp, TwoAdapters));
            Assert.True(D3D11AdapterSelection.IsSoftwareChoice(
                new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, 1), TwoAdapters));
            Assert.False(D3D11AdapterSelection.IsSoftwareChoice(
                new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, 0), TwoAdapters));
            Assert.False(D3D11AdapterSelection.IsSoftwareChoice(D3D11AdapterChoice.Default, TwoAdapters));
        }

        [Fact]
        public void Describe_NamesTheAdapterAndTheLeverThatChoseIt()
        {
            Assert.Contains("WARP", D3D11AdapterSelection.Describe(D3D11AdapterChoice.Warp, TwoAdapters),
                StringComparison.Ordinal);
            Assert.Contains("NVIDIA GeForce RTX 4070", D3D11AdapterSelection.Describe(
                new D3D11AdapterChoice(D3D11AdapterChoiceKind.Enumerated, 0), TwoAdapters), StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName,
                D3D11AdapterSelection.Describe(D3D11AdapterChoice.Default, TwoAdapters), StringComparison.Ordinal);
        }

        /// <summary>The preference does not depend on the enumeration. A machine with one adapter, only a software
        /// adapter, or an enumeration that failed still asks IDXGIFactory6, which answers with whatever it ranks
        /// first.</summary>
        [Fact]
        public void Choose_UnsetPrefersHighPerformanceWhateverTheEnumerationHolds()
        {
            var one = new[] { new D3D11AdapterInfo("Intel(R) UHD Graphics", isSoftware: false) };

            foreach (IReadOnlyList<D3D11AdapterInfo> adapters in new[] { one, SoftwareOnly, None })
            {
                D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                    D3D11AdapterSelection.Parse(null), adapters, gpuPreferenceAvailable: true, out string? warning);

                Assert.Equal(D3D11AdapterChoiceKind.HighPerformance, choice.Kind);
                Assert.Null(warning);
            }
        }

        /// <summary>
        /// EVERY EXPLICIT VALUE KEEPS WINNING, with the preference offered or not, and warp above all, because it is
        /// the value CI pins so the committed goldens keep their rasterizer. The expected kind travels as an int
        /// because the enum is internal and a public test method cannot take it as a parameter.
        /// </summary>
        [Theory]
        [InlineData("warp", (int)D3D11AdapterChoiceKind.WarpDriver, -1)]
        [InlineData("hardware", (int)D3D11AdapterChoiceKind.Enumerated, 0)]
        [InlineData("1", (int)D3D11AdapterChoiceKind.Enumerated, 1)]
        [InlineData("GeForce", (int)D3D11AdapterChoiceKind.Enumerated, 0)]
        [InlineData("Basic Render", (int)D3D11AdapterChoiceKind.Enumerated, 1)]
        public void Choose_EveryExplicitValueWinsOverTheHighPerformanceDefault(string value, int expectedKind,
            int expectedIndex)
        {
            foreach (bool available in new[] { true, false })
            {
                D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                    D3D11AdapterSelection.Parse(value), TwoAdapters, available, out string? warning);

                Assert.Equal((D3D11AdapterChoiceKind)expectedKind, choice.Kind);
                Assert.Equal(expectedIndex, choice.Index);
                Assert.Null(warning);
            }
        }

        /// <summary>An explicit value that cannot be honoured still lets DXGI pick, as it did before the
        /// preference existed, and never lands on the high-performance default: a pin that failed says so and lands
        /// where it always has.</summary>
        [Theory]
        [InlineData("2")]
        [InlineData("Radeon")]
        public void Choose_AnUnhonourableExplicitValueStillLetsDxgiPick(string value)
        {
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(
                D3D11AdapterSelection.Parse(value), TwoAdapters, gpuPreferenceAvailable: true, out string? warning);

            Assert.Equal(D3D11AdapterChoiceKind.DefaultEnumeration, choice.Kind);
            Assert.NotNull(warning);
        }

        /// <summary>The high-performance choice names no enumerated adapter, so like DXGI's own pick it answers
        /// false and leaves the flag to the created device, which is the authority on which adapter ran.</summary>
        [Fact]
        public void IsSoftwareChoice_LeavesTheHighPerformanceAdapterToTheCreatedDevice()
        {
            Assert.False(D3D11AdapterSelection.IsSoftwareChoice(D3D11AdapterChoice.HighPerformance, SoftwareOnly));
        }

        [Fact]
        public void Describe_NamesTheHighPerformanceDefaultAndTheLeverThatOverridesIt()
        {
            string line = D3D11AdapterSelection.Describe(D3D11AdapterChoice.HighPerformance, TwoAdapters);

            Assert.Contains("high-performance", line, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, line, StringComparison.Ordinal);
        }

        /// <summary>The two ways the default can fail on a real factory both WARN in the same shape as every
        /// unhonourable request above, naming the lever and saying DXGI is picking. The glue that raises them is
        /// Windows-only, so the wording is pinned here.</summary>
        [Fact]
        public void HighPerformanceFallbacks_WarnNamingTheLeverAndThatDxgiPicks()
        {
            string unavailable = D3D11AdapterSelection.HighPerformanceUnavailableWarning;
            string refused = D3D11AdapterSelection.HighPerformanceCreateFailedWarning("DXGI_ERROR_UNSUPPORTED");

            Assert.Contains("Letting DXGI pick", unavailable, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, unavailable, StringComparison.Ordinal);
            Assert.Contains("Letting DXGI pick", refused, StringComparison.Ordinal);
            Assert.Contains("DXGI_ERROR_UNSUPPORTED", refused, StringComparison.Ordinal);
            Assert.Contains(D3D11AdapterSelection.EnvVarName, refused, StringComparison.Ordinal);
        }

        /// <summary>Reading the live environment is the one impure member, and it has to work everywhere: the
        /// answer depends on the machine, so what is asserted is that asking is legal off Windows.</summary>
        [Fact]
        public void FromEnvironment_IsReadableOnAnyOperatingSystem()
        {
            _ = D3D11AdapterSelection.FromEnvironment();
        }
    }
}
