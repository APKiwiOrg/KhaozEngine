using System;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-G3 and V-G5: <c>KE_VULKAN_VALIDATION</c>, its four rungs, the pump they feed and the rate limit
    /// under it. Everything here is device-free, over plain message values, so the promotion, the caps, the strict
    /// latch and the controlled throw all run on a machine with no Vulkan loader. The native half is the
    /// messenger, which is the only part that cannot be tested off a driver.
    /// </summary>
    public sealed class VulkanValidationKnobTests
    {
        /// <summary>Every recognized value, in the spellings a shell actually produces.</summary>
        [Theory]
        [InlineData(null, (int)VulkanValidationMode.Off)]
        [InlineData("", (int)VulkanValidationMode.Off)]
        [InlineData("   ", (int)VulkanValidationMode.Off)]
        [InlineData("0", (int)VulkanValidationMode.Off)]
        [InlineData("off", (int)VulkanValidationMode.Off)]
        [InlineData("false", (int)VulkanValidationMode.Off)]
        [InlineData("1", (int)VulkanValidationMode.On)]
        [InlineData("true", (int)VulkanValidationMode.On)]
        [InlineData(" ON ", (int)VulkanValidationMode.On)]
        [InlineData("strict", (int)VulkanValidationMode.Strict)]
        [InlineData("STRICT", (int)VulkanValidationMode.Strict)]
        [InlineData("sync", (int)VulkanValidationMode.Sync)]
        [InlineData(" Sync ", (int)VulkanValidationMode.Sync)]
        // The expected rung travels as an int because the enum is internal to the backend package and a public
        // xUnit test method may not name it in its signature.
        public void TheLadderParses(string? value, int expected)
        {
            Assert.Equal(expected, (int)VulkanValidation.Parse(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A TYPO IS OFF PLUS A WARNING, and there is deliberately no "anything else means on" reading. This knob
        /// has four values and a fifth is a mistake, and reading a typo as a level would be the worst outcome
        /// available: a session that believes it is running <c>strict</c> and is running nothing produces a clean
        /// run that proves nothing.
        /// </summary>
        [Theory]
        [InlineData("2")]
        [InlineData("verbose")]
        [InlineData("strict!")]
        [InlineData("syncronization")]
        public void AnUnrecognizedValue_IsOffAndSaysWhatWorks(string value)
        {
            Assert.Equal(VulkanValidationMode.Off, VulkanValidation.Parse(value, out string? unrecognized));

            Assert.Equal(value, unrecognized);
            string warning = VulkanValidation.UnrecognizedWarning(unrecognized!);
            Assert.Contains("strict", warning, StringComparison.Ordinal);
            Assert.Contains("sync", warning, StringComparison.Ordinal);
        }

        /// <summary>Which rung wants what. The ladder is only useful if the three questions it answers are
        /// answered differently by different rungs.</summary>
        [Theory]
        [InlineData((int)VulkanValidationMode.Off, false, false, false)]
        [InlineData((int)VulkanValidationMode.On, true, false, false)]
        [InlineData((int)VulkanValidationMode.Strict, true, false, true)]
        [InlineData((int)VulkanValidationMode.Sync, true, true, false)]
        public void EachRungAsksForItsOwnThings(int rung, bool messenger, bool sync, bool throws)
        {
            var mode = (VulkanValidationMode)rung;

            Assert.Equal(messenger, VulkanValidation.WantsMessenger(mode));
            Assert.Equal(sync, VulkanValidation.WantsSynchronizationValidation(mode));
            Assert.Equal(throws, VulkanValidation.ThrowsOnError(mode));
        }

        /// <summary>The default says NOTHING, because an INFO line on every session about an unset lever is a line
        /// nobody reads. Every other rung says so, so a capture proves the lever was set rather than resting on
        /// the tester believing they set it.</summary>
        [Fact]
        public void OnlyAnActiveRungAnnouncesItself()
        {
            Assert.Empty(VulkanValidation.ActiveDescription(VulkanValidationMode.Off));

            Assert.Contains("VK_LAYER_KHRONOS_validation",
                VulkanValidation.ActiveDescription(VulkanValidationMode.On), StringComparison.Ordinal);
            Assert.Contains("STRICT",
                VulkanValidation.ActiveDescription(VulkanValidationMode.Strict), StringComparison.Ordinal);
            Assert.Contains("SYNCHRONISATION",
                VulkanValidation.ActiveDescription(VulkanValidationMode.Sync), StringComparison.Ordinal);
        }

        /// <summary>A machine with no layer installed gets a WARN naming what to install, and creation goes on
        /// WITHOUT validation. The person who set the variable is by definition mid-diagnosis, and stopping their
        /// app from starting is the least useful thing to do to them.</summary>
        [Fact]
        public void AMissingLayer_NamesTheFix()
        {
            string warning = VulkanValidation.LayerUnavailableWarning(VulkanValidationMode.On);

            Assert.Contains("vulkan-validationlayers", warning, StringComparison.Ordinal);
            Assert.Contains("WITHOUT", warning, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The pump: the promotion, the strict latch, the controlled throw, and the rule that the callback path never
    /// throws whatever happens to it.
    /// </summary>
    public sealed class VulkanValidationPumpTests
    {
        static VulkanValidationMessage Error(string text = "the thing is wrong", int id = 7,
            string name = "VUID-vkCmdDraw-None-02700")
            => new(VulkanValidationSeverity.Error, id, name, text);

        static VulkanValidationMessage Warning(string text = "the thing is questionable", int id = 3,
            string name = "UNASSIGNED-BestPractices")
            => new(VulkanValidationSeverity.Warning, id, name, text);

        /// <summary>Error goes to ERROR and warning goes to WARN. Unlike the Direct3D 11 pump, which caps at WARN,
        /// because a Vulkan validation error is a spec violation with a VUID attached rather than a driver's
        /// opinion about something benign.</summary>
        [Fact]
        public void SeverityIsPromoted()
        {
            var log = new RecordingLogger();
            var pump = new VulkanValidationPump(VulkanValidationMode.On, logger: log);

            pump.Report(Error());
            pump.Report(Warning());

            Assert.Single(log.Errors);
            Assert.Single(log.Warns);
            Assert.Contains("VUID-vkCmdDraw-None-02700", log.Errors[0], StringComparison.Ordinal);
        }

        /// <summary>Errors are counted whether or not the rate limit let them through, so a session that
        /// suppressed four hundred copies of one error still reports four hundred errors. A run reporting zero
        /// because the log went quiet is the exact misreading the limiter's notes exist to prevent.</summary>
        [Fact]
        public void ErrorsAreCountedEvenWhenSuppressed()
        {
            var pump = new VulkanValidationPump(VulkanValidationMode.On,
                new VulkanValidationRateLimit(repeatsPerMessage: 2), new RecordingLogger());

            for (int i = 0; i < 50; i++) pump.Report(Error());

            Assert.Equal(50, pump.ErrorCount);
            Assert.True(pump.Suppressed > 0);
        }

        /// <summary>
        /// The <c>1</c> rung LOGS and never latches, which is the difference between it and <c>strict</c>. It is
        /// also the rung a soak session runs on, so an error must not be able to stop the run.
        /// </summary>
        [Fact]
        public void ThePlainRung_LogsAndNeverThrows()
        {
            var pump = new VulkanValidationPump(VulkanValidationMode.On, logger: new RecordingLogger());

            pump.Report(Error());

            Assert.False(pump.IsLatched);
            pump.ThrowIfLatched("WaitForIdle");
        }

        /// <summary>
        /// <c>strict</c> LATCHES AND THROWS AT A CONTROLLED POINT, never inside the callback. The message names
        /// the site the check was made at and the message that latched, and it says WHY the throw is not where the
        /// error was seen: unwinding a managed exception through native driver frames destroys the stack the
        /// diagnostic was about.
        /// </summary>
        [Fact]
        public void TheStrictRung_LatchesAndThrowsAtTheControlledPoint()
        {
            var log = new RecordingLogger();
            var pump = new VulkanValidationPump(VulkanValidationMode.Strict, logger: log);

            // Reporting does NOT throw. That is the whole decision: the callback returns into the driver.
            pump.Report(Error("the descriptor set is not bound"));

            Assert.True(pump.IsLatched);
            Assert.Single(log.Errors);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => pump.ThrowIfLatched("vkCreateDevice"));

            Assert.Contains("vkCreateDevice", ex.Message, StringComparison.Ordinal);
            Assert.Contains("the descriptor set is not bound", ex.Message, StringComparison.Ordinal);
            Assert.Contains("KE_VULKAN_VALIDATION=strict", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A warning never latches, on any rung. <c>strict</c> is about errors, and latching on a
        /// best-practices note would stop every run on a layer that has opinions.</summary>
        [Fact]
        public void TheStrictRung_DoesNotLatchOnAWarning()
        {
            var pump = new VulkanValidationPump(VulkanValidationMode.Strict, logger: new RecordingLogger());

            pump.Report(Warning());

            Assert.False(pump.IsLatched);
            pump.ThrowIfLatched("WaitForIdle");
        }

        /// <summary>The FIRST error is the one reported, not the last. A later error is a consequence of the first
        /// as often as it is a second defect, and a latch that kept overwriting would name whichever error
        /// happened to be last.</summary>
        [Fact]
        public void TheStrictRung_KeepsTheFirstError()
        {
            var pump = new VulkanValidationPump(VulkanValidationMode.Strict, logger: new RecordingLogger());

            pump.Report(Error("the first one", id: 1, name: "VUID-first"));
            pump.Report(Error("the second one", id: 2, name: "VUID-second"));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => pump.ThrowIfLatched("WaitForIdle"));

            Assert.Contains("the first one", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the second one", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A LOGGER THAT FAULTS LOSES THE MESSAGE AND NOTHING ELSE. The report path runs inside a native driver
        /// callback, so an exception unwinding out of it goes through driver frames, which is undefined behaviour
        /// and destroys the stack the message was about. Losing one message is the cheaper failure by a wide
        /// margin, and this is the row that pins it.
        /// </summary>
        [Fact]
        public void AFaultingLogger_NeverEscapesTheReportPath()
        {
            var pump = new VulkanValidationPump(VulkanValidationMode.Strict, logger: new ThrowingLogger());

            pump.Report(Error());

            // The latch still took, because it happens before the log call.
            Assert.True(pump.IsLatched);
        }
    }

    /// <summary>
    /// The rate limit: two caps rather than the Direct3D 11 pump's three, because a <c>VK_EXT_debug_utils</c>
    /// messenger is PUSHED from the driver's thread and has no frame boundary to reset a per-frame budget at.
    /// </summary>
    public sealed class VulkanValidationRateLimitTests
    {
        static VulkanValidationMessage Message(int id, string text)
            => new(VulkanValidationSeverity.Error, id, "VUID-" + id, text);

        /// <summary>
        /// THE CAP THAT DOES THE REAL WORK. Validation's characteristic failure is one mistake reported once per
        /// draw call, so without a per-identity cap a session is thousands of copies of one line and the second
        /// distinct message is invisible.
        /// </summary>
        [Fact]
        public void OneRepeatedMessage_IsCappedAndOtherMessagesAreUnaffected()
        {
            var limit = new VulkanValidationRateLimit(repeatsPerMessage: 3);

            for (int i = 0; i < 3; i++) Assert.True(limit.Admit(Message(1, "same"), out _));

            // The fourth is refused and says so ONCE, naming the message it is about.
            Assert.False(limit.Admit(Message(1, "same"), out string? note));
            Assert.NotNull(note);
            Assert.Contains("VUID-1", note, StringComparison.Ordinal);

            // And the fifth is refused SILENTLY, because saying it again every time would be the firehose the cap
            // exists to stop.
            Assert.False(limit.Admit(Message(1, "same"), out string? second));
            Assert.Null(second);

            // A DIFFERENT message is unaffected, which is the entire point of keying per identity.
            Assert.True(limit.Admit(Message(2, "different"), out _));
        }

        /// <summary>The session cap is the soak backstop, for a slow trickle of DISTINCT messages that would
        /// otherwise pass the per-identity cap forever. It also says so exactly once.</summary>
        [Fact]
        public void TheSessionCap_IsTheBackstop()
        {
            var limit = new VulkanValidationRateLimit(messagesPerSession: 4);

            for (int i = 0; i < 4; i++) Assert.True(limit.Admit(Message(i, "distinct " + i), out _));

            Assert.False(limit.Admit(Message(99, "one more"), out string? note));
            Assert.NotNull(note);
            Assert.Contains("cap", note, StringComparison.Ordinal);

            Assert.False(limit.Admit(Message(100, "and another"), out string? second));
            Assert.Null(second);

            Assert.Equal(4, limit.Admitted);
            Assert.Equal(2, limit.Suppressed);
        }

        /// <summary>
        /// A CAP THAT SUPPRESSES SAYS SO. The suppressed count is what stops a truncated log reading as a quiet
        /// one, and "the log stops at message 512" read as "the problem stopped" is precisely the wrong
        /// conclusion.
        /// </summary>
        [Fact]
        public void SuppressionIsCounted()
        {
            var limit = new VulkanValidationRateLimit(repeatsPerMessage: 1, messagesPerSession: 100);

            limit.Admit(Message(1, "same"), out _);
            for (int i = 0; i < 9; i++) limit.Admit(Message(1, "same"), out _);

            Assert.Equal(1, limit.Admitted);
            Assert.Equal(9, limit.Suppressed);
        }

        /// <summary>
        /// THREAD-SAFE, unlike the Direct3D 11 limiter, and for the structural reason the per-frame cap is absent:
        /// the callback arrives on whatever thread made the offending call, so two threads can be inside
        /// <c>Admit</c> at once. A race here would corrupt the dictionary rather than merely miscount.
        /// </summary>
        [Fact]
        public void ConcurrentAdmissions_AreCountedExactly()
        {
            var limit = new VulkanValidationRateLimit(repeatsPerMessage: 1000, messagesPerSession: 1000);

            Parallel.For(0, 500, i => limit.Admit(Message(i, "distinct " + i), out _));

            Assert.Equal(500, limit.Admitted);
            Assert.Equal(0, limit.Suppressed);
        }
    }

    /// <summary>An <see cref="KhaozEngine.Diagnostics.ILogger"/> that fails at everything, for the one row that
    /// asserts a logger failure inside a native callback never escapes.</summary>
    internal sealed class ThrowingLogger : KhaozEngine.Diagnostics.ILogger
    {
        public string Category => "throwing";

        public bool IsEnabled(KhaozEngine.Diagnostics.LogLevel level) => true;

        public void Log(KhaozEngine.Diagnostics.LogLevel level, string message, Exception? exception = null)
            => throw new InvalidOperationException("the sink is gone");

        public void Trace(string message, Exception? exception = null) => throw new InvalidOperationException();
        public void Debug(string message, Exception? exception = null) => throw new InvalidOperationException();
        public void Info(string message, Exception? exception = null) => throw new InvalidOperationException();
        public void Warn(string message, Exception? exception = null) => throw new InvalidOperationException();
        public void Error(string message, Exception? exception = null) => throw new InvalidOperationException();
        public void Fatal(string message, Exception? exception = null) => throw new InvalidOperationException();
    }
}
