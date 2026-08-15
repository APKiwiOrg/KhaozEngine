using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE <see cref="Log.Configure(LoggerOptions)"/> CALL IN THIS ASSEMBLY, shared by every GPU backend that
    /// has a validation rung to report through it. #565 built this for Vulkan and
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/617 is why it is no longer Vulkan's alone.
    ///
    /// <para><b>WHY ONE HOST RATHER THAN ONE PER BACKEND.</b> <see cref="Log.Configure(LoggerOptions)"/> SHUTS
    /// DOWN the manager it replaces, and shutdown disposes and clears that manager's sink list. A second
    /// configure anywhere in this process therefore throws away the first host's sink, and the artifact ends up
    /// holding whatever the LAST configure admitted rather than the union of the armed rungs. Two module
    /// initializers would be exactly that second configure, on a leg where both rungs happened to be armed. So
    /// the call lives here, once, and each backend contributes only a DECISION (is my rung armed) and a SCOPE
    /// (my category prefix).</para>
    ///
    /// <para>A second failure used to ride on top of that one and no longer does. An <c>ILogger</c> handed out by
    /// the facade used to keep pointing at the manager it came from, so a reconfigure ORPHANED every producer
    /// that had already resolved one, and both native backends resolve theirs once into <c>static readonly</c>
    /// fields. 17.36.2 moved that binding to the facade (#616): a logger from <c>Log.For</c> now finds the
    /// configured manager per call, so a producer resolved before a reconfigure follows it. The one-host rule
    /// above is untouched by that, because it is about which SINK survives, not about which producers reach
    /// it.</para>
    ///
    /// <para><b>WHAT #617 MEASURED, AND WHY THE METAL HALF EXISTS.</b> The metal-native leg's first ever run under
    /// <c>MTL_SHADER_VALIDATION=1</c> failed 186 of 6201 rows, every golden reading back as nothing but the pass
    /// clear colour, and the uploaded <c>metal-validation-shader-validation-metal-native</c> artifact contained no
    /// Metal diagnostic of any kind. It could not have contained one. The native Metal backend reports the armed
    /// tier, the device class, and EVERY failed command buffer through the ambient <see cref="Log"/> facade
    /// (<c>MetalGpuDevice.ReportDeviceClass</c> and <c>MetalDeviceLossLatch.Check</c>), and that facade discards
    /// everything until something calls <see cref="Log.Configure(LoggerOptions)"/>. Nothing in this
    /// assembly did on a Metal leg, so the one question the run existed to answer (did the command buffers fail,
    /// or did they succeed and draw nothing) was unanswerable from its own evidence.</para>
    ///
    /// <para><b>ARMED RUNS ONLY.</b> A run with no rung armed on any backend configures nothing, allocates
    /// nothing, and leaves the no-op facade every ordinary <c>dotnet test</c> has always seen.</para>
    /// </summary>
    internal static class GpuValidationConsoleLogging
    {
        /// <summary>The category the facade's own convenience methods route to. Deliberately matching NO backend
        /// prefix, so a stray <c>Log.Warn</c> anywhere in the process cannot leak into a backend's artifact under
        /// that backend's name.</summary>
        internal const string FacadeCategory = "RenderTestsHost";

        /// <summary>
        /// THE MODULE INITIALIZER, and the only one in this assembly that touches the facade. It runs before any
        /// test does, which is what puts the configuration in place ahead of the <c>static readonly</c> logger
        /// fields both native backends resolve on first touch.
        /// <para>
        /// CA2255 is fine here for the reason the backend-registration initializers beside this file carry: a test
        /// project is application code, and the load guarantee a library cannot make is one a test assembly makes
        /// by definition.
        /// </para>
        /// </summary>
        [ModuleInitializer]
        internal static void Initialize()
        {
            string? vulkanLever = Environment.GetEnvironmentVariable(
                KhaozEngine.Gpu.Vulkan.Internal.VulkanValidation.EnvVarName);
            bool vulkanArmed = VulkanValidationConsoleLogging.IsArmed(vulkanLever);
            KhaozEngine.Gpu.Metal.Internal.MetalValidationMode metalArmed =
                MetalValidationConsoleLogging.ArmedTier();
            bool metalIsArmed = MetalValidationConsoleLogging.IsArmed(metalArmed);

            // Both levers are read before anything is constructed, so an unarmed process leaves this file having
            // done nothing but a handful of environment reads.
            if (!vulkanArmed && !metalIsArmed) return;

            var prefixes = new List<string>(2);
            if (vulkanArmed) prefixes.Add(VulkanValidationConsoleLogging.CategoryPrefix);
            if (metalIsArmed) prefixes.Add(MetalValidationConsoleLogging.CategoryPrefix);

            // useStdErrForErrors: false, deliberately. The artifact's whole value is the INTERLEAVING with test
            // names, and two streams merged by a shell pipe do not preserve the order they were written in, so an
            // error-severity line split onto stderr can land next to the wrong test. That matters more on Metal
            // than it did on Vulkan: a command-buffer failure is an ERROR line, and which test provoked it is the
            // entire diagnostic.
            Log.Configure(BuildOptions(new ConsoleSink(useStdErrForErrors: false), prefixes));

            if (vulkanArmed)
            {
                Log.Get(VulkanValidationConsoleLogging.HostCategory)
                    .Info(VulkanValidationConsoleLogging.ArmedAnnouncement(vulkanLever));
            }

            if (metalIsArmed)
            {
                Log.Get(MetalValidationConsoleLogging.HostCategory)
                    .Info(MetalValidationConsoleLogging.ArmedAnnouncement(metalArmed));
            }
        }

        /// <summary>
        /// The configuration an armed run gets, for whichever backends armed a rung. Pure: it builds and returns,
        /// and never touches the ambient facade, so a test can drive the whole seam through a
        /// <see cref="LogManager"/> of its own.
        /// </summary>
        /// <param name="destination">Where the surviving lines go. The console on CI, an
        /// <see cref="InMemorySink"/> under test.</param>
        /// <param name="categoryPrefixes">The armed backends' category prefixes. One wrapper admits all of them,
        /// rather than one wrapper each, because two wrappers over one destination would dispose it twice.</param>
        internal static LoggerOptions BuildOptions(ILogSink destination, IReadOnlyList<string> categoryPrefixes)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (categoryPrefixes is null) throw new ArgumentNullException(nameof(categoryPrefixes));

            var options = new LoggerOptions
            {
                // Synchronous, so a line is on the stream before the call that logged it returns. The async
                // writer is a background thread, and a run that dies inside the driver would lose whatever was
                // still queued, which is exactly the last message before a crash and the one worth having.
                Synchronous = true,
                // Info, not Warn. Both backends announce the live rung at Info, and that line is what proves the
                // lever was set on the run being read.
                MinimumLevel = LogLevel.Info,
                DefaultCategory = FacadeCategory,
            };
            options.Sinks.Add(new CategoryPrefixSink(destination, categoryPrefixes));
            return options;
        }
    }

    /// <summary>
    /// A sink decorator that passes on only the entries whose category starts with one of a set of prefixes.
    /// <para>
    /// DELIBERATELY TEST-LOCAL AND NOT ENGINE API. The engine's sinks filter on level and nothing else, and a
    /// category filter is a reasonable thing for <c>KhaozEngine.Diagnostics</c> to grow one day. It is not
    /// growing it here: #565 is a CI-evidence fix, and shipping a new public type in a package to serve one test
    /// host is how a fix turns into a surface that has to be documented, versioned and kept.
    /// </para>
    /// </summary>
    internal sealed class CategoryPrefixSink : ILogSink
    {
        readonly string[] _prefixes;
        readonly ILogSink _inner;

        internal CategoryPrefixSink(ILogSink inner, IReadOnlyList<string> prefixes)
        {
            if (prefixes is null) throw new ArgumentNullException(nameof(prefixes));

            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            var copy = new string[prefixes.Count];
            for (int i = 0; i < prefixes.Count; i++)
            {
                copy[i] = prefixes[i] ?? throw new ArgumentNullException(nameof(prefixes));
            }

            _prefixes = copy;
        }

        /// <inheritdoc />
        public void Emit(in LogEntry entry)
        {
            for (int i = 0; i < _prefixes.Length; i++)
            {
                if (entry.Category.StartsWith(_prefixes[i], StringComparison.Ordinal))
                {
                    _inner.Emit(entry);
                    return;
                }
            }
        }

        /// <inheritdoc />
        public void Flush() => _inner.Flush();

        /// <inheritdoc />
        public void Dispose() => _inner.Dispose();
    }
}
