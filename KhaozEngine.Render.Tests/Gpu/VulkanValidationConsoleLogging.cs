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
    /// <see cref="Log"/> facade, and that facade hands out <c>NullLogger</c> until something calls
    /// <see cref="Log.Configure(LoggerOptions)"/>. Nothing in this assembly ever did, so every
    /// <c>_log.Warn</c> and <c>_log.Error</c> in the pump was a no-op on every leg. The <c>strict</c> tier was
    /// unaffected, because its latch and its throw are counters rather than log calls, but WARNING-severity
    /// messages never fail anything by design, which makes the artifact the only place they are ever read. The
    /// <c>gpu-vulkan-sync</c> tier exists to surface exactly those, and it was surfacing them into a no-op.
    /// </para>
    /// <para>
    /// ARMED LEGS ONLY, so an ordinary run stays byte-quiet. The lever is the backend's own
    /// <c>KE_VULKAN_VALIDATION</c>, read through the backend's own parser, so "armed" here means precisely what
    /// it means to the code that creates the messenger: a typo that <see cref="VulkanValidation.Parse"/> refuses
    /// leaves validation off AND leaves this sink unconfigured, rather than producing a log that implies an
    /// instrument that is not running. Every other leg (and every developer's <c>dotnet test</c>) sees the
    /// unconfigured facade the rest of the suite has always seen.
    /// </para>
    /// <para>
    /// SCOPED TO THE VULKAN BACKEND'S CATEGORIES rather than to the whole engine, through
    /// <see cref="CategoryPrefixSink"/>. The facade has no category filtering of its own, so an unfiltered
    /// console sink would put every engine log line from every subsystem into a log whose entire value is that a
    /// validation message reads next to the test name that provoked it. The prefix is <c>Vulkan</c>, which is
    /// wider than the pump alone on purpose: <c>VulkanInstance</c> logs whether the layer was actually found and
    /// which rung is live, and a validation log that cannot show the instrument was running is not evidence of
    /// anything.
    /// </para>
    /// <para>
    /// A <c>[ModuleInitializer]</c>, following the three backend-registration initializers beside this file. It
    /// is the mechanism this assembly already uses for per-process setup that must happen before any test runs,
    /// and CA2255 is fine here for the reason stated on those: a test project is application code, and the load
    /// guarantee a library cannot make is one a test assembly makes by definition. Ordering matters too. The
    /// pump resolves its fallback logger when it is constructed, and a module initializer runs before any test
    /// can reach the code that constructs one.
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
        /// <c>VulkanValidationPump</c>, <c>VulkanInstance</c>, <c>VulkanGpuDevice</c> and their siblings.</summary>
        internal const string CategoryPrefix = "Vulkan";

        /// <summary>The category the facade's own convenience methods would use. Named for this seam so a line
        /// that somehow arrives through <c>Log.Warn</c> rather than a category logger is attributable, and
        /// filtered out by the prefix above rather than landing in the artifact unexplained. Deliberately NOT
        /// prefixed <c>Vulkan</c>: a Vulkan-prefixed default would let any stray facade call anywhere in the
        /// process leak into the artifact.</summary>
        internal const string FacadeCategory = "RenderTestsHost";

        /// <summary>The category this seam announces itself under. Prefixed, so the one line it writes survives
        /// its own filter.</summary>
        internal const string HostCategory = "VulkanValidationLogHost";

        /// <summary>Runs before any test in this assembly. A module initializer must return void, so the answer
        /// lives on <see cref="ConfigureFromEnvironment"/> next door.</summary>
        [ModuleInitializer]
        internal static void Initialize()
        {
            string? envValue = Environment.GetEnvironmentVariable(VulkanValidation.EnvVarName);
            if (ConfigureFromEnvironment()) Log.Get(HostCategory).Info(ArmedAnnouncement(envValue));
        }

        /// <summary>
        /// The host's own configuration, read from the live lever. Writes to stdout, which is what the CI step
        /// tees into the artifact. Returns whether a rung was armed, which is what lets a test that swapped the
        /// ambient manager put the host back the way it found it.
        /// </summary>
        internal static bool ConfigureFromEnvironment()
            => TryConfigure(
                Environment.GetEnvironmentVariable(VulkanValidation.EnvVarName),
                // useStdErrForErrors: false, deliberately. The artifact's whole value is the INTERLEAVING with
                // test names, and two streams merged by a shell pipe do not preserve the order they were written
                // in, so an error-severity line split onto stderr can land next to the wrong test.
                new ConsoleSink(useStdErrForErrors: false));

        /// <summary>
        /// Configures the ambient facade to write the Vulkan backend's lines to <paramref name="destination"/>,
        /// when and only when <paramref name="envValue"/> arms a validation rung. Returns false and touches
        /// nothing at all otherwise.
        /// </summary>
        /// <param name="envValue">The raw <c>KE_VULKAN_VALIDATION</c> value.</param>
        /// <param name="destination">Where the surviving lines go. The console on CI, an
        /// <see cref="InMemorySink"/> under test.</param>
        internal static bool TryConfigure(string? envValue, ILogSink destination)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (VulkanValidation.Parse(envValue, out _) == VulkanValidationMode.Off) return false;

            var options = new LoggerOptions
            {
                // Synchronous, so a line is on the stream before the call that logged it returns. The async
                // writer is a background thread, and a run that dies inside the driver would lose whatever was
                // still queued, which is exactly the last message before a crash and the one worth having.
                Synchronous = true,
                // Info, not Warn. The pump's own messages are Warn and Error, but VulkanInstance announces the
                // live rung at Info and that line is what proves the lever was set on the run being read.
                MinimumLevel = LogLevel.Info,
                DefaultCategory = FacadeCategory,
            };
            options.Sinks.Add(new CategoryPrefixSink(CategoryPrefix, destination));
            Log.Configure(options);
            return true;
        }

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

    /// <summary>
    /// A sink decorator that passes on only the entries whose category starts with a prefix.
    /// <para>
    /// DELIBERATELY TEST-LOCAL AND NOT ENGINE API. The engine's sinks filter on level and nothing else, and a
    /// category filter is a reasonable thing for <c>KhaozEngine.Diagnostics</c> to grow one day. It is not
    /// growing it here: #565 is a CI-evidence fix, and shipping a new public type in a package to serve one test
    /// host is how a fix turns into a surface that has to be documented, versioned and kept.
    /// </para>
    /// </summary>
    internal sealed class CategoryPrefixSink : ILogSink
    {
        readonly string _prefix;
        readonly ILogSink _inner;

        internal CategoryPrefixSink(string prefix, ILogSink inner)
        {
            _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public void Emit(in LogEntry entry)
        {
            if (!entry.Category.StartsWith(_prefix, StringComparison.Ordinal)) return;
            _inner.Emit(entry);
        }

        /// <inheritdoc />
        public void Flush() => _inner.Flush();

        /// <inheritdoc />
        public void Dispose() => _inner.Dispose();
    }
}
