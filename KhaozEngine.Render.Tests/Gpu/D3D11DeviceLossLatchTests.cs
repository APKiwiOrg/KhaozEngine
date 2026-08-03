using System;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION G3: the device-loss latch, driven device-free off a fake removal-reason source and the real
    /// <see cref="D3D11DeviceLiveness"/> token, so the once-only rule, the immediate read, the liveness flip, the
    /// header string and the throwing-site path all run on macOS and Linux.
    /// <para>
    /// WHY IMMEDIACY IS THE WHOLE DESIGN. <c>DXGI_ERROR_DEVICE_REMOVED</c> is sticky: every call after the device
    /// dies returns it, so the reason is only meaningful at the FIRST site that notices. All 25 stacks on issue
    /// #423 pointed at a texture view constructor deep inside resource-set activation, which is the site that
    /// happened to make the next call rather than the site anything went wrong at, and reconstructing what
    /// actually happened cost a full investigation. That is what #427 is about.
    /// </para>
    /// </summary>
    public sealed class D3D11DeviceLossLatchTests
    {
        sealed class FakeRemovedReason : ID3D11RemovedReason
        {
            internal int Reason { get; set; } = D3D11DeviceLossCodes.Ok;
            internal int Reads { get; private set; }
            internal bool Throws { get; set; }

            public int GetDeviceRemovedReason()
            {
                Reads++;
                if (Throws) throw new InvalidOperationException("the device is beyond asking");
                return Reason;
            }
        }

        static (D3D11DeviceLossLatch Latch, D3D11DeviceLiveness Liveness, FakeRemovedReason Reason, RecordingLogger Log)
            Build(int reason = D3D11DeviceLossCodes.DeviceHung)
        {
            var liveness = new D3D11DeviceLiveness();
            var source = new FakeRemovedReason { Reason = reason };
            var log = new RecordingLogger();
            return (new D3D11DeviceLossLatch(liveness, source, log), liveness, source, log);
        }

        /// <summary>An ordinary success is not a device loss and costs no reason read at all, which matters
        /// because this runs after every present, every staging map and every replay.</summary>
        [Theory]
        [InlineData(D3D11DeviceLossCodes.Ok)]
        [InlineData(1)]
        public void Check_IgnoresASuccessfulResult(int hresult)
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, FakeRemovedReason reason, _) = Build();

            Assert.False(latch.Check(hresult, "present"));
            Assert.False(latch.IsLost);
            Assert.True(liveness.IsAlive);
            Assert.Equal(0, reason.Reads);
        }

        /// <summary>
        /// AN ORDINARY FAILURE IS NOT A DEVICE LOSS EITHER, and that distinction is load-bearing. A latch that
        /// fired on every failing HRESULT would kill the device on a plain <c>DXGI_ERROR_INVALID_CALL</c>, after
        /// which every release is a no-op, every fence reads signaled and nothing anywhere says why.
        /// </summary>
        [Fact]
        public void Check_DoesNotLatchOnAnOrdinaryFailure()
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, FakeRemovedReason reason, _) = Build();

            Assert.False(latch.Check(D3D11DeviceLossCodes.InvalidCall, "present"));
            Assert.False(latch.IsLost);
            Assert.True(liveness.IsAlive);
            Assert.Equal(0, reason.Reads);
        }

        [Theory]
        [InlineData(D3D11DeviceLossCodes.DeviceRemoved)]
        [InlineData(D3D11DeviceLossCodes.DeviceReset)]
        public void Check_LatchesOnEitherRemovalCodeAndReadsTheReasonImmediately(int hresult)
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, FakeRemovedReason reason, RecordingLogger log)
                = Build(D3D11DeviceLossCodes.DeviceHung);

            Assert.True(latch.Check(hresult, "present"));

            Assert.True(latch.IsLost);
            Assert.Equal(1, reason.Reads);
            Assert.Equal(hresult, latch.ObservedHresult);
            Assert.Equal(D3D11DeviceLossCodes.DeviceHung, latch.RemovedReason);
            Assert.Equal("present", latch.Site);
            Assert.True(liveness.IsDead);
            Assert.Single(log.Errors);
            Assert.Contains("DXGI_ERROR_DEVICE_HUNG", log.Errors[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// THE LATCH IS TAKEN EXACTLY ONCE. The device is dead from every later site's point of view too, so they
        /// all answer true, but the recorded reason and the recorded SITE stay the first one's: two would be a
        /// race over which the session header carries, and the first site is the only one near the cause.
        /// </summary>
        [Fact]
        public void Check_RecordsTheFirstSiteAndNeverReReadsTheReason()
        {
            (D3D11DeviceLossLatch latch, _, FakeRemovedReason reason, RecordingLogger log) = Build();

            Assert.True(latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "present"));
            reason.Reason = D3D11DeviceLossCodes.DriverInternalError;
            Assert.True(latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "staging map"));
            Assert.True(latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "replay"));

            Assert.Equal(1, reason.Reads);
            Assert.Equal(D3D11DeviceLossCodes.DeviceHung, latch.RemovedReason);
            Assert.Equal("present", latch.Site);
            Assert.Single(log.Errors);
        }

        /// <summary>Once the device is known lost, every check answers true whatever it is handed, so a caller can
        /// use the answer as the one question worth asking: should I stop.</summary>
        [Fact]
        public void Check_AnswersTrueForAnythingOnceTheDeviceIsGone()
        {
            (D3D11DeviceLossLatch latch, _, _, _) = Build();
            latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "present");

            Assert.True(latch.Check(D3D11DeviceLossCodes.Ok, "replay"));
            Assert.True(latch.Check(D3D11DeviceLossCodes.InvalidCall, "staging map"));
        }

        /// <summary>
        /// THE FOURTH SITE (#489): the swapchain's resize apply calls into <c>ResizeBuffers</c>, <c>GetBuffer</c>
        /// and <c>CreateRenderTargetView</c>, all of which end in <c>CheckError</c>, so a device that dies during
        /// a resize arrives as a throw with no HRESULT anything can read. Asking the device directly is what puts
        /// that site back under the latch.
        /// </summary>
        [Fact]
        public void CheckAfterFault_LatchesWhenTheDeviceIsWhyTheCallThrew()
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, _, _) = Build(D3D11DeviceLossCodes.DeviceRemoved);

            Assert.True(latch.CheckAfterFault("swapchain resize apply"));

            Assert.True(latch.IsLost);
            Assert.Equal(D3D11DeviceLossCodes.DeviceRemoved, latch.RemovedReason);
            Assert.Equal("swapchain resize apply", latch.Site);
            Assert.True(liveness.IsDead);
        }

        /// <summary>
        /// A FALSE ANSWER MEANS THE THROW WAS SOMETHING ELSE, and the caller must go on treating it as its own
        /// fault. This answers one question and never handles the exception, so a bug in a resize argument stays
        /// a visible bug instead of being swallowed as a device loss.
        /// </summary>
        [Fact]
        public void CheckAfterFault_LeavesTheDeviceAloneWhenItIsFine()
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, _, RecordingLogger log)
                = Build(D3D11DeviceLossCodes.Ok);

            Assert.False(latch.CheckAfterFault("swapchain resize apply"));
            Assert.False(latch.IsLost);
            Assert.True(liveness.IsAlive);
            Assert.Empty(log.Errors);
        }

        /// <summary>A reason read that faults during a device loss would replace the diagnostic with a second,
        /// less informative failure at exactly the moment the first one mattered. It is swallowed, the loss is
        /// still latched, and the session says it cannot name the cause.</summary>
        [Fact]
        public void Check_StillLatchesWhenTheReasonCannotBeRead()
        {
            (D3D11DeviceLossLatch latch, D3D11DeviceLiveness liveness, FakeRemovedReason reason, RecordingLogger log)
                = Build();
            reason.Throws = true;

            Assert.True(latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "replay"));

            Assert.True(latch.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Equal(D3D11DeviceLossCodes.Ok, latch.RemovedReason);
            Assert.Single(log.Warns);
            Assert.Contains("GetDeviceRemovedReason threw", log.Warns[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// THE SESSION-HEADER FIELD: the stable TOKEN plus the site, not the sentence. A header field is grouped
        /// and counted across captures, so it has to be something two captures of the same fault produce
        /// identically, and the readable sentence goes in the session log beside it.
        /// </summary>
        [Fact]
        public void HeaderValue_IsNullUntilTheLossAndThenNamesTheReasonAndTheSite()
        {
            (D3D11DeviceLossLatch latch, _, _, _) = Build(D3D11DeviceLossCodes.DeviceHung);
            Assert.Null(latch.HeaderValue);

            latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "present");

            Assert.Equal("DXGI_ERROR_DEVICE_HUNG at present", latch.HeaderValue);
        }

        /// <summary>A site nobody named still produces a usable header value rather than a ragged one, because a
        /// diagnostic must never be the thing that produces a malformed capture.</summary>
        [Fact]
        public void HeaderValue_SurvivesAnUnnamedSite()
        {
            (D3D11DeviceLossLatch latch, _, _, _) = Build();
            latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "  ");

            Assert.Equal("DXGI_ERROR_DEVICE_HUNG at an unnamed site", latch.HeaderValue);
        }

        [Fact]
        public void Constructor_RefusesAMissingLivenessOrReasonSource()
        {
            Assert.Throws<ArgumentNullException>(
                () => new D3D11DeviceLossLatch(null!, new FakeRemovedReason()));
            Assert.Throws<ArgumentNullException>(
                () => new D3D11DeviceLossLatch(new D3D11DeviceLiveness(), null!));
        }

        // ---------------------------------------------------------------------------------------------------
        // The HRESULT vocabulary.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>The documented Windows SDK values, pinned. They are written out rather than taken from Vortice
        /// because DXGI's result codes are <c>static readonly</c> SharpGen values rather than compile-time
        /// constants, so naming one would put the interop on the load path off Windows.</summary>
        [Fact]
        public void TheCodesAreTheDocumentedWindowsSdkValues()
        {
            Assert.Equal(unchecked((int)0x887A0001), D3D11DeviceLossCodes.InvalidCall);
            Assert.Equal(unchecked((int)0x887A0005), D3D11DeviceLossCodes.DeviceRemoved);
            Assert.Equal(unchecked((int)0x887A0006), D3D11DeviceLossCodes.DeviceHung);
            Assert.Equal(unchecked((int)0x887A0007), D3D11DeviceLossCodes.DeviceReset);
            Assert.Equal(unchecked((int)0x887A0020), D3D11DeviceLossCodes.DriverInternalError);
        }

        /// <summary>Exactly the two codes decision G3 names. Hung and driver-internal are answers
        /// <c>GetDeviceRemovedReason</c> gives rather than codes a call returns, so treating them as a trigger
        /// would be checking for something that cannot arrive.</summary>
        [Fact]
        public void IsDeviceLoss_IsTheTwoRemovalCodesAndNothingElse()
        {
            Assert.True(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.DeviceRemoved));
            Assert.True(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.DeviceReset));
            Assert.False(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.DeviceHung));
            Assert.False(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.DriverInternalError));
            Assert.False(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.InvalidCall));
            Assert.False(D3D11DeviceLossCodes.IsDeviceLoss(D3D11DeviceLossCodes.Ok));
        }

        /// <summary>The sign bit and nothing more, so an occluded present (a success that presented nothing) is
        /// not mistaken for a fault.</summary>
        [Fact]
        public void IsFailure_IsTheSignBit()
        {
            Assert.False(D3D11DeviceLossCodes.IsFailure(0));
            Assert.False(D3D11DeviceLossCodes.IsFailure(0x087A0001));
            Assert.True(D3D11DeviceLossCodes.IsFailure(D3D11DeviceLossCodes.DeviceRemoved));
        }

        /// <summary>An unrecognized code still has to be reportable, because the point of the field is that a
        /// field crash self-describes.</summary>
        [Fact]
        public void DescribeAndToken_AlwaysProduceSomethingReportable()
        {
            Assert.Contains("DXGI_ERROR_DEVICE_HUNG",
                D3D11DeviceLossCodes.Describe(D3D11DeviceLossCodes.DeviceHung), StringComparison.Ordinal);
            Assert.Contains("0x12345678", D3D11DeviceLossCodes.Describe(0x12345678), StringComparison.Ordinal);
            Assert.Equal("0x12345678", D3D11DeviceLossCodes.Token(0x12345678));
            Assert.Equal("S_OK", D3D11DeviceLossCodes.Token(D3D11DeviceLossCodes.Ok));
        }
    }
}
