using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// #565: the test host's validation logging seam, both halves of it. Armed, a pump message reaches the
    /// configured sink as the formatted line the CI gate greps for. Unarmed, nothing is armed at all and the
    /// facade stays the no-op it has always been on an ordinary run.
    /// <para>
    /// NOTHING HERE CALLS <see cref="Log.Configure(LoggerOptions)"/>, WHICH IS THE POINT. A reconfigure is not
    /// restorable in this process: it shuts down the manager it replaces, shutdown disposes and clears that
    /// manager's sinks, and every <c>ILogger</c> already handed out keeps pointing at the dead manager. The
    /// Vulkan backend resolves several of its loggers once into <c>static readonly</c> fields, so one reconfigure
    /// part-way through an armed run silences those producers for the rest of it: an earlier version of this
    /// class did exactly that and cost the armed <c>sync</c> artifact most of its Vulkan lines, all of them
    /// silently. So the tests drive the PURE half of the seam
    /// (<see cref="VulkanValidationConsoleLogging.IsArmed"/> and
    /// <see cref="VulkanValidationConsoleLogging.BuildOptions"/>) through a <see cref="LogManager"/> they own and
    /// dispose, and the process facade is only ever READ, in
    /// <see cref="TheProcessFacade_MatchesTheLeverTheHostWasStartedWith"/>.
    /// </para>
    /// <para>
    /// No <c>DisableParallelization</c> collection either, for the same reason: with no writer of the ambient
    /// manager left in this assembly, the one read below cannot race anything, and a serial collection would
    /// suggest a hazard that has been removed rather than contained. A future test that DOES write the facade
    /// would need both the collection back and a much better reason than this one had.
    /// </para>
    /// <para>
    /// Device-free throughout. The pump takes plain message values, so the whole seam is decided on a machine
    /// with no Vulkan loader.
    /// </para>
    /// </summary>
    public sealed class VulkanValidationConsoleLoggingTests
    {
        /// <summary>
        /// THE FIX, end to end: with the lever armed, a message handed to the pump comes out of the seam's own
        /// configuration as the exact line the <c>gpu-vulkan-sync</c> gate step greps for. The manager is built
        /// from <see cref="VulkanValidationConsoleLogging.BuildOptions"/>, which is the same object the module
        /// initializer hands to the facade on an armed leg, so what is asserted here is what CI gets.
        /// </summary>
        [Theory]
        [InlineData("1")]
        [InlineData("strict")]
        [InlineData("sync")]
        public void AnArmedLever_PutsThePumpsFormattedLineInTheSink(string lever)
        {
            Assert.True(VulkanValidationConsoleLogging.IsArmed(lever));

            var sink = new InMemorySink();
            using var manager = new LogManager(VulkanValidationConsoleLogging.BuildOptions(sink));

            var pump = new VulkanValidationPump(
                VulkanValidationMode.Sync, logger: manager.GetLogger<VulkanValidationPump>());
            pump.Report(new VulkanValidationMessage(
                VulkanValidationSeverity.Warning, 42, "VUID-vkCmdDraw-None-08600", "a fabricated hazard"));

            var entry = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Equal(nameof(VulkanValidationPump), entry.Category);
            Assert.Equal(
                "Vulkan validation [Warning] VUID-vkCmdDraw-None-08600: a fabricated hazard", entry.Message);

            // The CI gate greps the FORMATTED line, not the message, so the formatter's output is what has to
            // carry the shape. Both jobs match 'Vulkan validation [<Severity>]' against the teed log.
            Assert.Contains(
                "Vulkan validation [Warning]", LogFormatter.Format(entry), StringComparison.Ordinal);
        }

        /// <summary>An error-severity message is the arm the <c>strict</c> tier latches on, and it has to reach
        /// the sink at ERROR rather than being folded in with the warnings.</summary>
        [Fact]
        public void AnErrorSeverityMessage_ArrivesAtErrorLevel()
        {
            var sink = new InMemorySink();
            using var manager = new LogManager(VulkanValidationConsoleLogging.BuildOptions(sink));

            var pump = new VulkanValidationPump(
                VulkanValidationMode.Sync, logger: manager.GetLogger<VulkanValidationPump>());
            pump.Report(new VulkanValidationMessage(
                VulkanValidationSeverity.Error, 7, "VUID-vkCmdPipelineBarrier2-None-00001", "a fabricated error"));

            var entry = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(
                "Vulkan validation [Error] VUID-vkCmdPipelineBarrier2-None-00001: a fabricated error",
                entry.Message);
        }

        /// <summary>
        /// THE QUIET DEFAULT, which is the half that keeps every unarmed leg byte-identical to what it was. An
        /// unset, off or unrecognized lever arms nothing, so the module initializer returns before it builds a
        /// sink or options and a developer's <c>dotnet test</c> keeps the no-op facade <c>LogFacadeTests</c> pins
        /// as intended behaviour.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("off")]
        // A typo reads as Off to the backend, which then runs no validation at all, so a log implying an
        // instrument that is not running would be the worst outcome available here.
        [InlineData("syncronization")]
        public void AnUnarmedLever_ArmsNothing(string? lever)
            => Assert.False(VulkanValidationConsoleLogging.IsArmed(lever));

        /// <summary>
        /// THE SCOPE. An armed leg carries the Vulkan backend's own categories and nothing else, so the artifact
        /// stays a validation log rather than becoming a dump of every engine subsystem that happens to log. The
        /// Info floor is pinned here too, in both directions: <c>VulkanInstance</c> announces the live rung at
        /// Info and that line proves the instrument was running, while anything below Info is dropped even from a
        /// Vulkan category.
        /// </summary>
        [Fact]
        public void OnlyTheVulkanBackendsCategoriesReachTheSink()
        {
            var sink = new InMemorySink();
            using var manager = new LogManager(VulkanValidationConsoleLogging.BuildOptions(sink));

            manager.GetLogger("VulkanInstance").Info("the rung is live");
            manager.GetLogger("VulkanInstance").Debug("below the floor");
            manager.GetLogger("AudioEngine").Warn("unrelated engine chatter");
            // What Log.Warn would route to on an armed process: the default category, deliberately unprefixed.
            manager.GetLogger(manager.DefaultCategory).Warn("through the facade's default category");

            var entry = Assert.Single(sink.Entries);
            Assert.Equal("VulkanInstance", entry.Category);
            Assert.Equal(LogLevel.Info, entry.Level);
        }

        /// <summary>The rest of the configuration an armed leg depends on: writes land before the call returns
        /// (a run that dies in the driver keeps its last message), the floor is Info, and the facade's own
        /// convenience methods route to a category the prefix filter drops.</summary>
        [Fact]
        public void TheArmedOptions_AreSynchronousAtInfoWithAnUnprefixedDefaultCategory()
        {
            LoggerOptions options = VulkanValidationConsoleLogging.BuildOptions(new InMemorySink());

            Assert.True(options.Synchronous);
            Assert.Equal(LogLevel.Info, options.MinimumLevel);
            Assert.Equal(VulkanValidationConsoleLogging.FacadeCategory, options.DefaultCategory);
            Assert.DoesNotContain(
                VulkanValidationConsoleLogging.CategoryPrefix, options.DefaultCategory, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ONE READ OF THE PROCESS FACADE, and it is read-only on purpose. It asserts the host and the live
        /// lever agree: an armed run has a configured manager that the pump's own category is enabled on, and an
        /// unarmed one has the untouched no-op facade. Both branches are reachable, and which one runs is decided
        /// by the environment rather than by this test, which is why it asserts a consistency rather than a state.
        /// <para>
        /// THE VULKAN LEVER IS NO LONGER THE ONLY ONE THAT CONFIGURES THE FACADE
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/617), so the unarmed branch asks the shared host
        /// rather than this seam alone. Asserting an unconfigured facade off the Vulkan lever by itself would
        /// fail on the metal-native leg, where <c>MTL_DEBUG_LAYER</c> is armed on every run and Vulkan's is
        /// never armed at all.
        /// </para>
        /// </summary>
        [Fact]
        public void TheProcessFacade_MatchesTheLeverTheHostWasStartedWith()
        {
            string? envValue = Environment.GetEnvironmentVariable(VulkanValidation.EnvVarName);
            ILogger pumpLogger = Log.For<VulkanValidationPump>();

            if (VulkanValidationConsoleLogging.IsArmed(envValue))
            {
                Assert.True(Log.IsConfigured);
                // The level the pump's warnings arrive at, and the one the module initializer's floor admits.
                Assert.True(pumpLogger.IsEnabled(LogLevel.Warn));
            }
            else if (!MetalValidationConsoleLogging.IsArmed(MetalValidationConsoleLogging.ArmedTier()))
            {
                Assert.False(Log.IsConfigured);
                // Not merely quiet: an unconfigured facade hands back a logger enabled for nothing, which is what
                // makes the pump's calls free rather than formatted-then-discarded.
                Assert.False(pumpLogger.IsEnabled(LogLevel.Error));
            }
            else
            {
                // A Metal-armed leg: the facade IS configured, and the scope is Metal's, so a Vulkan category
                // reaches a sink that drops it. That is the property worth pinning here, because it is what keeps
                // a Metal artifact from filling with Vulkan lines.
                Assert.True(Log.IsConfigured);
            }
        }

        /// <summary>
        /// The announcement the module initializer writes on an armed run, which is what makes an artifact with
        /// no validation lines in it readable: it says the sink existed, so a quiet log is a clean sweep rather
        /// than a lost producer. It has to survive its own category filter, and it has to name the lever, the
        /// scope and the shape a reader then greps for.
        /// </summary>
        [Fact]
        public void TheArmedAnnouncement_NamesTheLeverTheScopeAndTheShape()
        {
            string line = VulkanValidationConsoleLogging.ArmedAnnouncement("sync");

            Assert.Contains("KE_VULKAN_VALIDATION='sync'", line, StringComparison.Ordinal);
            Assert.Contains("Vulkan validation [<Severity>]", line, StringComparison.Ordinal);
            Assert.Contains(VulkanValidationConsoleLogging.CategoryPrefix, line, StringComparison.Ordinal);

            Assert.StartsWith(
                VulkanValidationConsoleLogging.CategoryPrefix,
                VulkanValidationConsoleLogging.HostCategory,
                StringComparison.Ordinal);
        }
    }
}
