using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DURABLE OUTCOME OF <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see>: the scan
    /// that reads a frame's recorded command timeline and answers whether any renderer rewrote uniform bytes a
    /// draw recorded in between had already bound.
    ///
    /// <para><b>WHY THAT EXACT SHAPE IS THE HAZARD.</b> A record-time <c>IGpuCommandList.UpdateBuffer</c> to a
    /// uniform buffer on the three engine-owned native backends is a memcpy into that frame's own ring segment. It
    /// records no command, so it is NOT ordered against the draws in the same list: the last write in the frame
    /// decides every byte, and a draw recorded between two writes reads the SECOND one. On the Veldrid backends
    /// the same call was a recorded copy that executed in command order and the draw read the FIRST.
    /// <c>RecordTimeUniformRewriteGpuTests</c> measures both readings on one machine. So a renderer with this
    /// shape renders differently on the two families, silently, with nothing thrown and nothing logged.</para>
    ///
    /// <para><b>THE RULE, EXACTLY.</b> Two uploads to one buffer are a hazard when all four hold:
    /// <list type="number">
    /// <item>the buffer is a UNIFORM buffer, the only usage any of the three rings backs. Drop this and the scan
    /// flags every vertex and instance stream the frame legitimately re-streams between draws (the water grid per
    /// plane, the trail vertices per blend), because those are not ring-backed and their record-time writes ARE
    /// ordered on every backend,</item>
    /// <item>a DRAW or dispatch is recorded between them. Drop this and it flags a redundant write that no draw
    /// can tell apart from a single one,</item>
    /// <item>one of those intervening draws BOUND a window of that buffer, and</item>
    /// <item>the two uploads disagree on a byte INSIDE that window.</item>
    /// </list>
    /// Conditions 3 and 4 are one idea and have to be applied together, over the window and not over the whole
    /// overlapping range. The engine's sanctioned pattern is to pack your own slot, upload the WHOLE mirror, and
    /// let the slots already recorded against go up again carrying the bytes they already held
    /// (<c>SpriteBatch</c> per Begin, and since 17.39.0 <c>GroundDecalRenderer</c> per pass). Two passes of that
    /// pattern DO differ, in the slot each of them owns, so judging the pair on the whole overlap reports shipped,
    /// correct code as a collapse. Judging it on the bytes an intervening draw could actually have read does not.
    /// <see cref="UniformWindowIndex"/> is where the windows come from.</para>
    ///
    /// <para><b>IT IS A SCAN OVER A RECORDING, NOT AN ASSERTION.</b> What a caller does with the findings is the
    /// caller's: <see cref="UniformRewriteGuardGpuTests"/> renders frames that reach every pass and asserts the
    /// list is empty. Keeping the scan separate is what lets a future row point it at a different frame without
    /// copying the rule, and it is what lets <see cref="UniformRewriteAuditTests"/> pin both verdicts with no
    /// device at all.</para>
    /// </summary>
    internal static class UniformRewriteAudit
    {
        /// <summary>One flagged pair: the buffer, the bytes that differ inside a window an intervening draw bound,
        /// that window, and the two uploads' positions in the frame's draw order. Everything a failure message
        /// needs to name the site without a debugger.</summary>
        internal readonly record struct Hazard(IGpuBuffer Buffer, uint Offset, uint Bytes, uint WindowOffset,
            uint WindowBytes, int DrawsBeforeFirst, int DrawsBeforeSecond)
        {
            /// <summary>How many draws or dispatches sit between the two writes, which is what makes it a
            /// collapse rather than a redundant write.</summary>
            public int DrawsBetween => DrawsBeforeSecond - DrawsBeforeFirst;

            public override string ToString() =>
                $"a {Buffer.SizeInBytes}-byte uniform buffer had [{Offset}, {Offset + Bytes}) written twice with "
                + $"{DrawsBetween} draw(s) recorded between the two writes, the bytes differ, and a draw in "
                + $"between bound the window [{WindowOffset}, {WindowOffset + WindowBytes}) of it";
        }

        /// <summary>
        /// Scan one frame's uploads against the windows its draws bound. <paramref name="isUniform"/> answers
        /// whether a buffer was created with <see cref="GpuBufferUsage.UniformBuffer"/>, which is the only usage
        /// the native backends ring-back, and <see cref="UniformBufferTrackingGpuDevice"/> is how a caller gets
        /// that answer. The uploads MUST have been recorded with <c>CapturePayloads</c> on, because a comparison
        /// of bytes is condition 4 and a scan with no bytes to compare would report the safe whole-mirror pattern
        /// as a hazard.
        /// <para><paramref name="reads"/> comes from <see cref="RecordingGpuCommandList.Reads"/> and is what makes
        /// conditions 3 and 4 answerable. An EMPTY reads list makes every rewrite look safe, so a caller that
        /// wants to believe an empty answer asserts the list is non-empty first.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">A uniform upload carries no captured payload.</exception>
        internal static List<Hazard> Scan(IReadOnlyList<RecordingGpuCommandList.Upload> uploads,
            Func<IGpuBuffer, bool> isUniform, IReadOnlyList<RecordingGpuCommandList.BoundRead> reads)
        {
            ArgumentNullException.ThrowIfNull(uploads);
            ArgumentNullException.ThrowIfNull(isUniform);
            ArgumentNullException.ThrowIfNull(reads);

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
                    if (TryDifferingWindow(first, second, reads, offset, bytes, out Hazard hazard))
                        found.Add(hazard);
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
                + "RecordingGpuCommandList.CapturePayloads before recording the frame: the scan's last condition "
                + "is that the rewritten bytes DIFFER, and without the bytes it would report the engine's "
                + "deliberate whole-mirror re-upload as a hazard.");
        }

        // The intersection of the two writes' byte ranges, if any. Two writes that never touch the same byte are
        // two independent slots of one buffer, which is one SHAPE the fix for a hazard takes.
        static bool TryOverlap(in RecordingGpuCommandList.Upload a, in RecordingGpuCommandList.Upload b,
            out uint offset, out uint bytes)
        {
            uint start = Math.Max(a.Offset, b.Offset);
            uint end = Math.Min(a.Offset + a.Bytes, b.Offset + b.Bytes);
            offset = start;
            bytes = end > start ? end - start : 0u;
            return bytes > 0;
        }

        // Conditions 3 and 4 together. Walk the windows the draws recorded BETWEEN the two uploads bound on this
        // buffer, and report the first one on which the two uploads disagree. A rewrite whose differing bytes fall
        // outside every such window is the whole-mirror-per-slot pattern and is not a hazard: no draw already
        // recorded could have read a byte the second write changed.
        static bool TryDifferingWindow(in RecordingGpuCommandList.Upload first,
            in RecordingGpuCommandList.Upload second, IReadOnlyList<RecordingGpuCommandList.BoundRead> reads,
            uint offset, uint bytes, out Hazard hazard)
        {
            for (int i = 0; i < reads.Count; i++)
            {
                RecordingGpuCommandList.BoundRead read = reads[i];
                if (!ReferenceEquals(read.Buffer, first.Buffer)) continue;
                // A draw sits between the two uploads when its ordinal is at least the count of draws recorded
                // before the first upload and below the count recorded before the second.
                if (read.DrawOrdinal < first.DrawsBefore || read.DrawOrdinal >= second.DrawsBefore) continue;

                uint start = Math.Max(offset, read.Offset);
                uint end = Math.Min(offset + bytes, read.Offset + read.Bytes);
                if (end <= start) continue;
                if (SameBytes(first, second, start, end - start)) continue;

                hazard = new Hazard(first.Buffer, start, end - start, read.Offset, read.Bytes, first.DrawsBefore,
                    second.DrawsBefore);
                return true;
            }

            hazard = default;
            return false;
        }

        // Whether the two writes put the SAME bytes in a range both of them cover.
        static bool SameBytes(in RecordingGpuCommandList.Upload a, in RecordingGpuCommandList.Upload b,
            uint offset, uint bytes)
        {
            ReadOnlySpan<byte> first = a.Data!.AsSpan((int)(offset - a.Offset), (int)bytes);
            ReadOnlySpan<byte> second = b.Data!.AsSpan((int)(offset - b.Offset), (int)bytes);
            return first.SequenceEqual(second);
        }
    }
}
