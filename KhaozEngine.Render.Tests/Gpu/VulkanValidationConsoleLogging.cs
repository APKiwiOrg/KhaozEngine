using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// #565's FIX: the one thing in this test process that gives the engine's own validation lines somewhere to
    /// go. Both Vulkan validation tiers exist to produce a LOG, and until this ran neither could contain a single
    /// engine-formatted line.
    /// <para>
    /// WHY THERE WAS NOTHING TO READ. <c>VulkanValidationPump</c> logs through the ambient
    /// <see cref="Log"/> facade, and that facade discards everything until something calls
    /// <see cref="Log.Configure(LoggerOptions)"/>. Nothing in this assembly ever did, so every
    /// <c>_log.Warn</c> and <c>_log.Error</c> in the pump was a no-op on every leg. The <c>strict</c> tier was
    /// unaffected, because its latch and its throw are counters rather than log calls, but WARNING-severity
    /// messages never fail anything by design, which makes the artifact the only place they are ever read. The
    /// <c>gpu-vulkan-sync</c> tier exists to surface exactly those, and it was surfacing them into a no-op.
    /// </para>
    /// <para>
    /// CONFIGURED EXACTLY ONCE PER PROCESS, AND NEVER FROM A TEST. This is a hard rule for this assembly and the
    /// reason the type is split the way it is. <see cref="Log.Configure(LoggerOptions)"/>
    /// SHUTS DOWN the manager it replaces, and shutdown disposes and clears that manager's sink list, so a
    /// reconfigure part-way through an armed run throws away the console sink this host installed and the rest
    /// of the run is captured by whatever the replacement admits. A first cut of this seam had its own tests
    /// reconfiguring the facade and restoring it afterwards, which cost the armed <c>sync</c> run most of its
    /// Vulkan lines: every <c>VulkanMemoryAllocator</c> INFO line and every <c>VulkanPresentBoundary</c> WARN and
    /// ERROR line logged after that class ran went nowhere. So this type is the DECISION half only
    /// (<see cref="IsArmed"/>, the scope, and the announcement), it is pure, and it is what the tests exercise
    /// against a <see cref="LogManager"/> they own.
    /// </para>
    /// <para>
    /// PART of that used to be a second, worse failure, and 17.36.2 removed it (#616). A logger handed out by the
    /// facade used to keep pointing at the manager it came from, so every logger resolved before a reconfigure
    /// wrote into a gutted manager for the rest of the process: enabled, submitting, and silent. Several Vulkan
    /// types resolve their logger once into a <c>static readonly</c> field, so one reconfigure orphaned the very
    /// producers the artifact exists to capture. A facade logger now finds the configured manager per call, so
    /// the producers survive a reconfigure. The rule above is NOT relaxed on the back of that: the sink still
    /// does not survive, and one host configuring once is still the only arrangement in which the artifact holds
    /// what the run was armed for.
    /// </para>
    /// <para>
    /// THE APPLY HALF MOVED TO <see cref="GpuValidationConsoleLogging"/>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/617), because Metal needed the same seam and a second
    /// module initializer calling <see cref="Log.Configure(LoggerOptions)"/> would be precisely the orphaning
    /// reconfigure described above, on any leg where both rungs were armed. One host, two decisions.
    /// </para>
    /// <para>
    /// ARMED LEGS ONLY, so an ordinary run stays byte-quiet. The lever is the backend's own
    /// <c>KE_VULKAN_VALIDATION</c>, read through the backend's own parser, so "armed" here means precisely what
    /// it means to the code that creates the messenger: a typo that <see cref="VulkanValidation.Parse"/> refuses
    /// leaves validation off AND leaves this sink unconfigured, rather than producing a log that implies an
    /// instrument that is not running. Every other leg (and every developer's <c>dotnet test</c>) sees the
    /// unconfigured facade the rest of the suite has always seen, and allocates nothing at all here: the lever is
    /// checked before the sink and the options are built.
    /// </para>
    /// <para>
    /// SCOPED TO THE VULKAN BACKEND, WHICH IS WIDER THAN THE PUMP AND IS MEANT TO BE. The prefix is
    /// <c>Vulkan</c>, matched through <see cref="CategoryPrefixSink"/>, and it admits EVERY category the native
    /// backend logs under, at Info and above. On a measured armed <c>sync</c> run that is: the allocator's
    /// per-allocation INFO lines from <c>VulkanMemoryAllocator</c> (the bulk of the log by a wide margin),
    /// <c>VulkanGpuDevice</c> and <c>VulkanBackendProvider</c> warnings, <c>VulkanPresentBoundary</c> warnings and
    /// errors, a <c>VulkanTimeline</c> warning, this host's own banner, and the pump's validation lines when the
    /// layer produces any. That breadth is the point: a validation line is only evidence next to what the backend
    /// was doing when it fired, <c>VulkanInstance</c> is what says the layer was found and which rung is live, and
    /// the allocator and present-boundary lines are what a barrier or lifetime complaint has to be read against.
    /// What the prefix keeps OUT is the rest of the engine, because the facade has no category filtering of its
    /// own and an unfiltered console sink would bury the whole thing under every other subsystem that logs.
    /// </para>
    /// <para>
    /// Applied from a <c>[ModuleInitializer]</c> next door, following the three backend-registration initializers
    /// beside this file. It is the mechanism this assembly already uses for per-process setup that must happen
    /// before any test runs. Ordering matters too. The pump resolves its fallback logger when it is constructed,
    /// and a module initializer runs before any test can reach the code that constructs one.
    /// </para>
    /// <para>
    /// It covers BOTH tiers because both run this assembly. The matrix leg's <c>strict</c> run and the
    /// <c>gpu-vulkan-sync</c> job's <c>sync</c> run are two <c>dotnet test</c> invocations over the same
    /// solution, differing in filter and in the value of the lever, so there is no second entry point to treat
    /// separately.
    /// </para>
    /// </summary>
    internal static class VulkanValidationConsoleLogging
    {
        /// <summary>Every category the native Vulkan backend logs under starts with this, and nothing else in the
        /// engine does. <c>Log.For&lt;T&gt;</c> uses <c>typeof(T).Name</c>, so the categories are the type names:
        /// <c>VulkanValidationPump</c>, <c>VulkanInstance</c>, <c>VulkanMemoryAllocator</c>,
        /// <c>VulkanPresentBoundary</c> and their siblings.</summary>
        internal const string CategoryPrefix = "Vulkan";

        /// <summary>The category the facade's own convenience methods would use. Named for the shared host so a
        /// line that somehow arrives through <c>Log.Warn</c> rather than a category logger is attributable, and
        /// filtered out by the prefix above rather than landing in the artifact unexplained. Deliberately NOT
        /// prefixed <c>Vulkan</c>: a Vulkan-prefixed default would let any stray facade call anywhere in the
        /// process leak into the artifact.</summary>
        internal const string FacadeCategory = GpuValidationConsoleLogging.FacadeCategory;

        /// <summary>The category this seam announces itself under. Prefixed, so the one line it writes survives
        /// its own filter.</summary>
        internal const string HostCategory = "VulkanValidationLogHost";

        /// <summary>
        /// Whether <paramref name="envValue"/> arms a validation rung, which is the whole of this seam's
        /// decision. Pure, and deliberately the backend's own parser rather than a second spelling of it: a value
        /// the backend refuses runs no validation, so a log configured for it would imply an instrument that is
        /// not running.
        /// </summary>
        /// <param name="envValue">The raw <c>KE_VULKAN_VALIDATION</c> value.</param>
        internal static bool IsArmed(string? envValue)
            => VulkanValidation.Parse(envValue, out _) != VulkanValidationMode.Off;

        /// <summary>
        /// The configuration an armed run gets. Pure: it builds and returns, and never touches the ambient
        /// facade, so a test can drive the whole seam through a <see cref="LogManager"/> of its own.
        /// </summary>
        /// <param name="destination">Where the surviving lines go. The console on CI, an
        /// <see cref="InMemorySink"/> under test.</param>
        internal static LoggerOptions BuildOptions(ILogSink destination)
            => GpuValidationConsoleLogging.BuildOptions(destination, new[] { CategoryPrefix });

        /// <summary>
        /// The one line this seam writes on its own account, at the top of an armed run's log.
        /// <para>
        /// A LOG WITH NO VALIDATION LINES IN IT IS AMBIGUOUS, and this is the line that resolves it. Zero
        /// validation messages is what a genuinely clean sweep looks like AND what a log looks like when its
        /// producer is gone, which is the property the <c>gpu-vulkan-sync</c> gate step already prints a count
        /// for. This says the sink existed on the run being read, so the two are tellable apart from the artifact
        /// alone rather than from a reader's memory of which release wired it up.
        /// </para>
        /// </summary>
        internal static string ArmedAnnouncement(string? envValue)
            => $"{VulkanValidation.EnvVarName}='{envValue}' armed a validation rung, so this test host configured "
                + $"a console sink for log categories starting with '{CategoryPrefix}' at "
                + $"{LogLevel.Info} and above. Engine-formatted validation lines "
                + "('Vulkan validation [<Severity>] <VUID>: <text>') therefore reach this log. The Khronos "
                + "layer's own output is independent of this and arrives whether or not it is configured. An "
                + "unarmed run configures nothing and this line is absent.";
    }
}
