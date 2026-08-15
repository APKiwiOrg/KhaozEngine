using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/617: the Metal half of the test host's validation logging
    /// seam. Armed, the native Metal backend's own lines reach the configured sink, and the one that matters most
    /// is <c>MetalDeviceLossLatch</c>'s failed-command-buffer ERROR, because a run where every golden reads back
    /// as the pass clear colour means something completely different depending on whether the command buffers
    /// failed.
    /// <para>
    /// NOTHING HERE CALLS <see cref="Log.Configure(LoggerOptions)"/>, for the reason spelled out on
    /// <see cref="VulkanValidationConsoleLoggingTests"/>: a reconfigure is not restorable in this process, and
    /// both native backends resolve their loggers once into <c>static readonly</c> fields. The tests drive the
    /// pure half against a <see cref="LogManager"/> they own.
    /// </para>
    /// <para>
    /// Device-free throughout. The latch takes a plain <see cref="MetalCommandBufferFault"/> snapshot precisely so
    /// its behaviour is decided on a machine with no Metal at all.
    /// </para>
    /// </summary>
    public sealed class MetalValidationConsoleLoggingTests
    {
        /// <summary>
        /// THE FIX, end to end, on the reading #617 could not get: a failed command buffer reaches the sink as an
        /// ERROR line naming the driver's own description and the site that first saw it. On the run that
        /// provoked the issue this line could not have existed, because the facade was an unconfigured no-op on
        /// every Metal leg.
        /// </summary>
        [Fact]
        public void AnArmedRun_PutsAFailedCommandBufferInTheSinkAtError()
        {
            var sink = new InMemorySink();
            using var manager = new LogManager(BuildMetalOptions(sink));

            var latch = new MetalDeviceLossLatch(
                new DeviceLiveness(), manager.GetLogger<MetalDeviceLossLatch>());

            Assert.True(latch.Check(
                new MetalCommandBufferFault(
                    MTLCommandBufferStatus.Error,
                    MTLCommandBufferError.Internal,
                    "a fabricated driver description"),
                "a fabricated site"));

            var entry = Assert.Single(sink.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(nameof(MetalDeviceLossLatch), entry.Category);
            Assert.Contains("a fabricated site", entry.Message, StringComparison.Ordinal);
            Assert.Contains("a fabricated driver description", entry.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE SCOPE. An armed leg carries the native Metal backend's own categories and nothing else, so the
        /// artifact stays a validation log rather than a dump of every engine subsystem that happens to log. The
        /// Info floor is pinned in both directions: <c>MetalGpuDevice</c> announces the live tier and the device
        /// class at Info and that line proves the instrument was running, while anything below Info is dropped
        /// even from a Metal category.
        /// </summary>
        [Fact]
        public void OnlyTheMetalBackendsCategoriesReachTheSink()
        {
            var sink = new InMemorySink();
            using var manager = new LogManager(BuildMetalOptions(sink));

            manager.GetLogger("MetalGpuDevice").Info("the tier is live");
            manager.GetLogger("MetalGpuDevice").Debug("below the floor");
            manager.GetLogger("VulkanInstance").Info("the other backend");
            manager.GetLogger("AudioEngine").Warn("unrelated engine chatter");
            manager.GetLogger(manager.DefaultCategory).Warn("through the facade's default category");

            var entry = Assert.Single(sink.Entries);
            Assert.Equal("MetalGpuDevice", entry.Category);
            Assert.Equal(LogLevel.Info, entry.Level);
        }

        /// <summary>
        /// BOTH BACKENDS AT ONCE STILL REACH ONE DESTINATION, which is the property that makes a single
        /// <c>Log.Configure</c> workable at all. A leg that armed both rungs gets both prefixes through the one
        /// wrapper rather than two wrappers over one console, which would dispose it twice.
        /// </summary>
        [Fact]
        public void BothPrefixesArmedTogether_ShareTheOneDestination()
        {
            var sink = new InMemorySink();
            using var manager = new LogManager(GpuValidationConsoleLogging.BuildOptions(
                sink,
                new[] { VulkanValidationConsoleLogging.CategoryPrefix, MetalValidationConsoleLogging.CategoryPrefix }));

            manager.GetLogger("MetalGpuDevice").Info("metal");
            manager.GetLogger("VulkanInstance").Info("vulkan");
            manager.GetLogger("AudioEngine").Info("neither");

            Assert.Equal(2, sink.Entries.Count);
        }

        /// <summary>
        /// Which tiers configure a sink. Both real ones do: the API layer alone already produces the device-class
        /// line and every command-buffer failure, and those are the readings a Metal artifact exists for.
        /// <para>
        /// The tier travels as a STRING because <c>MetalValidationMode</c> is internal and a public xUnit theory
        /// cannot take one in its signature, which is the same reason <see cref="MetalValidationTests"/> spells
        /// its modes out.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("Off", false)]
        [InlineData("On", true)]
        [InlineData("Shaders", true)]
        public void ATierArmsTheSink_WheneverItIsNotOff(string armed, bool expected)
            => Assert.Equal(
                expected,
                MetalValidationConsoleLogging.IsArmed(Enum.Parse<MetalValidationMode>(armed)));

        /// <summary>
        /// The announcement an armed run writes, which is what makes an artifact with no Metal lines in it
        /// readable: it says the sink existed, so a quiet log is a clean run rather than a lost producer. That
        /// ambiguity is exactly the state #617's artifact was in. It has to survive its own category filter, and
        /// it has to name the variables, the tier, the scope and the shape a reader then greps for.
        /// </summary>
        [Fact]
        public void TheArmedAnnouncement_NamesTheVariablesTheTierAndTheShape()
        {
            string line = MetalValidationConsoleLogging.ArmedAnnouncement(
                Arming(debugLayerArmed: true, shaderValidationArmed: true));

            Assert.Contains(MetalValidation.DebugLayerVar, line, StringComparison.Ordinal);
            Assert.Contains(MetalValidation.ShaderValidationVar, line, StringComparison.Ordinal);
            Assert.Contains("Shaders", line, StringComparison.Ordinal);
            Assert.Contains("A native Metal command buffer FAILED", line, StringComparison.Ordinal);
            Assert.Contains(MetalValidationConsoleLogging.CategoryPrefix, line, StringComparison.Ordinal);

            Assert.StartsWith(
                MetalValidationConsoleLogging.CategoryPrefix,
                MetalValidationConsoleLogging.HostCategory,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// A SHADER-ONLY RUN'S HEADER DOES NOT NAME <c>MTL_DEBUG_LAYER</c>, which is #628 in the test host rather
        /// than in the backend. The header named both variables on every armed run because it was handed the
        /// merged tier, and the merge cannot tell a shader-only launch from a both-armed one. This is the first
        /// line of the artifact, so it fixed the reader's belief about the launch environment before any engine
        /// line could correct it.
        /// </summary>
        [Fact]
        public void TheArmedAnnouncement_OnAShaderOnlyRun_NamesOnlyTheShaderVariable()
        {
            string line = MetalValidationConsoleLogging.ArmedAnnouncement(
                Arming(debugLayerArmed: false, shaderValidationArmed: true));

            Assert.Contains(MetalValidation.ShaderValidationVar, line, StringComparison.Ordinal);
            Assert.DoesNotContain(MetalValidation.DebugLayerVar, line, StringComparison.Ordinal);
            Assert.Contains("Shaders", line, StringComparison.Ordinal);
        }

        /// <summary>The debug layer alone reports the lower tier and names only its own variable.</summary>
        [Fact]
        public void TheArmedAnnouncement_OnADebugLayerOnlyRun_NamesOnlyTheDebugVariable()
        {
            string line = MetalValidationConsoleLogging.ArmedAnnouncement(new MetalValidationArming(
                MetalValidationMode.On, null, MetalValidationMode.On, true, false, false, false));

            Assert.Contains(MetalValidation.DebugLayerVar, line, StringComparison.Ordinal);
            Assert.DoesNotContain(MetalValidation.ShaderValidationVar, line, StringComparison.Ordinal);
            Assert.Contains("On", line, StringComparison.Ordinal);
        }

        // An arming reading at the Shaders tier, with the two variables driven independently of it: that is the
        // whole distinction the merged tier loses.
        static MetalValidationArming Arming(bool debugLayerArmed, bool shaderValidationArmed)
            => new(MetalValidationMode.Shaders, null, MetalValidationMode.Shaders, debugLayerArmed,
                shaderValidationArmed, false, false);

        /// <summary>
        /// THE ONE READ OF THE PROCESS FACADE on this side, read-only, and it asserts the same consistency the
        /// Vulkan row does: a Metal-armed host has a configured manager with the latch's category enabled on it.
        /// Which branch runs is decided by the launch environment rather than by this test, which is why it
        /// asserts a consistency rather than a state.
        /// </summary>
        [Fact]
        public void TheProcessFacade_MatchesTheTierTheHostWasLaunchedWith()
        {
            if (!MetalValidationConsoleLogging.IsArmed(MetalValidationConsoleLogging.ArmedTier())) return;

            Assert.True(Log.IsConfigured);
            Assert.True(Log.For<MetalDeviceLossLatch>().IsEnabled(LogLevel.Error));
        }

        static LoggerOptions BuildMetalOptions(ILogSink destination)
            => GpuValidationConsoleLogging.BuildOptions(
                destination, new[] { MetalValidationConsoleLogging.CategoryPrefix });
    }
}
