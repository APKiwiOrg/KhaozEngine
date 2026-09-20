using System;
using System.Collections.Generic;
using KhaozEngine.Benchmarks.Items;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// The instrument every memory budget is read through, fenced on its own rather than only through the
/// budgets that consume it.
/// <para>
/// <b>What this pins is a SIGN, never a figure.</b> A byte-exact expectation would go red on a busy
/// runner, which is the failure mode the whole benchmark suite is written to avoid. What these facts
/// require is that retaining a known live set moves the reading UP, because the one thing a retention
/// instrument may never do is report that holding memory released it. How FAR up is not assertable in a
/// test host, where heap slack absorbs part of a retention before it shows as growth, so the only other
/// bound is a ceiling that rejects a reading which plainly came from some other collection.
/// </para>
/// <para>
/// The bug they exist for: <c>Read</c> called <c>GetGCMemoryInfo()</c> with no argument, which reports the
/// latest collection OF ANY KIND rather than the forced blocking one it had just performed. Under load a
/// background collection finishing in between meant a before and an after described two different
/// collections, and budget 12 reported a page delta of -72,525,224 bytes against a live set the builder
/// provably keeps (https://github.com/APKiwiOrg/KhaozEngine/issues/1030).
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public sealed class ResidentMemoryTests
{
    /// <summary>Sixteen mebibytes in one mebibyte blocks, big enough to dominate the surrounding noise and
    /// small enough that forcing three compacting collections around it stays cheap.</summary>
    const int BlockBytes = 1024 * 1024;
    const int BlockCount = 16;
    const long LiveBytes = (long)BlockBytes * BlockCount;

    [Fact]
    public void Retaining_a_known_live_set_moves_the_reading_up()
    {
        long before = ResidentMemory.Read();

        var held = new List<byte[]>(BlockCount);
        for (int i = 0; i < BlockCount; i++)
        {
            var block = new byte[BlockBytes];
            // Touch every page so the block cannot be an untouched reservation on any runtime.
            for (int b = 0; b < block.Length; b += 4096) block[b] = (byte)i;
            held.Add(block);
        }

        long after = ResidentMemory.Read();
        GC.KeepAlive(held);

        long delta = after - before;

        // The sign is the whole point: holding 16 MiB may never read as a release.
        Assert.True(delta > 0,
            $"retaining {LiveBytes} bytes read as a delta of {delta} bytes ({before} -> {after}), " +
            "which means the two readings did not describe the same class of collection");

        // A CEILING ONLY, and deliberately no floor. A benchmark run measured a 27,620,000 byte live set
        // at 27,800,304, inside one percent, but that is a dedicated process. Inside a test host the heap
        // carries slack that absorbs part of a retention before it shows as growth: this same 16 MiB read
        // as 6,291,976 bytes on one run here, which is honest rather than broken. So the floor is the SIGN
        // above, which is the property the bug actually violated, and the ceiling is all that is left to
        // say: a reading many times the live set did not come from the collection we forced.
        Assert.InRange(delta, 1L, LiveBytes * 8);
    }

    [Fact]
    public void Two_readings_with_nothing_retained_between_them_agree()
    {
        long first = ResidentMemory.Read();
        long second = ResidentMemory.Read();

        // Nothing was retained in between, so any movement is the instrument's own noise. Four mebibytes
        // of tolerance is far more than a settled heap drifts under a test host and far less than the tens
        // of mebibytes a mismatched collection reports.
        Assert.InRange(Math.Abs(second - first), 0L, 4L * BlockBytes);
    }
}
