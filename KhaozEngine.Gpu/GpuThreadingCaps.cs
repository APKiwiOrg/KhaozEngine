namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The graphics driver's multi-threading capabilities, read off a live Direct3D11 device
    /// (<c>ID3D11Device::CheckFeatureSupport</c> with <c>D3D11_FEATURE_THREADING</c>). Meaningful on Direct3D11
    /// ONLY: every other backend leaves <see cref="GpuDeviceContext.ThreadingCaps"/> null, and so does a query
    /// that failed. See <see cref="GpuThreadingDiagnostics"/> for the display strings.
    /// </summary>
    /// <param name="DriverCommandLists">
    /// <c>D3D11_FEATURE_DATA_THREADING.DriverCommandLists</c>. True when the DRIVER builds deferred-context
    /// command lists itself. False when it cannot, in which case the D3D11 runtime emulates them in software by
    /// recording every call into a token stream and replaying it on the immediate context at submit time. That
    /// emulation puts a fixed cost on every single recorded call, and it is the pathological case worth warning
    /// about: it can cost an order of magnitude of frame rate versus the same machine on Vulkan.
    /// </param>
    /// <param name="DriverConcurrentCreates">
    /// <c>D3D11_FEATURE_DATA_THREADING.DriverConcurrentCreates</c>. True when resources can be created on other
    /// threads while drawing. False means coarse driver-side synchronization serializes those creates. Carried
    /// because it comes back in the same query and it is the other half of the same driver's threading story,
    /// not because the engine branches on it.
    /// </param>
    public readonly record struct GpuThreadingCaps(bool DriverCommandLists, bool DriverConcurrentCreates)
    {
        /// <summary>
        /// True when the driver does NOT build command lists, so the D3D11 runtime is emulating them in software.
        /// The whole reason this probe exists.
        /// </summary>
        public bool CommandListsAreEmulated => !DriverCommandLists;
    }

    /// <summary>
    /// Pure, device-free formatting AND warn decisions for <see cref="GpuThreadingCaps"/>: the one-line INFO
    /// description, which of the two warnings (an emulating driver, or a probe that faulted) belongs under it, and
    /// both warning bodies. Split out from the probe so the wording and the decision are headless-testable on any
    /// platform, since the native query behind them can only run on Windows, on Direct3D11, while both device
    /// creation paths that consume them can be driven anywhere. A game debug overlay can use <see cref="Describe"/>
    /// directly.
    /// </summary>
    public static class GpuThreadingDiagnostics
    {
        /// <summary>What <see cref="Describe"/> reports when there are no caps to report: a non-Direct3D11 device,
        /// a non-Windows host, or a query that failed. The three are deliberately one bucket, because none of them
        /// tells a reader anything about the driver.</summary>
        public const string UnknownDescription =
            "unknown (not a Direct3D11 device, or the capability query did not run)";

        /// <summary>The INFO-line body for a threading-caps value, or <see cref="UnknownDescription"/> for null.
        /// Names the raw D3D11 field names so a log line can be matched against the Microsoft documentation, then
        /// says in plain words what the answer means.</summary>
        public static string Describe(GpuThreadingCaps? caps)
        {
            if (caps is not GpuThreadingCaps c) return UnknownDescription;
            string verdict = c.DriverCommandLists
                ? "the driver builds command lists"
                : "the D3D11 runtime is EMULATING command lists in software";
            return $"DriverCommandLists={Yes(c.DriverCommandLists)}, "
                + $"DriverConcurrentCreates={Yes(c.DriverConcurrentCreates)} ({verdict})";
        }

        /// <summary>True only for a KNOWN-bad driver: caps were read and <c>DriverCommandLists</c> came back false.
        /// An unknown value never warns, because "we could not ask" is not evidence of a problem.</summary>
        public static bool ShouldWarn(GpuThreadingCaps? caps) => caps is { DriverCommandLists: false };

        /// <summary>
        /// The WARN body logged when <see cref="ShouldWarn"/> is true. Written for a tester reading their own log
        /// with no graphics background: it says what is wrong, how bad it is, and the one thing to try next.
        /// </summary>
        public static string EmulatedCommandListsWarning { get; } =
            "This graphics driver reports DriverCommandLists=FALSE, so Windows has to emulate Direct3D11 command "
            + "lists in software instead of letting the driver build them. That is a KNOWN SEVERE performance "
            + "risk: it puts a fixed cost on every single drawing command the game records, and the same machine "
            + "can run many times faster on another backend. If this session feels slow, set "
            + $"{GpuBackendSelector.EnvVarName}=vulkan and compare before investigating anything else.";

        /// <summary>
        /// The WARN body for a probe that was ATTEMPTED and produced no answer, naming <paramref name="failure"/>
        /// as the reason. A different fact from <see cref="EmulatedCommandListsWarning"/>, which reports a driver
        /// that answered badly: this one reports that nothing was learned, so it says what a later report cannot
        /// rule out rather than telling the reader to change something.
        /// </summary>
        internal static string ProbeFailureWarning(string failure)
            => $"Could not read the Direct3D11 driver threading capabilities ({failure}). "
                + "Rendering is unaffected, but a slow-session report from this run cannot rule out a driver "
                + "that emulates command lists.";

        /// <summary>
        /// The one WARN (if any) belonging under the Direct3D11 threading INFO line, given the caps and the probe's
        /// failure reason. Null means no warning at all. A known-bad driver wins over a failure reason: caps that
        /// came back are the actionable fact, and in practice the two arms are exclusive anyway, since a probe that
        /// failed has no caps to be bad.
        /// <para>
        /// Pure and separate from the logging so both device-creation paths can be pinned headlessly. The failure
        /// string is the one input an ADOPTED native device must supply for itself (there is no Veldrid device for
        /// the context to probe), and a provider whose raw-pointer probe faulted has to still produce this warning
        /// instead of a bare "unknown" INFO line.
        /// </para>
        /// </summary>
        internal static string? WarningFor(GpuThreadingCaps? caps, string? probeFailure)
        {
            if (ShouldWarn(caps)) return EmulatedCommandListsWarning;
            return probeFailure is null ? null : ProbeFailureWarning(probeFailure);
        }

        static string Yes(bool value) => value ? "TRUE" : "FALSE";
    }
}
