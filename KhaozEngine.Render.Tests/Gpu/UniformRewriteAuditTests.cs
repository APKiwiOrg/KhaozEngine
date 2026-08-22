using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// BOTH VERDICTS OF <see cref="UniformRewriteAudit"/>, PINNED WITH NO DEVICE. The audit's rule is arithmetic
    /// over a recording, so the two shapes it has to separate can be recorded through a
    /// <see cref="FakeGpuDevice"/>: the engine's sanctioned whole-mirror-per-slot upload, which is SAFE and which
    /// a range-wide comparison used to flag on shipped code, and the ring collapse the same buffer had before
    /// 17.39.0, which is a HAZARD.
    ///
    /// <para><b>THE TWO CASES ARE THE SAME FRAME BAR ONE LINE.</b> Both write a 512-byte buffer whole, twice, with
    /// a draw in between, and both differ between the two writes. The only difference is which 256-byte slot the
    /// second pass packs, which decides whether the differing bytes are inside the window the intervening draw
    /// bound. That is the whole rule, and stating it as one parameter of one recording is what keeps the two
    /// verdicts from drifting apart.</para>
    ///
    /// <para><b>THIS IS THE GUARD'S ANTI-VACUITY PIN.</b> <see cref="UniformRewriteGuardGpuTests"/> asserts a
    /// finding list is EMPTY, and an empty list is also what a broken scan returns. This pins the red case, needs
    /// no GPU, and so runs on the ordinary push-path suite where the guard is skipped.</para>
    /// </summary>
    public sealed class UniformRewriteAuditTests
    {
        // The shipped GroundDecalRenderer shape: 2 x 256-byte slots, 80 read bytes, the pass's slot bound by a
        // dynamic offset.
        const uint SlotBytes = 256, PayloadBytes = 80, UboBytes = SlotBytes * 2;

        [Fact]
        public void A_whole_mirror_upload_per_slot_is_not_a_hazard()
        {
            List<UniformRewriteAudit.Hazard> hazards = RunTwoPassFrame(secondPassSlot: SlotBytes, out _);

            Assert.Empty(hazards);
        }

        [Fact]
        public void Two_passes_packing_one_slot_is_a_hazard_that_names_the_bound_window()
        {
            List<UniformRewriteAudit.Hazard> hazards = RunTwoPassFrame(secondPassSlot: 0, out IGpuBuffer ubo);

            UniformRewriteAudit.Hazard hazard = Assert.Single(hazards);
            Assert.Same(ubo, hazard.Buffer);
            // The window the FIRST pass's draw bound, and the bytes inside it the second write changed.
            Assert.Equal(0u, hazard.WindowOffset);
            Assert.Equal(PayloadBytes, hazard.WindowBytes);
            Assert.Equal(0u, hazard.Offset);
            Assert.Equal(PayloadBytes, hazard.Bytes);
            Assert.Equal(1, hazard.DrawsBetween);
            Assert.Contains("bound the window [0, 80)", hazard.ToString());
        }

        [Fact]
        public void A_rewrite_with_no_draw_between_it_and_the_first_write_is_not_a_hazard()
        {
            var frame = new Frame();
            frame.Pass(slot: 0, value: 1, draws: 0);
            frame.Pass(slot: 0, value: 2, draws: 1);

            Assert.Empty(frame.Scan());
        }

        // One frame of the two-pass decal shape. Pass one packs slot 0 and binds it, a draw follows, pass two packs
        // the slot at secondPassSlot and binds THAT. Every upload covers the whole buffer, which is the pattern
        // being tested: what differs is only which slot the second write changed.
        static List<UniformRewriteAudit.Hazard> RunTwoPassFrame(uint secondPassSlot, out IGpuBuffer ubo)
        {
            var frame = new Frame();
            frame.Pass(slot: 0, value: 1, draws: 1);
            frame.Pass(slot: secondPassSlot, value: 2, draws: 1);
            ubo = frame.Ubo;
            return frame.Scan();
        }

        // A recorded frame over a fake device: a real tracking decorator, a real recording command list, a real
        // dynamic-offset layout and set. Only the renderer is stubbed out.
        sealed class Frame
        {
            readonly UniformBufferTrackingGpuDevice _tracker;
            readonly RecordingGpuCommandList _rec;
            readonly IGpuResourceSet _set;
            readonly byte[] _mirror = new byte[UboBytes];

            internal Frame()
            {
                _tracker = new UniformBufferTrackingGpuDevice(new FakeGpuDevice());
                IGpuResourceFactory f = _tracker.Factory;
                Ubo = f.CreateBuffer(new GpuBufferDescription(UboBytes, GpuBufferUsage.UniformBuffer));
                IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment,
                        dynamic: true)));
                _set = f.CreateResourceSet(new GpuResourceSetDescription(layout,
                    new GpuBufferRange(Ubo, 0, PayloadBytes)));
                _rec = new RecordingGpuCommandList(f.CreateCommandList())
                {
                    CapturePayloads = true,
                    UniformWindowsOfSet = _tracker.WindowsOf,
                };
            }

            internal IGpuBuffer Ubo { get; }

            /// <summary>One pass: stamp <paramref name="value"/> over the payload of <paramref name="slot"/> in
            /// the CPU mirror, upload the mirror WHOLE, then bind that slot and record
            /// <paramref name="draws"/> draws through it.</summary>
            internal void Pass(uint slot, byte value, int draws)
            {
                _mirror.AsSpan((int)slot, (int)PayloadBytes).Fill(value);
                _rec.UpdateBuffer(Ubo, 0, (ReadOnlySpan<byte>)_mirror);
                _rec.SetGraphicsResourceSet(0, _set, slot);
                for (int i = 0; i < draws; i++) _rec.Draw(6);
            }

            internal List<UniformRewriteAudit.Hazard> Scan()
            {
                Assert.Equal(0, _tracker.UnresolvedResourceSets);
                return UniformRewriteAudit.Scan(_rec.Uploads, _tracker.IsUniform, _rec.Reads);
            }
        }
    }
}
