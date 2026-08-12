using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// #565: the test host's validation logging seam, both halves of it. Armed, a pump message reaches the
    /// configured sink as the formatted line the CI gate greps for. Unarmed, nothing is configured at all and the
    /// facade stays the no-op it has always been on an ordinary run.
    /// <para>
    /// SERIAL, because <see cref="Log.Configure(LoggerOptions)"/> writes process-global state and xUnit runs
    /// collections in parallel. Every test that swaps it restores the host afterwards through
    /// <see cref="RestoreHostState"/>, which REBUILDS the configuration rather than putting the old manager back,
    /// for the reason written there.
    /// </para>
    /// <para>
    /// Device-free throughout. The pump takes plain message values, so the whole seam is decided on a machine
    /// with no Vulkan loader.
    /// </para>
    /// </summary>
    [Collection("LoggingSerial")]
    public sealed class VulkanValidationConsoleLoggingTests
    {
        /// <summary>
        /// THE FIX, end to end: with the lever armed, a message handed to the pump comes out of the configured
        /// sink as the exact line the <c>gpu-vulkan-sync</c> gate step greps for.
        /// <para>
        /// The pump is constructed with no logger, which is how the real one is built
        /// (<c>VulkanDebugMessenger.TryCreate</c> passes <c>logger: null</c>), so this exercises the ambient
        /// facade path rather than a hand-injected logger. It also pins the ordering half of the fix: by the time
        /// this runs the pump type has long been initialized by its sibling tests, and the line still arrives,
        /// which a logger captured once into a static field could not do.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("1")]
        [InlineData("strict")]
        [InlineData("sync")]
        public void AnArmedLever_PutsThePumpsFormattedLineInTheSink(string lever)
        {
            try
            {
                var sink = new InMemorySink();
                Assert.True(VulkanValidationConsoleLogging.TryConfigure(lever, sink));

                var pump = new VulkanValidationPump(VulkanValidationMode.Sync);
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
            finally
            {
                RestoreHostState();
            }
        }

        /// <summary>An error-severity message is the arm the <c>strict</c> tier latches on, and it has to reach
        /// the sink at ERROR rather than being folded in with the warnings.</summary>
        [Fact]
        public void AnErrorSeverityMessage_ArrivesAtErrorLevel()
        {
            try
            {
                var sink = new InMemorySink();
                Assert.True(VulkanValidationConsoleLogging.TryConfigure("sync", sink));

                var pump = new VulkanValidationPump(VulkanValidationMode.Sync);
                pump.Report(new VulkanValidationMessage(
                    VulkanValidationSeverity.Error, 7, "VUID-vkCmdPipelineBarrier2-None-00001",
                    "a fabricated error"));

                var entry = Assert.Single(sink.Entries);
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Equal(
                    "Vulkan validation [Error] VUID-vkCmdPipelineBarrier2-None-00001: a fabricated error",
                    entry.Message);
            }
            finally
            {
                RestoreHostState();
            }
        }

        /// <summary>
        /// THE QUIET DEFAULT, which is the half that keeps every unarmed leg byte-identical to what it was. An
        /// unset, off or unrecognized lever configures nothing and does not touch the ambient manager, so a
        /// developer's <c>dotnet test</c> and every non-validation CI leg keep the no-op facade
        /// <c>LogFacadeTests</c> pins as intended behaviour.
        /// <para>
        /// The assertion is that the ambient manager is the SAME reference afterwards, rather than that it is
        /// null. On an armed leg the module initializer has already configured one, and "did not touch it" is the
        /// property that holds on both. Nothing is restored here because nothing was changed.
        /// </para>
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
        public void AnUnarmedLever_ConfiguresNothing(string? lever)
        {
            var prior = Log.Manager;
            var sink = new InMemorySink();

            Assert.False(VulkanValidationConsoleLogging.TryConfigure(lever, sink));

            Assert.Same(prior, Log.Manager);
            Assert.Empty(sink.Entries);
        }

        /// <summary>With nothing configured the facade hands back a logger that is enabled for nothing, which is
        /// what makes the pump's calls free rather than merely quiet. The pump must survive it without throwing,
        /// because the callback it runs under is a native driver frame, and it must still COUNT the error, since
        /// the <c>strict</c> latch is what a count feeds and that half was never broken.</summary>
        [Fact]
        public void WithNothingConfigured_ThePumpRoutesToADisabledLogger()
        {
            try
            {
                Log.Shutdown();

                Assert.False(Log.IsConfigured);
                Assert.False(Log.For<VulkanValidationPump>().IsEnabled(LogLevel.Error));

                var pump = new VulkanValidationPump(VulkanValidationMode.Sync);
                var ex = Record.Exception(() => pump.Report(new VulkanValidationMessage(
                    VulkanValidationSeverity.Error, 1, "VUID-nothing", "into the void")));

                Assert.Null(ex);
                Assert.Equal(1, pump.ErrorCount);
            }
            finally
            {
                RestoreHostState();
            }
        }

        /// <summary>
        /// THE SCOPE. An armed leg carries the Vulkan backend's own categories and nothing else, so the artifact
        /// stays a validation log rather than becoming a dump of every engine subsystem that happens to log. The
        /// Info floor is pinned here too: <c>VulkanInstance</c> announces the live rung at Info, and that line is
        /// what proves the instrument was running on the run being read.
        /// </summary>
        [Fact]
        public void OnlyTheVulkanBackendsCategoriesReachTheSink()
        {
            try
            {
                var sink = new InMemorySink();
                Assert.True(VulkanValidationConsoleLogging.TryConfigure("strict", sink));

                Log.Get("VulkanInstance").Info("the rung is live");
                Log.Get("AudioEngine").Warn("unrelated engine chatter");
                Log.Warn("through the facade's default category");

                var entry = Assert.Single(sink.Entries);
                Assert.Equal("VulkanInstance", entry.Category);
                Assert.Equal(LogLevel.Info, entry.Level);
            }
            finally
            {
                RestoreHostState();
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

        /// <summary>
        /// Puts the process back the way the module initializer left it.
        /// <para>
        /// IT REBUILDS RATHER THAN RE-ADOPTING, which is not a style choice. <c>Log.Configure</c> shuts down
        /// whatever manager it replaces, and shutdown disposes and CLEARS that manager's sink list, so the
        /// manager a test captured on entry is already gutted by the time the test wants it back. Putting that
        /// object back would leave an armed CI leg logging into a manager with no sinks for every test that runs
        /// after this class, which is the original bug wearing a different hat and just as invisible. Reading the
        /// live lever again reproduces the host's real configuration, and leaves the facade unconfigured on every
        /// unarmed run.
        /// </para>
        /// </summary>
        static void RestoreHostState()
        {
            if (!VulkanValidationConsoleLogging.ConfigureFromEnvironment()) Log.Shutdown();
        }
    }
}
