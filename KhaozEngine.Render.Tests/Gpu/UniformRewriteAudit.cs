using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DURABLE OUTCOME OF <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see>: the scan
    /// that reads a frame's recorded command timeline and answers whether any renderer rewrote a range of a
    /// UNIFORM buffer it had already written, with a draw recorded in between and with different bytes.
    ///
    /// <para><b>WHY THAT EXACT SHAPE IS THE HAZARD.</b> A record-time <c>IGpuCommandList.UpdateBuffer</c> to a
    /// uniform buffer on the three engine-owned native backends is a memcpy into that frame's own ring segment. It
    /// records no command, so it is NOT ordered against the draws in the same list: the last write in the frame
    /// decides every byte, and a draw recorded between two writes reads the SECOND one. On the Veldrid backends
    /// the same call is a recorded copy that executes in command order and the draw reads the FIRST.
    /// <c>RecordTimeUniformRewriteGpuTests</c> measures both readings on one machine. So a renderer with this
    /// shape renders differently on the two families, silently, with nothing thrown and nothing logged.</para>
    ///
    /// <para><b>AND WHY EACH OF THE THREE CONDITIONS HAS TO BE THERE.</b> Drop the UNIFORM condition and the scan
    /// flags every vertex and instance stream the frame legitimately re-streams between draws (the water grid per
    /// plane, the trail vertices per blend), because those are not ring-backed and their record-time writes ARE
    /// ordered on every backend. Drop the DRAW-BETWEEN condition and it flags a redundant write that no draw can
    /// tell apart from one write. Drop the DIFFERENT-BYTES condition and it flags the deliberate, proven-safe
    /// pattern this engine uses in four places: pack your own slot, upload the WHOLE mirror, and let the slots
    /// already recorded against go up again carrying the bytes they already held (<c>SpriteBatch</c> per Begin,
    /// and since 17.39.0 <c>GroundDecalRenderer</c> per pass).</para>
    ///
    /// <para><b>IT IS A SCAN OVER A RECORDING, NOT AN ASSERTION.</b> What a caller does with the findings is the
    /// caller's: <see cref="UniformRewriteGuardGpuTests"/> renders a frame that reaches every pass and asserts the
    /// list is empty. Keeping the scan separate is what lets a future row point it at a different frame without
    /// copying the rule.</para>
    /// </summary>
    internal static class UniformRewriteAudit
    {
        /// <summary>One flagged pair: the buffer, the overlapping byte range, and the two uploads' positions in
        /// the frame's draw order. Everything a failure message needs to name the site without a debugger.</summary>
        internal readonly record struct Hazard(IGpuBuffer Buffer, uint Offset, uint Bytes, int DrawsBeforeFirst,
            int DrawsBeforeSecond)
        {
            /// <summary>How many draws or dispatches sit between the two writes, which is what makes it a
            /// collapse rather than a redundant write.</summary>
            public int DrawsBetween => DrawsBeforeSecond - DrawsBeforeFirst;

            public override string ToString() =>
                $"a {Buffer.SizeInBytes}-byte uniform buffer had [{Offset}, {Offset + Bytes}) written twice with "
                + $"{DrawsBetween} draw(s) recorded between the two writes, and the bytes differ";
        }

        /// <summary>
        /// Scan one frame's uploads. <paramref name="isUniform"/> answers whether a buffer was created with
        /// <see cref="GpuBufferUsage.UniformBuffer"/>, which is the only usage the native backends ring-back;
        /// <see cref="UniformBufferTrackingGpuDevice"/> is how a caller gets that answer. The uploads MUST have
        /// been recorded with <c>CapturePayloads</c> on, because a comparison of bytes is the whole third
        /// condition, and a scan with no bytes to compare would report the safe whole-mirror pattern as a hazard.
        /// </summary>
        /// <exception cref="InvalidOperationException">A uniform upload carries no captured payload.</exception>
        internal static List<Hazard> Scan(IReadOnlyList<RecordingGpuCommandList.Upload> uploads,
            Func<IGpuBuffer, bool> isUniform)
        {
            ArgumentNullException.ThrowIfNull(uploads);
            ArgumentNullException.ThrowIfNull(isUniform);

            var found = new List<Hazard>();
            for (int i = 0; i < uploads.Count; i++)
            {
                RecordingGpuCommandList.Upload first = uploads[i];
                if (!isUniform(first.Buffer)) continue;
                RequirePayload(first);

                for (int j = i + 1; j < uploads.Count; j++)
                {
                    RecordingGpuCommandList.Upload second = uploads[j];
                    if (!ReferenceEquals(second.Buffer, first.Buffer)) continue;
                    RequirePayload(second);

                    // No draw between them: the second write simply replaces the first before anything could read
                    // either, which is what every backend does and what a redundant write means.
                    if (second.DrawsBefore == first.DrawsBefore) continue;

                    if (!TryOverlap(first, second, out uint offset, out uint bytes)) continue;
                    if (SameBytes(first, second, offset, bytes)) continue;

                    found.Add(new Hazard(first.Buffer, offset, bytes, first.DrawsBefore, second.DrawsBefore));
                }
            }

            return found;
        }

        /// <summary>The findings as a message a failing assertion can print whole. Empty in, empty out.</summary>
        internal static string Describe(IReadOnlyList<Hazard> hazards)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hazards.Count; i++) sb.Append("\n  - ").Append(hazards[i].ToString());
            return sb.ToString();
        }

        static void RequirePayload(in RecordingGpuCommandList.Upload upload)
        {
            if (upload.Data is not null) return;

            throw new InvalidOperationException(
                "UniformRewriteAudit.Scan was given an upload with no captured payload. Set "
                + "RecordingGpuCommandList.CapturePayloads before recording the frame: the scan's third condition "
                + "is that the rewritten bytes DIFFER, and without the bytes it would report the engine's "
                + "deliberate whole-mirror re-upload as a hazard.");
        }

        // The intersection of the two writes' byte ranges, if any. Two writes that never touch the same byte are
        // two independent slots of one buffer, which is the SHAPE the fix for a hazard takes.
        static bool TryOverlap(in RecordingGpuCommandList.Upload a, in RecordingGpuCommandList.Upload b,
            out uint offset, out uint bytes)
        {
            uint start = Math.Max(a.Offset, b.Offset);
            uint end = Math.Min(a.Offset + a.Bytes, b.Offset + b.Bytes);
            offset = start;
            bytes = end > start ? end - start : 0u;
            return bytes > 0;
        }

        // Whether the two writes put the SAME bytes in the overlapping range. Compared on the overlap alone, so a
        // whole-buffer re-upload that only re-states an earlier slot is safe however much else it carries.
        static bool SameBytes(in RecordingGpuCommandList.Upload a, in RecordingGpuCommandList.Upload b,
            uint offset, uint bytes)
        {
            ReadOnlySpan<byte> first = a.Data!.AsSpan((int)(offset - a.Offset), (int)bytes);
            ReadOnlySpan<byte> second = b.Data!.AsSpan((int)(offset - b.Offset), (int)bytes);
            return first.SequenceEqual(second);
        }
    }
}
