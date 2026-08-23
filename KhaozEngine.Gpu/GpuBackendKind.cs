namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The graphics backend the engine runs on. Selection is centralized in <see cref="GpuBackendSelector"/>, and
    /// the active backend is exposed on <see cref="GpuDeviceContext.Backend"/>. <see cref="Direct3D11Native"/>,
    /// <see cref="VulkanNative"/> and <see cref="MetalNative"/> name the engine-owned implementations, which
    /// arrive through <see cref="GpuBackendProviders"/> and are the only members that can create a device.
    /// <see cref="Metal"/>, <see cref="Vulkan"/>, <see cref="Direct3D11"/> and <see cref="OpenGL"/> named the
    /// Veldrid backend removed in 18.0.0 and are RETIRED (<see cref="GpuBackendSelector.IsRetired"/>).
    /// </summary>
    /// <remarks>
    /// Members are APPEND-ONLY and pinned to explicit values, the same contract
    /// <see cref="GpuBackendSource"/> carries. A consuming game persists the player's chosen backend as a stored
    /// preference and hands it back here as a <see cref="GpuBackendKind"/>, so renumbering would silently repoint
    /// every saved graphics setting at a different backend. Never reorder, renumber, or remove one.
    /// <para>
    /// Appending IS supported, and section 4.3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> is the audit an append has to pass, walked a
    /// second time for Vulkan in section 4.2 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> and a third time for Metal in section 4.2 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>. The enum
    /// itself is the safe part. What is not safe is every place that switches on it, compares against it, or
    /// derives a string from it: three of those degrade a new backend SILENTLY rather than failing, and the worst
    /// of them did not throw at all (a discard arm that asked Veldrid for a Metal device). Walk the table.
    /// </para>
    /// <para>
    /// The audit is an executable one now, which is what made the second append a diff rather than a
    /// re-derivation: <c>GpuBackendKindAppendAuditTests</c> and its Vulkan and Metal siblings in
    /// <c>KhaozEngine.Render.Tests</c> carry one device-free test per site, so a fourth append finds every
    /// decision already written down and pinned.
    /// </para>
    /// <para>
    /// SINCE 18.0.0 THE THREE NATIVE MEMBERS ARE THE ONLY LIVE ONES. The OS probe answers
    /// <see cref="MetalNative"/> on macOS, <see cref="Direct3D11Native"/> on Windows and
    /// <see cref="VulkanNative"/> on Linux and everything else, and the Veldrid backend that implemented the
    /// other four was deleted by https://github.com/APKiwiOrg/KhaozEngine/issues/687.
    /// </para>
    /// <para>
    /// THE FOUR MEMBERS THEMSELVES ARE KEPT FOREVER, because the enum is append-only and a game has persisted
    /// them as a player's saved choice. They are RETIRED rather than removed and rather than repointed:
    /// <see cref="GpuBackendSelector.IsRetired"/> answers for them, a stored preference or a
    /// <c>KE_GRAPHICS_BACKEND</c> token naming one is redirected onto that API's native backend with a WARN
    /// (reported to the game as <see cref="GpuBackendSource.FallbackAfterFailure"/>, which is the signal it
    /// already clears a stored choice on), and naming one in CODE throws
    /// <see cref="GpuBackendRetiredException"/>. Section 5.2 of
    /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> rules out the tidy-looking alternative by name:
    /// repointing <see cref="Direct3D11"/> at <see cref="Direct3D11Native"/> would move every Windows tester's
    /// stored choice onto a different implementation with no rebuild signal and no player notice.
    /// </para>
    /// <para>
    /// WHICH sites degrade silently is not a fixed list, and the third append is where that stopped being a
    /// formality. <see cref="MetalNative"/> is the first appended member for which the two software frame-cap
    /// sites are NOT correct by default: they apply a real cap only on Metal, so an append that left them alone
    /// would take the cap away from the native Mac client and say nothing. Read the append's own section rather
    /// than assuming the previous one's answers carry.
    /// </para>
    /// </remarks>
    public enum GpuBackendKind
    {
        /// <summary>
        /// RETIRED IN 18.0.0. Apple Metal through Veldrid: the macOS default until 17.40.0 and the one-release
        /// opt-out after it. Redirects to <see cref="MetalNative"/> from a stored preference or a
        /// <c>KE_GRAPHICS_BACKEND=metal</c> token, and throws <see cref="GpuBackendRetiredException"/> when
        /// named in code. See the remarks above.
        /// </summary>
        Metal = 0,
        /// <summary>
        /// RETIRED IN 18.0.0. Vulkan through Veldrid: the Linux (and catch-all) default until 17.40.0 and the
        /// one-release opt-out after it. Redirects to <see cref="VulkanNative"/>. See the remarks above.
        /// </summary>
        Vulkan = 1,
        /// <summary>
        /// RETIRED IN 18.0.0. Direct3D 11 through Veldrid: the Windows default until 17.40.0 and the
        /// one-release opt-out after it. Redirects to <see cref="Direct3D11Native"/>. See the remarks above.
        /// </summary>
        Direct3D11 = 2,
        /// <summary>
        /// RETIRED IN 18.0.0. OpenGL through Veldrid, and the one retirement with NO native replacement: the
        /// engine never had an OpenGL implementation of its own and is not gaining one, so it redirects to
        /// whatever <see cref="GpuBackendSelector.ProbeOS"/> answers for the platform. It was never offered by
        /// <see cref="GpuBackendSelector.SupportedBackends"/> either, because there is no windowed GL path.
        /// See the remarks above.
        /// </summary>
        OpenGL = 3,

        /// <summary>
        /// Direct3D 11 through the engine's OWN native backend (<c>KhaozEngine.Gpu.D3D11</c>) rather than through
        /// Veldrid. THE WINDOWS DEFAULT since 17.40.0, still selectable by name
        /// (<c>KE_GRAPHICS_BACKEND=d3d11-native</c>), and created by the
        /// <see cref="IGpuBackendProvider"/> that package registers, never by this one: it is a separate member
        /// precisely so a session log, a telemetry header and a frame time are attributed to the implementation
        /// that actually ran. It renders the SAME images as <see cref="Direct3D11"/>, and from <c>17.41.0</c> it
        /// OWNS the <c>direct3d11-native</c> golden family. It was a GUEST in the incumbent's <c>direct3d11</c>
        /// family until then (decision I3), which was the strongest free proof the port had, and row 2 of the
        /// Veldrid removal promoted it because the incumbent that owned <c>direct3d11</c> was being deleted and a
        /// family whose owner is gone is a set of references nothing may ever re-bake
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/685">#685</see>). The new family was
        /// seeded as a byte-identical COPY of the incumbent's, so the guest-era agreement survives as committed
        /// bytes.
        /// </summary>
        Direct3D11Native = 4,

        /// <summary>
        /// Vulkan through the engine's OWN native backend (<c>KhaozEngine.Gpu.Vulkan</c>) rather than through
        /// Veldrid. THE LINUX DEFAULT since 17.40.0, and the catch-all for an OS the probe does not recognize,
        /// still selectable by name (<c>KE_GRAPHICS_BACKEND=vulkan-native</c>, or the shorter
        /// <c>vk-native</c>) and created by the <see cref="IGpuBackendProvider"/> that package registers, never
        /// by this one, for the same attribution reason <see cref="Direct3D11Native"/> is a separate member: a
        /// session log, a telemetry header and a frame time have to name the implementation that actually ran.
        /// It renders the SAME images as <see cref="Vulkan"/>, and from <c>17.41.0</c> it OWNS the
        /// <c>vulkan-native</c> golden family, promoted out of the incumbent's for the reason and in the way
        /// <see cref="Direct3D11Native"/> was.
        /// <para>
        /// The one place this differs from its Direct3D 11 sibling is what the default flip MEANT. The Windows
        /// flip moved that platform onto <see cref="Direct3D11Native"/>. This one moved LINUX, and with it the
        /// catch-all arm, so a machine the probe does not recognize lands here too. The flip was taken by
        /// decision on 2026-08-22 ahead of two of decision V-RO3's rollout gates, which stay open as issues and
        /// are named in the dated addendum to section 17 of
        /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>.
        /// </para>
        /// </summary>
        VulkanNative = 5,

        /// <summary>
        /// Apple Metal through the engine's OWN native backend (<c>KhaozEngine.Gpu.Metal</c>) rather than through
        /// Veldrid. Selected by name (<c>KE_GRAPHICS_BACKEND=metal-native</c>, or the shorter <c>mtl-native</c>)
        /// and created by the <see cref="IGpuBackendProvider"/> that package registers, never by this one, for the
        /// same attribution reason its two siblings are separate members: a session log, a telemetry header and a
        /// frame time have to name the implementation that actually ran. It renders the SAME images as
        /// <see cref="Metal"/>, and from <c>17.41.0</c> it OWNS the <c>metal-native</c> golden family, promoted
        /// out of the incumbent's for the reason and in the way <see cref="Direct3D11Native"/> was.
        /// <para>
        /// Two things about this member differ from both siblings, and both are consequences of WHICH backend it
        /// is a second implementation of. The <c>metal-native</c> family it owns is a byte-identical copy
        /// of <c>metal</c>, the FLEET's cross-backend reference that every other leg's references are read
        /// against, so it INHERITS that standing rather than competing with it and a disagreement here is a fleet
        /// event rather than a leg event (decision M-I3 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, whose guest ruling row 2 of the Veldrid
        /// removal superseded while keeping its bytes).
        /// </para>
        /// <para>
        /// And the flip changed the macOS default, which is not a player population but the fleet's
        /// DEVELOPMENT platform: every windowed playtest, every capture, every editor session and every local
        /// golden bake on a Mac has run on this backend since 17.40.0. A local bake was the one visible cost,
        /// and it lasted one release: a guest of a family may not overwrite it, so baking on a Mac meant naming
        /// <c>KE_GRAPHICS_BACKEND=metal</c> until <c>17.41.0</c> gave this member the <c>metal-native</c> family
        /// of its own. The flip was taken by decision on 2026-08-22 ahead of the rollout
        /// gate that is still open, which is named in the dated addendum to section 17 of that document.
        /// </para>
        /// </summary>
        MetalNative = 6,
    }

    /// <summary>
    /// Predicates over <see cref="GpuBackendKind"/> that more than one site needs, one per API that has two
    /// implementations. They live here rather than being spelled out at each site so the answer cannot drift:
    /// <see cref="IsDirect3D11"/> in particular is read by the driver-threading probe and by the log line that
    /// reports what the probe found, and a copy that disagreed would produce a session log claiming an answer
    /// nobody asked for.
    /// </summary>
    public static class GpuBackendKinds
    {
        /// <summary>
        /// Whether <paramref name="kind"/> is Direct3D 11 through EITHER implementation. This is the right question
        /// for anything that talks to the D3D11 API or reports on the D3D11 driver (the
        /// <c>D3D11_FEATURE_DATA_THREADING</c> probe and its log line), because the driver underneath is the same
        /// one whichever implementation drove it. It is the WRONG question for anything that creates a device,
        /// since <see cref="GpuBackendKind.Direct3D11"/> is retired and no longer maps onto an implementation.
        /// </summary>
        public static bool IsDirect3D11(this GpuBackendKind kind)
            => kind is GpuBackendKind.Direct3D11 or GpuBackendKind.Direct3D11Native;

        /// <summary>
        /// Whether <paramref name="kind"/> is Vulkan through EITHER implementation. The sibling of
        /// <see cref="IsDirect3D11"/> and it exists for the same reason (decision V-I5 of
        /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>): the question gets asked at more than
        /// one site, and a copy of it spelled out at each site drifts.
        /// <para>
        /// It is the right question for anything that talks to the Vulkan API or reports on the Vulkan driver,
        /// because the driver and the ICD underneath are the same ones whichever implementation drove them. It is
        /// the WRONG question for anything that creates a device, since <see cref="GpuBackendKind.Vulkan"/> is
        /// retired and no longer maps onto an implementation. Nothing in the engine gates on it today, unlike
        /// <see cref="IsDirect3D11"/>, whose two readers are the driver-threading probe and the log line that
        /// reports what the probe found: Vulkan has no <c>D3D11_FEATURE_DATA_THREADING</c> analogue to ask about,
        /// so those two sites correctly exclude both Vulkan implementations.
        /// </para>
        /// </summary>
        public static bool IsVulkan(this GpuBackendKind kind)
            => kind is GpuBackendKind.Vulkan or GpuBackendKind.VulkanNative;

        /// <summary>
        /// Whether <paramref name="kind"/> is Apple Metal through EITHER implementation. The third of these
        /// predicates (decision M-I5 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>).
        /// <para>
        /// It is the right question for anything that talks to the Metal API or reasons about how a Metal frame
        /// reaches the display, because the drawable and the display underneath are the same ones whichever
        /// implementation drove them. It is the WRONG question for anything that creates a device, since
        /// <see cref="GpuBackendKind.Metal"/> is retired and no longer maps onto an implementation.
        /// </para>
        /// <para>
        /// It has NO reader in the engine today, and the reason is worth recording rather than looking like an
        /// oversight. It was written for the software frame-cap pair in <c>KhaozEngine.Windowing</c>,
        /// <c>FrameCap.Resolve</c> and <c>DisplaySettings.RequiresFrameCapWarning</c>, which took the family arm
        /// as a CONSERVATIVE DEFAULT while which arm <see cref="GpuBackendKind.MetalNative"/> belongs in was an
        /// open measurement (decision M-W3). Rollout gate 5 took that measurement on 2026-08-11: the native
        /// present throttles the CPU from vsync alone, so both sites went back to an equality against
        /// <see cref="GpuBackendKind.Metal"/> and the software cap was the incumbent's alone. That incumbent was
        /// deleted in 18.0.0, so no live backend takes the cap. The predicate stays because the QUESTION it asks
        /// is still the right one for the next site that reasons about the Metal API rather than about one
        /// particular implementation of it.
        /// </para>
        /// </summary>
        public static bool IsMetal(this GpuBackendKind kind)
            => kind is GpuBackendKind.Metal or GpuBackendKind.MetalNative;
    }

    /// <summary>
    /// OS family used by <see cref="GpuBackendSelector"/>'s default probe. A tiny engine enum (rather than
    /// touching <c>RuntimeInformation</c> directly) so the selection logic is headless-testable: a test can
    /// drive <see cref="GpuBackendSelector.Select(string?, OSPlatformKind)"/> with any OS without mocking the
    /// real environment.
    /// </summary>
    public enum OSPlatformKind
    {
        /// <summary>An OS the probe does not specially recognize (falls back to the default backend).</summary>
        Unknown,
        /// <summary>macOS / OSX.</summary>
        MacOS,
        /// <summary>Windows.</summary>
        Windows,
        /// <summary>Linux.</summary>
        Linux,
    }
}
